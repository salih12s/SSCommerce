using System.Net;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Application.Inventory;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>
/// ATOMİKLİK ve KİLİT testleri (transaction rollback, anahtarın tüketilmemesi, kilit zaman aşımı, eşzamanlı pasifleştirme).
/// Gerçek PostgreSQL ister: rollback davranışı mock ile gösterilemez.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class StockAtomicityTests(InventoryApiFixture fixture)
{
    /// <summary>Bu test veritabanında bir backend'in kilit beklediği (wait_event_type = Lock) an'a kadar yoklar.</summary>
    private async Task WaitForLockWaiterAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Hiçbir sunucu süreci kilit beklemedi; istek engellenmedi.");
    }

    [Fact]
    public async Task When_the_movement_insert_fails_after_the_balance_update_the_balance_is_rolled_back()
    {
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 7);
        var key = InventoryApiFixture.NewKey();

        // Var olmayan aktör: bakiye UPDATE'i BAŞARILI olur, hareket INSERT'i FK (23503) ile patlar. İkinci adım ilkini geri almalı.
        var failure = await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            fixture.WithServiceAsync(s => s.ApplyStockOperationAsync(fixture.Command(warehouse, variant, -3, key, actor: Guid.NewGuid()), TestContext.Current.CancellationToken)));

        Assert.Equal("23503", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(7, await fixture.OnHandAsync(warehouse, variant));   // düşüş geri alındı
        Assert.Equal(1, await fixture.VersionAsync(warehouse, variant));  // sürüm de geri alındı
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
        Assert.Equal(0, await fixture.MovementKeyCountAsync(key));

        // Anahtar tüketilmedi: aynı anahtar geçerli aktörle uygulanır.
        var retry = await fixture.WithServiceAsync(s => s.ApplyStockOperationAsync(fixture.Command(warehouse, variant, -3, key), TestContext.Current.CancellationToken));
        Assert.Equal(StockOperationStatus.Applied, retry.Status);
        Assert.Equal(4, await fixture.OnHandAsync(warehouse, variant));
    }

    [Fact]
    public async Task When_the_movement_insert_fails_after_a_balance_insert_the_new_balance_row_is_not_left_behind()
    {
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            fixture.WithServiceAsync(s => s.ApplyStockOperationAsync(fixture.Command(warehouse, variant, 5, actor: Guid.NewGuid()), TestContext.Current.CancellationToken)));

        Assert.Null(await fixture.OnHandAsync(warehouse, variant)); // ON CONFLICT yolundaki INSERT de geri alındı
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task A_rejected_insufficient_stock_request_leaves_no_movement_and_the_key_stays_reusable()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 2);
        var key = InventoryApiFixture.NewKey();

        await ApiAssert.ProblemAsync(await admin.AdjustAsync(warehouse, variant, -5, key: key), 409, "insufficient_stock");

        Assert.Equal(2, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
        Assert.Equal(0, await fixture.MovementKeyCountAsync(key));

        // Aynı anahtar şimdi BAŞKA içerikle kullanılabilir (reddedilen istek anahtarı tüketmedi); 409 idempotency_key_reused olmaz.
        var accepted = await admin.ReceiveAsync(warehouse, variant, 3, key);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementKeyCountAsync(key));
    }

    [Fact]
    public async Task A_rejected_upper_limit_request_leaves_no_movement()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 999_999);

        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(warehouse, variant, 2), 409, "stock_limit_exceeded");

        Assert.Equal(999_999, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
        Assert.Equal(HttpStatusCode.Created, (await admin.ReceiveAsync(warehouse, variant, 1)).StatusCode); // tam sınır serbest
        Assert.Equal(1_000_000, await fixture.OnHandAsync(warehouse, variant));
    }

    [Fact]
    public async Task A_replay_is_returned_even_after_the_warehouse_was_deactivated_and_changes_nothing()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();
        var first = await admin.ReceiveAsync(warehouse, variant, 4, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await fixture.ExecuteAsync($"UPDATE warehouses SET is_active = false WHERE id = '{warehouse}'");

        var replay = await admin.ReceiveAsync(warehouse, variant, 4, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // sözleşme sırası: anahtar kontrolü depo aktifliğinden ÖNCE
        using var json = await replay.JsonAsync();
        Assert.True(json.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(4, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task A_warehouse_deactivated_concurrently_blocks_the_stock_operation_until_commit_and_then_it_is_rejected()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 5);

        // Ayrı bağlantıda açık (commit edilmemiş) bir "depoyu pasifleştir" transaction'ı.
        await using var other = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await other.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var update = new NpgsqlCommand($"UPDATE warehouses SET is_active = false WHERE id = '{warehouse}'", other, transaction))
        {
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var pending = admin.AdjustAsync(warehouse, variant, -1);
        // Sabit bekleme yok: PostgreSQL'in kendisi "bu veritabanında bir sunucu süreci kilit bekliyor" diyene kadar yoklanır (üst sınır 20 sn).
        await WaitForLockWaiterAsync();
        Assert.False(pending.IsCompleted, "Depoyu FOR SHARE okuyan stok işlemi, pasifleştirme commit edilene kadar beklemeliydi.");

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        await ApiAssert.ProblemAsync(await pending, 409, "warehouse_inactive");
        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task Lock_wait_times_out_with_503_stock_busy_writes_nothing_and_the_key_is_reusable()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();

        // Aynı anahtarın advisory kilidini dışarıdan tut (oturum düzeyinde): istek 5 sn bekleyip 55P03 almalı.
        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", holder))
        {
            acquire.Parameters.AddWithValue("key", key);
            await acquire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var blocked = await admin.ReceiveAsync(warehouse, variant, 2, key);
        stopwatch.Stop();

        await ApiAssert.ProblemAsync(blocked, 503, "stock_busy");
        // Alt sınır: kilit zaman aşımı GERÇEKTEN tetiklendi (hemen reddedilmedi). Üst sınır yalnız "sonsuza dek beklemedi" der; CI yavaşlığında
        // flaky olmasın diye cömert (30 sn).
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.0, 30.0);
        Assert.Null(await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", holder))
        {
            release.Parameters.AddWithValue("key", key);
            await release.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var retry = await admin.ReceiveAsync(warehouse, variant, 2, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode); // 503 anahtarı tüketmedi
        Assert.Equal(2, await fixture.OnHandAsync(warehouse, variant));
    }
}

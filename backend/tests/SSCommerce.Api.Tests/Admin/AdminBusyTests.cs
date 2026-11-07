using System.Diagnostics;
using System.Net;
using Npgsql;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// INTEGRATION testleri: kilit zaman aşımı ve deadlock durumu 500 değil 503 <c>busy</c> olur (hiçbir şey yazılmaz, istek güvenle tekrarlanır).
/// Gerçek kilit çatışması için ayrı bir bağlantı satırı kilitli tutar. Neden mock değil: 55P03/40P01'i yalnız gerçek PostgreSQL üretir.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AdminBusyTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task A_catalog_write_that_waits_longer_than_the_lock_timeout_is_503_busy_changes_nothing_and_succeeds_after_the_lock_is_released()
    {
        var admin = await fixture.AdminAsync();
        var (category, version, _) = await admin.NewCategoryAsync(name: "Kilitli Kategori");
        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var lockRow = new NpgsqlCommand($"SELECT id FROM categories WHERE id = '{category}' FOR UPDATE", holder, transaction))
        {
            await lockRow.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var clock = Stopwatch.StartNew();
        var blocked = await admin.PatchAsync(AdminKit.Url($"/categories/{category}"), new { version, name = "Beklemeli" });
        clock.Stop();

        await ApiAssert.ProblemAsync(blocked, 503, "busy");
        Assert.InRange(clock.Elapsed.TotalSeconds, 4.0, 14.0); // 5 sn kilit zaman aşımı
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Kilitli Kategori", await fixture.ScalarAsync<string>($"SELECT name FROM categories WHERE id = '{category}'"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync(AdminKit.Url($"/categories/{category}"), new { version, name = "Artık serbest" })).StatusCode);
    }

    [Fact]
    public async Task A_real_deadlock_between_the_category_tree_lock_and_a_category_row_lock_never_becomes_a_500()
    {
        var admin = await fixture.AdminAsync();
        var (category, version, _) = await admin.NewCategoryAsync(name: "Çıkmaz Kategori");
        await using var other = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await other.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var lockRow = new NpgsqlCommand($"SELECT id FROM categories WHERE id = '{category}' FOR UPDATE", other, transaction))
        {
            await lockRow.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // İstek: advisory kilidi alır (sözleşme sırası: advisory -> satır), sonra satır kilidini bekler (diğer bağlantıda).
        var request = admin.PatchAsync(AdminKit.Url($"/categories/{category}"), new { version, name = "Çıkmaz" });
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Diğer bağlantı ters sırayla (satır -> advisory) ilerler: döngü. PostgreSQL ikisinden birini 40P01 ile keser.
        string? otherOutcome;
        try
        {
            await using var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('catalog:category-tree', 0))", other, transaction);
            await advisory.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            otherOutcome = "completed";
        }
        catch (PostgresException exception)
        {
            otherOutcome = exception.SqlState;
        }

        try
        {
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // İşlem zaten iptal edilmiş olabilir.
        }

        var response = await request;
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        if (otherOutcome == PostgresErrorCodes.DeadlockDetected)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // diğer taraf kesildi, istek devam etti
        }
        else
        {
            await ApiAssert.ProblemAsync(response, 503, "busy"); // istek kesildi: deadlock 503 busy olur
        }
    }
}

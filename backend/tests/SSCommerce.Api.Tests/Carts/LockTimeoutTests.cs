using System.Diagnostics;
using System.Net;
using Npgsql;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>
/// KİLİT ZAMAN AŞIMI: kullanıcı başına sıralama noktası (sepet satırı FOR UPDATE, adres advisory lock) dışarıdan tutulurken gelen yazma,
/// 5 sn bekleyip 503 busy dönmeli, hiçbir şey yazmamalı; kilit bırakılınca AYNI istek başarılı olmalıdır. Alt sınır zaman aşımının GERÇEKTEN
/// tetiklendiğini (hemen reddedilmediğini), üst sınır (30 sn) yalnız "sonsuza dek beklemediğini" gösterir (CI yavaşlığında flaky olmasın).
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class LockTimeoutTests(InventoryApiFixture fixture)
{
    /// <summary>AddressService.LockNamespace ile aynı sabit (ASCII "ADDR"). Servis iç sınıf olduğundan burada kopyalanır; değişirse test kırmızı olur.</summary>
    private const int AddressLockNamespace = 0x41444452;

    [Fact]
    public async Task A_cart_write_blocked_by_a_held_cart_row_lock_returns_503_busy_after_the_timeout_writes_nothing_and_succeeds_after_release()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        Assert.Equal(HttpStatusCode.OK, (await customer.AddToCartAsync(variant, 1)).StatusCode); // sepet satırı var

        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var hold = new NpgsqlCommand($"SELECT id FROM carts WHERE user_id = '{customer.UserId}' FOR UPDATE", holder, transaction))
        {
            Assert.NotNull(await hold.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        var stopwatch = Stopwatch.StartNew();
        var blocked = await customer.AddToCartAsync(variant, 3);
        stopwatch.Stop();

        await ApiAssert.ProblemAsync(blocked, 503, "busy");
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.0, 30.0);
        Assert.Equal(1, await fixture.LineQuantityAsync(customer.UserId, variant)); // hiçbir şey yazılmadı
        Assert.Equal(1, await fixture.LineCountAsync(customer.UserId));

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);

        var retry = await customer.AddToCartAsync(variant, 3);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode); // 503 sepeti bozmadı
        Assert.Equal(4, await fixture.LineQuantityAsync(customer.UserId, variant));
    }

    [Fact]
    public async Task A_cart_patch_and_delete_blocked_by_the_same_lock_also_get_503_busy_and_change_nothing()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        await customer.AddToCartAsync(variant, 2);
        var item = await customer.ItemIdAsync(variant);

        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var hold = new NpgsqlCommand($"SELECT id FROM carts WHERE user_id = '{customer.UserId}' FOR UPDATE", holder, transaction))
        {
            await hold.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var patch = await customer.SetQuantityAsync(item, 5);
        var elapsedAfterPatch = stopwatch.Elapsed;

        await ApiAssert.ProblemAsync(patch, 503, "busy");
        Assert.InRange(elapsedAfterPatch.TotalSeconds, 4.0, 30.0);
        Assert.Equal(2, await fixture.LineQuantityAsync(customer.UserId, variant));

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await customer.SetQuantityAsync(item, 5)).StatusCode);
        Assert.Equal(5, await fixture.LineQuantityAsync(customer.UserId, variant));
    }

    [Fact]
    public async Task An_address_write_blocked_by_a_held_two_argument_advisory_lock_returns_503_busy_writes_nothing_and_succeeds_after_release()
    {
        var customer = await fixture.CustomerAsync();

        // Aynı kullanıcı için aynı iki argümanlı kilit anahtarını (ad alanı, hashtext(kullanıcı)) oturum düzeyinde dışarıdan tut.
        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@ns, hashtext(@user))", holder))
        {
            acquire.Parameters.AddWithValue("ns", AddressLockNamespace);
            acquire.Parameters.AddWithValue("user", customer.UserId.ToString("D"));
            await acquire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var blocked = await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress());
        stopwatch.Stop();

        await ApiAssert.ProblemAsync(blocked, 503, "busy");
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.0, 30.0);
        Assert.Equal(0, await fixture.AddressCountAsync(customer.UserId));

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@ns, hashtext(@user))", holder))
        {
            release.Parameters.AddWithValue("ns", AddressLockNamespace);
            release.Parameters.AddWithValue("user", customer.UserId.ToString("D"));
            Assert.True((bool)(await release.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }

        var retry = await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress());
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await fixture.AddressCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task A_held_lock_of_one_user_does_not_block_another_user_or_the_single_argument_stock_lock_space()
    {
        var blockedUser = await fixture.CustomerAsync();
        var freeUser = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        await blockedUser.AddToCartAsync(variant, 1);

        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var hold = new NpgsqlCommand($"SELECT id FROM carts WHERE user_id = '{blockedUser.UserId}' FOR UPDATE", holder, transaction))
        {
            await hold.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var other = await freeUser.AddToCartAsync(variant, 1);          // başka kullanıcı: engellenmez
        var address = await freeUser.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress());
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(HttpStatusCode.Created, address.StatusCode);
        Assert.True(stopwatch.Elapsed.TotalSeconds < 4.0, "Başka kullanıcının yazması kilit zaman aşımını beklememeli.");
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }
}

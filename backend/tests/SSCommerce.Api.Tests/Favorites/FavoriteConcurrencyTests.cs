using System.Diagnostics;
using System.Net;
using Npgsql;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Favorites;

/// <summary>
/// FAVORİ YARIŞ testleri: gerçek paralel HTTP, gerçek PostgreSQL. Ekleme kullanıcı başına iki argümanlı advisory lock ile sıraya girer; kilit olmasa
/// "say, sonra ekle" iki paralel istekte ikisi de 99 görüp 101. favoriyi yazardı (check-then-act yarışı). Testler "bir kez deneyip geçti" değildir:
/// başlangıç kapısı (gate) tüm istekleri aynı anda bırakır ve yarış turları tekrarlanır. Bu testler mock'la YAZILAMAZ: yarışı yalnız gerçek DB gösterir.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class FavoriteConcurrencyTests(InventoryApiFixture fixture)
{
    /// <summary>FavoriteService.LockNamespace ile aynı sabit (ASCII "FAVS"). Servis iç sınıf olduğundan kopyalanır; değişirse test kırmızı olur.</summary>
    private const int FavoriteLockNamespace = 0x46415653;

    private static async Task<List<(HttpStatusCode Status, string Body)>> RunParallelAsync(int count, Func<int, Task<HttpResponseMessage>> send)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.Task;
            return await send(i);
        }).ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(tasks);
        var results = new List<(HttpStatusCode, string)>();
        foreach (var response in responses)
        {
            results.Add((response.StatusCode, await response.TextAsync()));
        }

        return results;
    }

    private static string CodeOf(string body)
    {
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task Two_parallel_puts_of_the_same_product_both_return_204_and_leave_one_row_repeated_for_many_rounds()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(12);

        foreach (var product in products) // her tur yeni bir ürün: yarış her turda sıfırdan başlar
        {
            var results = await RunParallelAsync(2, _ => customer.AddFavoriteAsync(product));

            Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.Status));  // benzersizlik ihlali 500 olmaz
            Assert.Equal(1, await fixture.FavoriteRowCountAsync(customer.UserId, product));
        }

        Assert.Equal(12, await fixture.FavoriteCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Many_parallel_puts_of_the_same_product_are_all_204_with_a_single_row()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];

        var results = await RunParallelAsync(16, _ => customer.AddFavoriteAsync(product));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.Status));
        Assert.Equal(1, await fixture.FavoriteRowCountAsync(customer.UserId, product));
    }

    [Fact]
    public async Task Five_parallel_puts_of_different_products_at_99_favorites_admit_exactly_one_and_reject_four_with_409()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(104);
        await fixture.SeedFavoritesAsync(customer.UserId, products.Take(99));
        var candidates = products.Skip(99).ToArray(); // 5 yeni ürün

        var results = await RunParallelAsync(5, i => customer.AddFavoriteAsync(candidates[i]));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.NoContent));
        var rejected = results.Where(r => r.Status != HttpStatusCode.NoContent).ToList();
        Assert.Equal(4, rejected.Count);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("favorite_limit", CodeOf(r.Body));
        });
        Assert.Equal(100, await fixture.FavoriteCountAsync(customer.UserId)); // sınır aşılmadı
        var winners = await Task.WhenAll(candidates.Select(c => fixture.FavoriteRowCountAsync(customer.UserId, c)));
        Assert.Equal(1, winners.Sum());
    }

    [Fact]
    public async Task Twelve_parallel_puts_with_five_free_slots_admit_exactly_five()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(107);
        await fixture.SeedFavoritesAsync(customer.UserId, products.Take(95));
        var candidates = products.Skip(95).ToArray(); // 12 yeni ürün, 5 boş yer

        var results = await RunParallelAsync(12, i => customer.AddFavoriteAsync(candidates[i]));

        Assert.Equal(5, results.Count(r => r.Status == HttpStatusCode.NoContent));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Conflict && CodeOf(r.Body) == "favorite_limit"));
        Assert.Equal(100, await fixture.FavoriteCountAsync(customer.UserId));
    }

    [Fact]
    public async Task At_99_a_mix_of_duplicate_and_distinct_puts_never_exceeds_100_and_never_fails_with_500()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(102);
        await fixture.SeedFavoritesAsync(customer.UserId, products.Take(99));
        var first = products[99];
        var second = products[100];
        var existing = products[0];

        // Aynı yeni ürün 3 kez + başka yeni ürün 2 kez + mevcut ürün 2 kez, hepsi aynı anda.
        var targets = new[] { first, first, first, second, second, existing, existing };
        var results = await RunParallelAsync(targets.Length, i => customer.AddFavoriteAsync(targets[i]));

        Assert.All(results, r => Assert.True(
            r.Status is HttpStatusCode.NoContent or HttpStatusCode.Conflict, $"beklenmeyen durum: {r.Status}"));
        Assert.Equal(100, await fixture.FavoriteCountAsync(customer.UserId));
        // Tek boş yeri kim aldıysa onun TÜM istekleri 204; diğerinin tüm istekleri 409. İkisi birden veya hiçbiri olamaz.
        var firstRows = await fixture.FavoriteRowCountAsync(customer.UserId, first);
        var secondRows = await fixture.FavoriteRowCountAsync(customer.UserId, second);
        Assert.Equal(1, firstRows + secondRows);
        Assert.Equal(firstRows == 1 ? 3 : 0, results.Where((_, i) => targets[i] == first).Count(r => r.Status == HttpStatusCode.NoContent));
        Assert.Equal(secondRows == 1 ? 2 : 0, results.Where((_, i) => targets[i] == second).Count(r => r.Status == HttpStatusCode.NoContent));
        Assert.All(results.Where((_, i) => targets[i] == existing), r => Assert.Equal(HttpStatusCode.NoContent, r.Status));
    }

    [Fact]
    public async Task Parallel_puts_and_deletes_of_the_same_product_never_fail_and_leave_a_consistent_state()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];

        var results = await RunParallelAsync(20, i => i % 2 == 0 ? customer.AddFavoriteAsync(product) : customer.RemoveFavoriteAsync(product));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.Status));
        Assert.InRange(await fixture.FavoriteRowCountAsync(customer.UserId, product), 0, 1);
        // Son durum tutarlı: ids ucu ile satır sayısı aynı şeyi söyler.
        Assert.Equal(await fixture.FavoriteRowCountAsync(customer.UserId, product), (await customer.FavoriteIdsAsync()).Length);
    }

    [Fact]
    public async Task Writes_of_different_users_do_not_wait_for_each_other_and_do_not_mix()
    {
        var users = new List<ApiClient>();
        for (var i = 0; i < 6; i++)
        {
            users.Add(await fixture.CustomerAsync());
        }

        var products = await fixture.CreateProductsAsync(4);

        var results = await RunParallelAsync(6 * 4, i => users[i % 6].AddFavoriteAsync(products[i / 6]));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.Status));
        foreach (var user in users)
        {
            Assert.Equal(4, await fixture.FavoriteCountAsync(user.UserId));
        }
    }

    // ---------- Kilidin kendisi ----------

    [Fact]
    public async Task A_put_waits_on_the_per_user_lock_and_completes_only_after_it_is_released()
    {
        var customer = await fixture.CustomerAsync();
        var other = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(2);

        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns, hashtext(@user))", holder, transaction))
        {
            acquire.Parameters.AddWithValue("ns", FavoriteLockNamespace);
            acquire.Parameters.AddWithValue("user", customer.UserId.ToString("D"));
            await acquire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Kilit tutulurken: başka kullanıcı etkilenmez (kilit kullanıcı başına, global değil)...
        Assert.Equal(HttpStatusCode.NoContent, (await other.AddFavoriteAsync(products[0])).StatusCode);
        // ...ve aynı kullanıcının ADRES yazması da etkilenmez: favori ad alanı adres ad alanından ayrıdır.
        Assert.Equal(HttpStatusCode.Created, (await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress("Ev"))).StatusCode);

        // Kilit zaman aşımı 5 sn: ısınma istekleri bittikten SONRA başlatılır, bekleme süresi kısa tutulur.
        var blocked = customer.AddFavoriteAsync(products[0]);
        await Task.Delay(750, TestContext.Current.CancellationToken);
        Assert.False(blocked.IsCompleted, "PUT kullanıcı kilidi tutulurken tamamlanmamalıydı.");
        Assert.Equal(0, await fixture.FavoriteCountAsync(customer.UserId));

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, (await blocked).StatusCode);
        Assert.Equal(1, await fixture.FavoriteCountAsync(customer.UserId));
    }

    [Fact]
    public async Task A_put_blocked_by_the_held_lock_returns_503_busy_after_the_timeout_writes_nothing_and_succeeds_after_release()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];

        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@ns, hashtext(@user))", holder))
        {
            acquire.Parameters.AddWithValue("ns", FavoriteLockNamespace);
            acquire.Parameters.AddWithValue("user", customer.UserId.ToString("D"));
            await acquire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var blocked = await customer.AddFavoriteAsync(product);
        stopwatch.Stop();

        await ApiAssert.ProblemAsync(blocked, 503, "busy");
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.0, 30.0);   // zaman aşımı gerçekten tetiklendi, sonsuza dek beklenmedi
        Assert.Equal(0, await fixture.FavoriteCountAsync(customer.UserId));

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@ns, hashtext(@user))", holder))
        {
            release.Parameters.AddWithValue("ns", FavoriteLockNamespace);
            release.Parameters.AddWithValue("user", customer.UserId.ToString("D"));
            await release.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await customer.AddFavoriteAsync(product)).StatusCode); // 503 hiçbir şeyi bozmadı
        Assert.Equal(1, await fixture.FavoriteCountAsync(customer.UserId));
    }
}

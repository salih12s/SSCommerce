using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>
/// YARIŞ (race condition) testleri: GERÇEK PostgreSQL + GERÇEK paralel HTTP istekleri. Mock ile kanıtlanamaz, çünkü kanıtlanan şey
/// veritabanının kilit/koşullu UPDATE davranışıdır. Her test kendi deposunu ve varyantını kurar. İstekler bir "başlangıç kapısında"
/// toplanıp aynı anda bırakılır (çakışma olasılığını artırmak için).
/// Her testten sonra aynı üç şey doğrulanır: HTTP sonuçları, bakiye (inventory_balances) ve defter (stock_movements) birbiriyle tutarlı.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class StockConcurrencyTests(InventoryApiFixture fixture)
{
    private static async Task<List<(HttpStatusCode Status, JsonDocument Body)>> RunParallelAsync(
        int count,
        Func<int, Task<HttpResponseMessage>> send)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.Task;
            return await send(i);
        }).ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(tasks);

        var results = new List<(HttpStatusCode, JsonDocument)>();
        foreach (var response in responses)
        {
            results.Add((response.StatusCode, await response.JsonAsync()));
        }

        return results;
    }

    private static string Code(JsonDocument body) => body.RootElement.GetProperty("code").GetString()!;

    [Fact]
    public async Task One_unit_of_stock_and_20_parallel_decrements_succeed_exactly_once()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 1);

        var results = await RunParallelAsync(20, _ => admin.AdjustAsync(warehouse, variant, -1));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        var rejected = results.Where(r => r.Status != HttpStatusCode.Created).ToList();
        Assert.Equal(19, rejected.Count);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("insufficient_stock", Code(r.Body));
        });
        Assert.Equal(0, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
        Assert.Equal(-1, await fixture.MovementDeltaSumAsync(variant));
        Assert.Equal(2, await fixture.VersionAsync(warehouse, variant)); // tohum 1 + tek başarılı yazma
    }

    [Fact]
    public async Task Ten_units_and_25_parallel_decrements_succeed_exactly_ten_times_and_never_oversell()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 10);

        var results = await RunParallelAsync(25, _ => admin.AdjustAsync(warehouse, variant, -1));

        var created = results.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.Equal(10, created.Count);
        Assert.Equal(15, results.Count(r => r.Status == HttpStatusCode.Conflict && Code(r.Body) == "insufficient_stock"));
        Assert.Equal(0, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(10, await fixture.MovementCountAsync(variant));
        Assert.Equal(-10, await fixture.MovementDeltaSumAsync(variant));

        // Her başarılı işlem farklı bir "sonrası" değer görmeli: 9,8,...,0. İki istek aynı birimi tüketseydi bir değer tekrar ederdi.
        var after = created.Select(r => r.Body.RootElement.GetProperty("onHandAfter").GetInt32()).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 10).ToArray(), after);
    }

    [Fact]
    public async Task Same_key_and_same_payload_sent_20_times_in_parallel_moves_the_balance_once()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();

        // Bakiye satırı hiç yok: tekrarlar INSERT ... ON CONFLICT yolunu da yarıştırır.
        var results = await RunParallelAsync(20, _ => admin.ReceiveAsync(warehouse, variant, 5, key));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(19, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK));

        var created = results.Single(r => r.Status == HttpStatusCode.Created).Body.RootElement;
        Assert.False(created.GetProperty("replayed").GetBoolean());
        var replays = results.Where(r => r.Status == HttpStatusCode.OK).Select(r => r.Body.RootElement).ToList();
        Assert.All(replays, replay =>
        {
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(created.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid()); // ilk sonuç, yeni hareket değil
            Assert.Equal(5, replay.GetProperty("onHandAfter").GetInt32());
        });

        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.VersionAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
        Assert.Equal(1, await fixture.MovementKeyCountAsync(key));
    }

    [Fact]
    public async Task Same_key_with_a_different_payload_is_rejected_and_leaves_the_balance_unchanged()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();

        var first = await admin.ReceiveAsync(warehouse, variant, 5, key);
        var second = await admin.ReceiveAsync(warehouse, variant, 6, key); // aynı anahtar, farklı miktar

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await ApiAssert.ProblemAsync(second, 409, "idempotency_key_reused");
        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));

        // Farkın her bileşeni parmak izine girer: neden, referans, tür ve uç da farklı istek sayılır.
        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(warehouse, variant, 5, key, reason: "Başka neden"), 409, "idempotency_key_reused");
        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(warehouse, variant, 5, key, reference: "PO-1"), 409, "idempotency_key_reused");
        await ApiAssert.ProblemAsync(await admin.AdjustAsync(warehouse, variant, 5, "adjustment", key), 409, "idempotency_key_reused");
        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
    }

    [Fact]
    public async Task Same_key_with_two_different_payloads_in_parallel_lets_exactly_one_win()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();

        var results = await RunParallelAsync(10, i => admin.ReceiveAsync(warehouse, variant, 1 + (i % 2), key)); // 5 × miktar 1, 5 × miktar 2

        var winners = results.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.Single(winners);
        var winningQuantity = winners[0].Body.RootElement.GetProperty("quantityDelta").GetInt32();
        // Kazananla aynı içerikliler 200 (replay), farklı içerikliler 409 idempotency_key_reused.
        Assert.Equal(4, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.Status == HttpStatusCode.Conflict && Code(r.Body) == "idempotency_key_reused"));
        Assert.Equal(winningQuantity, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task Parallel_receives_into_a_missing_balance_row_create_it_once_and_lose_no_unit()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var results = await RunParallelAsync(20, _ => admin.ReceiveAsync(warehouse, variant, 3));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Equal(60, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(20, await fixture.VersionAsync(warehouse, variant)); // satır bir kez eklendi (1), 19 kez artırıldı
        Assert.Equal(20, await fixture.MovementCountAsync(variant));
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM inventory_balances WHERE warehouse_id = '{warehouse}' AND product_variant_id = '{variant}'"));
    }

    [Fact]
    public async Task Mixed_parallel_increases_and_decreases_keep_balance_and_ledger_consistent()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        const int initial = 30;
        await fixture.SeedBalanceAsync(warehouse, variant, initial);

        // Sabit tohumlu (tekrarlanabilir) karışık işlemler: 20 artış (+1..+5) ve 20 azalış (-1..-9).
        var random = new Random(20261101);
        var deltas = Enumerable.Range(0, 20).Select(_ => random.Next(1, 6))
            .Concat(Enumerable.Range(0, 20).Select(_ => -random.Next(1, 10)))
            .OrderBy(_ => random.Next())
            .ToArray();

        var results = await RunParallelAsync(deltas.Length, i => deltas[i] > 0
            ? admin.ReceiveAsync(warehouse, variant, deltas[i])
            : admin.AdjustAsync(warehouse, variant, deltas[i]));

        var succeeded = results.Select((r, i) => (r, delta: deltas[i])).Where(x => x.r.Status == HttpStatusCode.Created).ToList();
        var rejected = results.Where(r => r.Status != HttpStatusCode.Created).ToList();
        Assert.All(rejected, r => Assert.Equal("insufficient_stock", Code(r.Body))); // artışlar üst sınıra (1.000.000) yaklaşmaz
        Assert.NotEmpty(succeeded);

        var finalOnHand = await fixture.OnHandAsync(warehouse, variant);
        var successfulSum = succeeded.Sum(x => x.delta);
        Assert.Equal(initial + successfulSum, finalOnHand);                                  // bakiye = başlangıç + başarılıların toplamı
        Assert.Equal(successfulSum, await fixture.MovementDeltaSumAsync(variant));          // defter toplamı aynı sayıyı verir
        Assert.Equal(succeeded.Count, await fixture.MovementCountAsync(variant));           // her başarı tam bir hareket, hata hareket bırakmaz
        Assert.Equal(1 + succeeded.Count, await fixture.VersionAsync(warehouse, variant));  // her başarılı yazma sürümü tam bir artırır
        Assert.True(finalOnHand >= 0);

        // Hiçbir ara değer negatife düşmedi ve her "sonrası" değeri, kendi deltasıyla başka bir "sonrası"ya (ya da başlangıca) bağlanır.
        var afterValues = succeeded.Select(x => x.r.Body.RootElement.GetProperty("onHandAfter").GetInt32()).ToList();
        Assert.All(afterValues, value => Assert.True(value >= 0));
        var reachable = new HashSet<int>(afterValues) { initial };
        Assert.All(succeeded, x => Assert.Contains(x.r.Body.RootElement.GetProperty("onHandAfter").GetInt32() - x.delta, reachable));
    }
}

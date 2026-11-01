using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>
/// SEPET YARIŞ testleri: aynı kullanıcının gerçek paralel HTTP istekleri, gerçek PostgreSQL. Kanıtlanan şey kullanıcı başına
/// <c>SELECT … FOR UPDATE</c> sıralamasıdır (tek sepet, tek satır, adet/satır sınırları aşılmaz, toplam tutarlı). Kilit olmasa
/// okuma-değiştirme-yazma güncellemeleri birbirini ezerdi.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class CartConcurrencyTests(InventoryApiFixture fixture)
{
    private static async Task<List<(HttpStatusCode Status, JsonDocument Body)>> RunParallelAsync(int count, Func<int, Task<HttpResponseMessage>> send)
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
    public async Task Twenty_parallel_plus_one_adds_of_the_same_variant_make_one_row_with_quantity_twenty()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(50);

        var results = await RunParallelAsync(20, _ => customer.AddToCartAsync(variant));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, await fixture.LineCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.CartCountAsync(customer.UserId));
        Assert.Equal(20, await fixture.LineQuantityAsync(customer.UserId, variant));
        // Her başarılı yanıt FARKLI bir toplam gördü: 1..20 (bir artış başka bir artışı ezseydi tekrar ederdi).
        Assert.Equal(
            Enumerable.Range(1, 20).ToArray(),
            results.Select(r => r.Body.RootElement.GetProperty("itemCount").GetInt32()).Order().ToArray());
    }

    [Fact]
    public async Task Parallel_first_adds_of_different_variants_create_exactly_one_cart()
    {
        var customer = await fixture.CustomerAsync();
        var variants = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            variants.Add((await fixture.StockedVariantAsync(5)).VariantId);
        }

        var results = await RunParallelAsync(12, i => customer.AddToCartAsync(variants[i]));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, await fixture.CartCountAsync(customer.UserId));   // ux_carts_user_id + ON CONFLICT DO NOTHING
        Assert.Equal(12, await fixture.LineCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Twenty_five_parallel_adds_of_one_unit_never_exceed_the_line_cap_and_the_rest_get_409()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(50);

        var results = await RunParallelAsync(25, _ => customer.AddToCartAsync(variant));

        Assert.Equal(20, results.Count(r => r.Status == HttpStatusCode.OK));
        var rejected = results.Where(r => r.Status != HttpStatusCode.OK).ToList();
        Assert.Equal(5, rejected.Count);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("cart_quantity_limit", Code(r.Body));
        });
        Assert.Equal(20, await fixture.LineQuantityAsync(customer.UserId, variant));
        Assert.Equal(1, await fixture.LineCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Parallel_adds_beyond_the_stock_never_exceed_it()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(7);

        var results = await RunParallelAsync(15, _ => customer.AddToCartAsync(variant));

        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Equal("insufficient_stock", Code(r.Body)));
        Assert.Equal(7, await fixture.LineQuantityAsync(customer.UserId, variant));
    }

    [Fact]
    public async Task Sixty_parallel_adds_of_distinct_variants_cannot_exceed_fifty_lines()
    {
        var customer = await fixture.CustomerAsync();
        var product = await fixture.CreateProductAsync(Enumerable.Repeat(true, 60).ToList());
        var warehouse = await fixture.CreateWarehouseAsync();
        foreach (var (id, _) in product.Variants)
        {
            await fixture.SeedBalanceAsync(warehouse, id, 5);
        }

        var results = await RunParallelAsync(60, i => customer.AddToCartAsync(product.Variants[i].Id));

        Assert.Equal(50, results.Count(r => r.Status == HttpStatusCode.OK));
        var rejected = results.Where(r => r.Status != HttpStatusCode.OK).ToList();
        Assert.Equal(10, rejected.Count);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("cart_line_limit", Code(r.Body));
        });
        Assert.Equal(50, await fixture.LineCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.CartCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Parallel_patch_add_and_delete_on_one_line_leave_a_consistent_row_and_subtotal()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(50);
        var (anchor, _, _) = await fixture.StockedVariantAsync(50);
        await customer.AddToCartAsync(anchor, 2);
        await customer.AddToCartAsync(variant, 5);
        var itemId = await customer.ItemIdAsync(variant);

        // 30 karışık işlem: i%3==0 PATCH (mutlak 1..19), 1 ekleme (+1), 2 silme. Kullanıcı kilidi hepsini sıraya sokar.
        string Kind(int i) => (i % 3) switch { 0 => "patch", 1 => "add", _ => "delete" };
        var results = await RunParallelAsync(30, i => (i % 3) switch
        {
            0 => customer.SetQuantityAsync(itemId, 1 + (i % 19)),
            1 => customer.AddToCartAsync(variant),
            _ => customer.RemoveItemAsync(itemId),
        });

        // (1) 500 yok; 409 yalnız ekleme satır sınırını (20) aşınca; 404 yalnız silinmiş (eski kimlikli) satıra PATCH/DELETE.
        Assert.All(results.Select((r, i) => (r, kind: Kind(i))), x =>
        {
            Assert.True(x.r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.NotFound, $"beklenmeyen durum: {x.r.Status}");
            if (x.r.Status == HttpStatusCode.Conflict)
            {
                Assert.Equal("add", x.kind);
                Assert.Equal("cart_quantity_limit", Code(x.r.Body));
            }

            if (x.r.Status == HttpStatusCode.NotFound)
            {
                Assert.NotEqual("add", x.kind);
                Assert.Equal("cart_item_not_found", Code(x.r.Body));
            }
        });

        // (2) Ekleme hiç 404 vermez ve silinen satırı YENİ kimlikle yeniden yaratır; eski kimlik bir kez silindi mi bir daha dönmez:
        //     10 silme isteğinden TAM BİRİ 200 alır (ilk sıradaki), kalan dokuzu 404.
        var deletes = results.Where((_, i) => Kind(i) == "delete").ToList();
        Assert.Equal(1, deletes.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(9, deletes.Count(r => r.Status == HttpStatusCode.NotFound));

        // (3) Her başarılı yanıt, o anın TUTARLI bir anlık görüntüsüdür: çapa satırı bozulmadı ve itemCount/ara toplam satırlarla birebir uyumlu.
        var observed = new HashSet<int?>();
        foreach (var (status, body) in results.Where(r => r.Status == HttpStatusCode.OK))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(items.Sum(x => x.GetProperty("quantity").GetInt32()), body.RootElement.GetProperty("itemCount").GetInt32());
            Assert.Equal(items.Sum(x => x.GetProperty("lineSubtotalMinor").GetInt64()), body.RootElement.GetProperty("subtotalMinor").GetInt64());
            Assert.Equal(2, items.Single(x => x.GetProperty("variantId").GetGuid() == anchor).GetProperty("quantity").GetInt32());
            var line = items.Where(x => x.GetProperty("variantId").GetGuid() == variant).Select(x => (int?)x.GetProperty("quantity").GetInt32()).SingleOrDefault();
            Assert.True(line is null or (>= 1 and <= 20), $"geçersiz adet: {line}");
            observed.Add(line);
            _ = status;
        }

        // (4) Son durum, kilit sırasındaki SON işlemin görüntüsüdür: yanıtlarda görülen durumlardan biri olmalı (ya da satır yok).
        var finalQuantity = await fixture.LineQuantityAsync(customer.UserId, variant);
        Assert.Contains(finalQuantity is null ? (int?)null : (int)finalQuantity.Value, observed);
        Assert.Equal(1 + (finalQuantity is null ? 0 : 1), await fixture.LineCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.CartCountAsync(customer.UserId));
        using var cart = await (await customer.GetCartAsync()).JsonAsync();
        var expectedItems = 2 + (int)(finalQuantity ?? 0);
        Assert.Equal(expectedItems, cart.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(expectedItems * 10_000L, cart.RootElement.GetProperty("subtotalMinor").GetInt64());
    }

    [Fact]
    public async Task Different_users_do_not_block_each_other_or_leak_into_each_others_carts()
    {
        var users = new List<ApiClient>();
        for (var i = 0; i < 8; i++)
        {
            users.Add(await fixture.CustomerAsync());
        }

        var (variant, _, _) = await fixture.StockedVariantAsync(100);

        var results = await RunParallelAsync(8 * 3, i => users[i % 8].AddToCartAsync(variant, 2));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        foreach (var user in users)
        {
            Assert.Equal(6, await fixture.LineQuantityAsync(user.UserId, variant));
            Assert.Equal(1, await fixture.CartCountAsync(user.UserId));
        }
    }
}

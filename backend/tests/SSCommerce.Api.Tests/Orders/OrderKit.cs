using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>Bir müşterinin sepeti, adresi ve geçerli quote'u. Sepet satırları HTTP üzerinden eklenir (gerçek yol).</summary>
internal sealed record Shopper(ApiClient Client, Guid AddressId, string Quote)
{
    public Guid UserId => Client.UserId;

    public Task<HttpResponseMessage> PlaceAsync(string? key = null) => Client.PlaceAsync(AddressId, Quote, key);
}

/// <summary>Sipariş testleri için küçük yardımcılar: istemci kısayolları, veri kurucular ve "hiçbir şey yazılmadı" anlık görüntüsü (her test kendi verisini kurar).</summary>
internal static class OrderKit
{
    public const string OrdersUrl = "/api/v1/orders";
    public const string StaffUrl = "/api/v1/admin/orders";

    public static string NewKey() => $"ok-{Guid.NewGuid():N}";

    // ---------- İstemci kısayolları ----------

    /// <summary>Checkout önizlemesinden sunucunun quote değerini alır (önizleme ilerlenebilirse).</summary>
    public static async Task<string> QuoteAsync(this ApiClient client, Guid addressId)
    {
        var response = await client.PreviewAsync(addressId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.True(json.RootElement.GetProperty("canProceed").GetBoolean());
        return json.RootElement.GetProperty("quote").GetString()!;
    }

    public static Task<HttpResponseMessage> PlaceAsync(this ApiClient client, Guid addressId, string quote, string? key = null) =>
        client.PostAsync(OrdersUrl, new { addressId, quote }, key ?? NewKey());

    public static Task<HttpResponseMessage> CancelAsync(this ApiClient client, Guid orderId) =>
        client.PostAsync($"{OrdersUrl}/{orderId}/cancel", null);

    public static Task<HttpResponseMessage> GetOrderAsync(this ApiClient client, Guid orderId) => client.GetAsync($"{OrdersUrl}/{orderId}");

    public static Task<HttpResponseMessage> StaffStatusAsync(this ApiClient client, Guid orderId, string status, string? reason = null) =>
        client.PostAsync($"{StaffUrl}/{orderId}/status", new { status, reason });

    public static async Task<Guid> IdOfAsync(this HttpResponseMessage response)
    {
        using var json = await response.JsonAsync();
        return json.RootElement.GetProperty("id").GetGuid();
    }

    public static async Task<(HttpStatusCode Status, JsonDocument Body)> ReadAsync(this HttpResponseMessage response) =>
        (response.StatusCode, await response.JsonAsync());

    public static string Code(JsonDocument body) => body.RootElement.GetProperty("code").GetString()!;

    /// <summary>Aynı anda başlayan <paramref name="count"/> gerçek paralel istek (başlangıç kapısı bütün görevleri aynı anda serbest bırakır).</summary>
    public static async Task<List<(HttpStatusCode Status, JsonDocument Body)>> RunParallelAsync(int count, Func<int, Task<HttpResponseMessage>> send)
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

    // ---------- Veri kurucular ----------

    public static async Task<Shopper> ShopperAsync(
        this InventoryApiFixture fixture,
        IReadOnlyList<(Guid Variant, int Quantity)> lines,
        WebApplicationFactory<Program>? factory = null)
    {
        var client = await fixture.CustomerAsync(factory);
        var address = await client.CreateAddressAsync();
        foreach (var (variant, quantity) in lines)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.AddToCartAsync(variant, quantity)).StatusCode);
        }

        return new Shopper(client, address, await client.QuoteAsync(address));
    }

    public static Task<Shopper> ShopperAsync(this InventoryApiFixture fixture, Guid variant, int quantity = 1, WebApplicationFactory<Program>? factory = null) =>
        fixture.ShopperAsync([(variant, quantity)], factory);

    /// <summary>İki aktif depoda stoklu bir varyant; depo kodları (bayt sıralı) A &lt; B olduğundan dağıtım önce A'yı boşaltır.</summary>
    public static async Task<(Guid Variant, Guid WarehouseA, Guid WarehouseB, string Sku)> SplitVariantAsync(
        this InventoryApiFixture fixture,
        int stockA,
        int stockB)
    {
        var (variant, sku) = await fixture.CreateVariantAsync();
        var prefix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var a = await fixture.CreateWarehouseAsync(code: $"{prefix}-A");
        var b = await fixture.CreateWarehouseAsync(code: $"{prefix}-B");
        if (stockA > 0)
        {
            await fixture.SeedBalanceAsync(a, variant, stockA);
        }

        if (stockB > 0)
        {
            await fixture.SeedBalanceAsync(b, variant, stockB);
        }

        return (variant, a, b, sku);
    }

    /// <summary>Verilen durumda bir sipariş üretir (personel kendi uçlarıyla ilerletir). İptal edilmiş sipariş Admin iptaliyle üretilir.</summary>
    public static async Task<(Shopper Shopper, Guid OrderId, Guid Variant, Guid Warehouse)> OrderInStateAsync(
        this InventoryApiFixture fixture,
        string status,
        ApiClient admin,
        int stock = 10,
        int quantity = 2)
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(stock);
        var shopper = await fixture.ShopperAsync(variant, quantity);
        var placed = await shopper.PlaceAsync();
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var orderId = await placed.IdOfAsync();

        string[] path = status switch
        {
            "placed" => [],
            "processing" => ["processing"],
            "shipped" => ["processing", "shipped"],
            "completed" => ["processing", "shipped", "completed"],
            "cancelled" => ["cancelled"],
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        foreach (var step in path)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.StaffStatusAsync(orderId, step)).StatusCode);
        }

        return (shopper, orderId, variant, warehouse);
    }

    // ---------- Anlık görüntü: "hiçbir şey yazılmadı" kanıtı ----------

    /// <summary>
    /// Bakiye (miktar + sürüm), hareket sayısı, kullanıcının sipariş sayısı ve sepet satırları tek metinde. İki anlık görüntü eşitse hiçbir
    /// bakiye, hareket, sipariş ya da sepet satırı değişmemiştir.
    /// </summary>
    public static async Task<string> StateAsync(this InventoryApiFixture fixture, Guid userId, params Guid[] variants)
    {
        var list = string.Join(",", variants.Select(v => $"'{v}'"));
        var balances = await fixture.ScalarAsync<string>(
            $"SELECT coalesce(string_agg(warehouse_id || ':' || on_hand_quantity || ':' || version, ',' ORDER BY warehouse_id, product_variant_id), '') FROM inventory_balances WHERE product_variant_id IN ({list})");
        var movements = await fixture.ScalarAsync<long>($"SELECT count(*) FROM stock_movements WHERE product_variant_id IN ({list})");
        var orders = await fixture.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE user_id = '{userId}'");
        var cart = await fixture.ScalarAsync<string>(
            $"SELECT coalesce(string_agg(i.id || ':' || i.quantity, ',' ORDER BY i.id), '') FROM cart_items i JOIN carts c ON c.id = i.cart_id WHERE c.user_id = '{userId}'");
        return $"balances=[{balances}] movements={movements} orders={orders} cart=[{cart}]";
    }

    public static async Task<long> OrderCountAsync(this InventoryApiFixture fixture, Guid userId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE user_id = '{userId}'");

    public static async Task<long> OnHandSumAsync(this InventoryApiFixture fixture, Guid variantId) =>
        await fixture.ScalarAsync<long?>($"SELECT sum(on_hand_quantity) FROM inventory_balances WHERE product_variant_id = '{variantId}'") ?? 0;

    public static async Task<long> OrderStatusCountAsync(this InventoryApiFixture fixture, Guid orderId, string status) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE id = '{orderId}' AND status = '{status}'");

    public static async Task<long> HistoryCountAsync(this InventoryApiFixture fixture, Guid orderId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM order_status_history WHERE order_id = '{orderId}'");

    public static async Task<long> AllocationCountAsync(this InventoryApiFixture fixture, Guid orderId) =>
        await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id WHERE i.order_id = '{orderId}'");

    public static async Task<long> MovementsOfOrderAsync(this InventoryApiFixture fixture, Guid orderId, string type) =>
        await fixture.ScalarAsync<long>(
            $"""
            SELECT count(*) FROM stock_movements m
            WHERE m.operation_type = '{type}'
              AND m.operation_key IN (SELECT 'order-' || CASE WHEN '{type}' = 'order_placement' THEN 'place-' ELSE 'cancel-' END || replace(a.id::text, '-', '')
                                      FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id WHERE i.order_id = '{orderId}')
            """);

    /// <summary>Sistem yöneticisi (Admin) ve depo görevlisi (Warehouse) istemcileri.</summary>
    public static Task<ApiClient> WarehouseAsync(this InventoryApiFixture fixture, WebApplicationFactory<Program>? factory = null) =>
        fixture.LoginAsync(factory ?? fixture.DevelopmentFactory, AppRoles.Warehouse);
}

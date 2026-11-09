using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Application.Inventory;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>
/// Stok uçlarının HTTP sözleşmesi: yetki (401/403), CSRF/Origin, girdi doğrulaması (400), durum hataları (404/409), idempotency,
/// mass assignment, no-store, güvenli hata gövdeleri. Her test kendi verisini kurar.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class StockApiTests(InventoryApiFixture fixture)
{
    private static readonly (string Method, string Url, string? Body)[] AllAdminInventoryEndpoints =
    [
        ("GET", "/api/v1/admin/warehouses", null),
        ("POST", "/api/v1/admin/warehouses", """{"code":"AB-1","name":"x"}"""),
        ("PATCH", "/api/v1/admin/warehouses/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01", """{"name":"y"}"""),
        ("GET", "/api/v1/admin/inventory/balances", null),
        ("GET", "/api/v1/admin/inventory/movements", null),
        ("POST", "/api/v1/admin/inventory/receive", """{"quantity":1}"""),
        ("POST", "/api/v1/admin/inventory/adjust", """{"quantityDelta":1}"""),
    ];

    private async Task<HttpResponseMessage> SendRawAsync(ApiClient client, (string Method, string Url, string? Body) endpoint) =>
        await client.SendAsync(new HttpMethod(endpoint.Method), endpoint.Url, endpoint.Body, idempotencyKey: endpoint.Method == "POST" ? InventoryApiFixture.NewKey() : null);

    // ---------- 401 / 403 ----------

    [Fact]
    public async Task Anonymous_requests_get_401_unauthenticated_on_every_inventory_endpoint()
    {
        var anonymous = fixture.Anonymous();
        foreach (var endpoint in AllAdminInventoryEndpoints)
        {
            var response = await SendRawAsync(anonymous, endpoint);
            await ApiAssert.ProblemAsync(response, 401, "unauthenticated");
        }
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Warehouse")] // D-045: depo rolü stok yazamaz ve okuyamaz
    public async Task Non_admin_roles_get_403_forbidden_on_every_inventory_endpoint_read_and_write(string role)
    {
        var client = await fixture.LoginAsync(fixture.DevelopmentFactory, role);
        foreach (var endpoint in AllAdminInventoryEndpoints)
        {
            var response = await SendRawAsync(client, endpoint);
            await ApiAssert.ProblemAsync(response, 403, "forbidden");
        }
    }

    [Fact]
    public async Task A_forbidden_write_changes_nothing()
    {
        var customer = await fixture.LoginAsync(fixture.DevelopmentFactory, AppRoles.Customer);
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        await ApiAssert.ProblemAsync(await customer.ReceiveAsync(warehouse, variant, 5), 403, "forbidden");

        Assert.Null(await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    // ---------- CSRF / Origin ----------

    [Fact]
    public async Task Missing_csrf_token_gets_400_csrf_invalid_and_changes_nothing()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var response = await admin.PostAsync(
            "/api/v1/admin/inventory/receive",
            new { warehouseId = warehouse, variantId = variant, quantity = 5, reason = "x" },
            InventoryApiFixture.NewKey(),
            sendCsrf: false);

        await ApiAssert.ProblemAsync(response, 400, "csrf_invalid");
        Assert.Null(await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task A_foreign_Origin_gets_403_origin_not_allowed_even_with_a_valid_session_and_csrf_token()
    {
        var admin = await fixture.AdminAsync(fixture.ProductionFactory);
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var body = new { warehouseId = warehouse, variantId = variant, quantity = 5, reason = "x" };

        await ApiAssert.ProblemAsync(
            await admin.PostAsync("/api/v1/admin/inventory/receive", body, InventoryApiFixture.NewKey(), origin: "https://evil.example"),
            403,
            "origin_not_allowed");
        Assert.Null(await fixture.OnHandAsync(warehouse, variant));

        // İzinli origin geçer.
        var allowed = await admin.PostAsync("/api/v1/admin/inventory/receive", body, InventoryApiFixture.NewKey(), origin: InventoryApiFixture.ProductionOrigin);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    // Receive dışındaki yazma uçlarında da CSRF/Origin koruması ÇALIŞMA ZAMANINDA kanıtlanır (yalnız ApiSurface metadata'sı yetmez).
    [Fact]
    public async Task Missing_csrf_and_a_foreign_Origin_are_rejected_on_adjust_warehouse_create_and_warehouse_patch_and_nothing_is_written()
    {
        var admin = await fixture.AdminAsync(fixture.ProductionFactory);
        var warehouse = await fixture.CreateWarehouseAsync(name: "Orijinal Ad");
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 5);
        var code = $"CS{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var adjust = new { warehouseId = warehouse, variantId = variant, quantityDelta = -2, type = "adjustment", reason = "x" };
        var create = new { code, name = "Yeni" };
        var patch = new { name = "Degisti", isActive = false };
        var patchUrl = $"/api/v1/admin/warehouses/{warehouse}";
        const string foreign = "https://evil.example";

        // Eksik CSRF belirteci => 400 csrf_invalid
        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/inventory/adjust", adjust, InventoryApiFixture.NewKey(), sendCsrf: false), 400, "csrf_invalid");
        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/warehouses", create, sendCsrf: false), 400, "csrf_invalid");
        await ApiAssert.ProblemAsync(await admin.PatchAsync(patchUrl, patch, sendCsrf: false), 400, "csrf_invalid");

        // Yabancı Origin (geçerli oturum + geçerli CSRF ile bile) => 403 origin_not_allowed
        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/inventory/adjust", adjust, InventoryApiFixture.NewKey(), origin: foreign), 403, "origin_not_allowed");
        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/warehouses", create, origin: foreign), 403, "origin_not_allowed");
        await ApiAssert.ProblemAsync(await admin.PatchAsync(patchUrl, patch, origin: foreign), 403, "origin_not_allowed");

        // Hiçbir şey yazılmadı.
        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.VersionAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
        Assert.Equal(0, await fixture.ScalarAsync<long>($"SELECT count(*) FROM warehouses WHERE code = '{code}'"));
        Assert.Equal("Orijinal Ad", await fixture.ScalarAsync<string>($"SELECT name FROM warehouses WHERE id = '{warehouse}'"));
        Assert.True(await fixture.ScalarAsync<bool>($"SELECT is_active FROM warehouses WHERE id = '{warehouse}'"));
    }

    // ---------- Aktör (denetim) ve başka yöneticiden tekrar ----------

    [Fact]
    public async Task The_movement_reports_the_calling_admin_as_actor_on_create_and_in_the_movements_list()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var received = await admin.ReceiveAsync(warehouse, variant, 4);
        var adjusted = await admin.AdjustAsync(warehouse, variant, -1);

        using var r = await received.JsonAsync();
        using var a = await adjusted.JsonAsync();
        Assert.Equal(admin.UserId, r.RootElement.GetProperty("actorUserId").GetGuid());
        Assert.Equal(admin.UserId, a.RootElement.GetProperty("actorUserId").GetGuid());
        using var list = await (await admin.GetAsync($"/api/v1/admin/inventory/movements?variantId={variant}")).JsonAsync();
        Assert.All(list.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal(admin.UserId, item.GetProperty("actorUserId").GetGuid()));
    }

    [Fact]
    public async Task A_replay_by_a_second_admin_returns_the_ORIGINAL_actor_and_writes_nothing()
    {
        var first = await fixture.AdminAsync();
        var second = await fixture.AdminAsync();
        Assert.NotEqual(first.UserId, second.UserId);
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();
        var created = await first.ReceiveAsync(warehouse, variant, 6, key, reference: "PO-7");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var original = await created.JsonAsync();

        var replay = await second.ReceiveAsync(warehouse, variant, 6, key, reference: "PO-7");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var json = await replay.JsonAsync();
        Assert.True(json.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.UserId, json.RootElement.GetProperty("actorUserId").GetGuid()); // tekrarı gönderen DEĞİL
        Assert.Equal(original.RootElement.GetProperty("id").GetGuid(), json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(6, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.VersionAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
        Assert.Equal(first.UserId, await fixture.MovementActorAsync(key));
    }

    [Fact]
    public async Task A_second_admin_reusing_the_key_with_a_different_payload_gets_409_idempotency_key_reused()
    {
        var first = await fixture.AdminAsync();
        var second = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();
        Assert.Equal(HttpStatusCode.Created, (await first.ReceiveAsync(warehouse, variant, 6, key)).StatusCode);

        await ApiAssert.ProblemAsync(await second.ReceiveAsync(warehouse, variant, 7, key), 409, "idempotency_key_reused");

        Assert.Equal(6, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
        Assert.Equal(first.UserId, await fixture.MovementActorAsync(key));
    }

    // ---------- Idempotency-Key başlığı ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kisa-anahtar")]                                  // 16 karakterden kısa
    [InlineData("gecersiz karakter bosluk var 12345")]            // boşluk
    [InlineData("gecersiz-anahtar-turkce-ğ-ü-ş-karakterler")]     // ASCII dışı
    [InlineData("a,b-anahtar-virgul-birlestirme-12345678")]       // virgül
    public async Task Missing_or_malformed_Idempotency_Key_gets_400_validation_failed(string? key)
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var response = await admin.PostAsync(
            "/api/v1/admin/inventory/receive",
            new { warehouseId = warehouse, variantId = variant, quantity = 5, reason = "x" },
            key);

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        using var json = await response.JsonAsync();
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task An_over_long_Idempotency_Key_gets_400_and_the_longest_valid_key_is_accepted()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(warehouse, variant, 1, new string('a', 129)), 400, "validation_failed");
        Assert.Equal(HttpStatusCode.Created, (await admin.ReceiveAsync(warehouse, variant, 1, new string('b', 128))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.ReceiveAsync(warehouse, variant, 1, new string('c', 16))).StatusCode);
    }

    // ---------- Girdi doğrulaması ----------

    public static TheoryData<string, string> InvalidReceiveBodies => new()
    {
        { "quantity=0", """{"warehouseId":"{W}","variantId":"{V}","quantity":0,"reason":"x"}""" },
        { "quantity=-5", """{"warehouseId":"{W}","variantId":"{V}","quantity":-5,"reason":"x"}""" },
        { "quantity>max", """{"warehouseId":"{W}","variantId":"{V}","quantity":1000001,"reason":"x"}""" },
        { "quantity missing", """{"warehouseId":"{W}","variantId":"{V}","reason":"x"}""" },
        { "reason missing", """{"warehouseId":"{W}","variantId":"{V}","quantity":1}""" },
        { "reason blank", """{"warehouseId":"{W}","variantId":"{V}","quantity":1,"reason":"   "}""" },
        { "reason too long", "{\"warehouseId\":\"{W}\",\"variantId\":\"{V}\",\"quantity\":1,\"reason\":\"" + new string('r', 501) + "\"}" },
        { "reference too long", "{\"warehouseId\":\"{W}\",\"variantId\":\"{V}\",\"quantity\":1,\"reason\":\"x\",\"reference\":\"" + new string('f', 129) + "\"}" },
        { "warehouseId missing", """{"variantId":"{V}","quantity":1,"reason":"x"}""" },
        { "variantId empty guid", """{"warehouseId":"{W}","variantId":"00000000-0000-0000-0000-000000000000","quantity":1,"reason":"x"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidReceiveBodies))]
    public async Task Invalid_receive_bodies_get_400_validation_failed_and_change_nothing(string caseName, string template)
    {
        _ = caseName;
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var body = template.Replace("{W}", warehouse.ToString()).Replace("{V}", variant.ToString());

        var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/admin/inventory/receive", body, InventoryApiFixture.NewKey());

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        Assert.Null(await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    public static TheoryData<string, string> InvalidAdjustBodies => new()
    {
        { "delta=0", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":0,"type":"adjustment","reason":"x"}""" },
        { "delta>max", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":1000001,"type":"adjustment","reason":"x"}""" },
        { "delta<-max", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":-1000001,"type":"adjustment","reason":"x"}""" },
        { "delta huge", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":9999999999999,"type":"adjustment","reason":"x"}""" },
        { "type missing", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":1,"reason":"x"}""" },
        { "type receiving", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":1,"type":"receiving","reason":"x"}""" },
        { "type unknown", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":1,"type":"gift","reason":"x"}""" },
        { "type wrong case", """{"warehouseId":"{W}","variantId":"{V}","quantityDelta":1,"type":"Adjustment","reason":"x"}""" },
        { "reason too long", "{\"warehouseId\":\"{W}\",\"variantId\":\"{V}\",\"quantityDelta\":1,\"type\":\"correction\",\"reason\":\"" + new string('r', 501) + "\"}" },
    };

    [Theory]
    [MemberData(nameof(InvalidAdjustBodies))]
    public async Task Invalid_adjust_bodies_get_400_validation_failed_and_change_nothing(string caseName, string template)
    {
        _ = caseName;
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 10);
        var body = template.Replace("{W}", warehouse.ToString()).Replace("{V}", variant.ToString());

        var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/admin/inventory/adjust", body, InventoryApiFixture.NewKey());

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Theory]
    [InlineData("reason", "ab\0cd")]
    [InlineData("reason", "iki\nsatir")]
    [InlineData("reference", "PO\0-1")]
    [InlineData("reference", "PO\t1")]
    public async Task Control_characters_in_reason_or_reference_get_400_not_500_on_receive_and_adjust(string field, string value)
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 5);
        var receive = new Dictionary<string, object?> { ["warehouseId"] = warehouse, ["variantId"] = variant, ["quantity"] = 1, ["reason"] = "x", [field] = value };
        var adjust = new Dictionary<string, object?> { ["warehouseId"] = warehouse, ["variantId"] = variant, ["quantityDelta"] = -1, ["type"] = "adjustment", ["reason"] = "x", [field] = value };

        var r1 = await admin.PostAsync("/api/v1/admin/inventory/receive", receive, InventoryApiFixture.NewKey());
        var r2 = await admin.PostAsync("/api/v1/admin/inventory/adjust", adjust, InventoryApiFixture.NewKey());

        await ApiAssert.ProblemAsync(r1, 400, "validation_failed");
        await ApiAssert.ProblemAsync(r2, 400, "validation_failed");
        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task Boundary_values_are_accepted_max_quantity_and_both_adjustment_types_and_trimmed_text()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var max = await admin.ReceiveAsync(warehouse, variant, 1_000_000, reason: "  " + new string('r', 500) + "  ", reference: "  PO-9  ");
        Assert.Equal(HttpStatusCode.Created, max.StatusCode);
        using var json = await max.JsonAsync();
        Assert.Equal(500, json.RootElement.GetProperty("reason").GetString()!.Length); // kırpıldı
        Assert.Equal("PO-9", json.RootElement.GetProperty("reference").GetString());
        Assert.Equal(1_000_000, json.RootElement.GetProperty("onHandAfter").GetInt32());

        Assert.Equal(HttpStatusCode.Created, (await admin.AdjustAsync(warehouse, variant, -1_000_000, "correction")).StatusCode);
        Assert.Equal(0, await fixture.OnHandAsync(warehouse, variant));
    }

    // ---------- Durum hataları ----------

    [Fact]
    public async Task Missing_warehouse_gets_404_warehouse_not_found_and_missing_variant_404_variant_not_found()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(Guid.NewGuid(), variant, 1), 404, "warehouse_not_found");
        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(warehouse, Guid.NewGuid(), 1), 404, "variant_not_found");
        await ApiAssert.ProblemAsync(await admin.AdjustAsync(Guid.NewGuid(), variant, -1), 404, "warehouse_not_found");
        await ApiAssert.ProblemAsync(await admin.AdjustAsync(warehouse, Guid.NewGuid(), -1), 404, "variant_not_found");
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task Inactive_warehouse_and_inactive_variant_get_409_and_change_nothing()
    {
        var admin = await fixture.AdminAsync();
        var inactiveWarehouse = await fixture.CreateWarehouseAsync(active: false);
        var activeWarehouse = await fixture.CreateWarehouseAsync();
        var (activeVariant, _) = await fixture.CreateVariantAsync();
        var (inactiveVariant, _) = await fixture.CreateVariantAsync(variantActive: false);

        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(inactiveWarehouse, activeVariant, 1), 409, "warehouse_inactive");
        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(activeWarehouse, inactiveVariant, 1), 409, "variant_inactive");

        Assert.Equal(0, await fixture.MovementCountAsync(activeVariant));
        Assert.Equal(0, await fixture.MovementCountAsync(inactiveVariant));
        Assert.Null(await fixture.OnHandAsync(inactiveWarehouse, activeVariant));
    }

    [Fact]
    public async Task Going_below_zero_gets_409_insufficient_stock_and_decrementing_a_missing_balance_too()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        await ApiAssert.ProblemAsync(await admin.AdjustAsync(warehouse, variant, -1), 409, "insufficient_stock"); // bakiye satırı hiç yok
        Assert.Null(await fixture.OnHandAsync(warehouse, variant)); // satır oluşturulmadı

        await fixture.SeedBalanceAsync(warehouse, variant, 3);
        await ApiAssert.ProblemAsync(await admin.AdjustAsync(warehouse, variant, -4, "correction"), 409, "insufficient_stock");
        Assert.Equal(HttpStatusCode.Created, (await admin.AdjustAsync(warehouse, variant, -3)).StatusCode); // tam sıfıra serbest
    }

    // ---------- Idempotency (HTTP) ----------

    [Fact]
    public async Task The_first_application_is_201_and_a_repeat_is_200_replayed_with_the_original_result()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, sku) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();

        var first = await admin.ReceiveAsync(warehouse, variant, 8, key, reference: "PO-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var created = await first.JsonAsync();
        // Bu arada başka bir işlem bakiyeyi değiştirir; tekrar, İLK sonucu (onHandAfter=8) döndürmelidir.
        await admin.ReceiveAsync(warehouse, variant, 2);
        var second = await admin.ReceiveAsync(warehouse, variant, 8, key, reference: "PO-1");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var replay = await second.JsonAsync();
        Assert.Equal(created.RootElement.GetProperty("id").GetGuid(), replay.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(8, replay.RootElement.GetProperty("onHandAfter").GetInt32());
        Assert.True(replay.RootElement.GetProperty("replayed").GetBoolean());
        Assert.False(created.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(2, await fixture.MovementCountAsync(variant));

        // Yanıt şekli (sözleşme): tam bu alanlar, para/birim yok, aktör yok.
        Assert.Equal(
            new[] { "actorEmail", "actorUserId", "id", "onHandAfter", "occurredAt", "operationType", "quantityDelta", "reason", "reference", "replayed", "sku", "variantId", "warehouseId" }.Order().ToArray(),
            created.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(sku, created.RootElement.GetProperty("sku").GetString());
        Assert.Equal("receiving", created.RootElement.GetProperty("operationType").GetString());
        Assert.Equal(8, created.RootElement.GetProperty("quantityDelta").GetInt32());
    }

    // ---------- Mass assignment / kimlik ----------

    [Fact]
    public async Task Client_sent_actor_and_balance_fields_are_ignored_the_actor_comes_from_the_session()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var key = InventoryApiFixture.NewKey();
        var stranger = Guid.NewGuid();

        var response = await admin.PostAsync(
            "/api/v1/admin/inventory/receive",
            new
            {
                warehouseId = warehouse,
                variantId = variant,
                quantity = 3,
                reason = "x",
                actorUserId = stranger,
                userId = stranger,
                onHandAfter = 999,
                onHandQuantity = 999,
                onHand = 999,
                version = 77,
                operationKey = "baska-bir-anahtar-123456",
                requestFingerprint = new string('a', 64),
                occurredAt = "2001-01-01T00:00:00Z",
                replayed = true,
            },
            key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal(3, json.RootElement.GetProperty("onHandAfter").GetInt32());
        Assert.False(json.RootElement.GetProperty("replayed").GetBoolean());
        Assert.True(json.RootElement.GetProperty("occurredAt").GetDateTimeOffset().Year >= 2026);
        Assert.Equal(3, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.VersionAsync(warehouse, variant));
        Assert.Equal(admin.UserId, await fixture.MovementActorAsync(key));
        Assert.Equal(1, await fixture.MovementKeyCountAsync(key));
    }

    // ---------- no-store / güvenli hata ----------

    [Fact]
    public async Task Admin_inventory_responses_are_no_store_on_success_and_on_every_error()
    {
        var admin = await fixture.AdminAsync();
        var anonymous = fixture.Anonymous();
        var customer = await fixture.LoginAsync(fixture.DevelopmentFactory, AppRoles.Customer);
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var responses = new[]
        {
            await admin.GetAsync("/api/v1/admin/warehouses"),                                     // 200
            await admin.ReceiveAsync(warehouse, variant, 1),                                      // 201
            await admin.ReceiveAsync(warehouse, variant, 0),                                      // 400
            await admin.AdjustAsync(warehouse, variant, -50),                                     // 409
            await admin.ReceiveAsync(Guid.NewGuid(), variant, 1),                                 // 404
            await anonymous.GetAsync("/api/v1/admin/inventory/balances"),                         // 401
            await customer.GetAsync("/api/v1/admin/inventory/movements"),                         // 403
            await admin.PostAsync("/api/v1/admin/inventory/receive", new { }, "kisa", sendCsrf: false), // 400 csrf
        };

        Assert.All(responses, response => Assert.Equal("no-store", response.Headers.CacheControl?.ToString()));
    }

    [Fact]
    public async Task An_unexpected_server_failure_returns_a_generic_500_without_the_exception_text_or_sql()
    {
        const string secret = "Host=10.1.2.3;Password=hunter2;stock_movements violates";
        await using var factory = ApiFactory.Create(
            "Production",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddScoped<IInventoryService>(_ => new ThrowingInventoryService(secret));
            },
            new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { ["Security:AllowedOrigins:0"] = InventoryApiFixture.ProductionOrigin });
        var admin = await fixture.LoginAsync(factory, AppRoles.Admin);
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var response = await admin.ReceiveAsync(warehouse, variant, 1);

        await ApiAssert.ProblemAsync(response, 500, "internal_error");
        var text = await response.TextAsync();
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("10.1.2.3", text);
        Assert.DoesNotContain("stock_movements", text);
        Assert.DoesNotContain("Exception", text);
    }

    [Fact]
    public async Task Validation_and_state_errors_never_contain_sql_or_exception_text()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var bodies = new List<string>
        {
            await (await admin.ReceiveAsync(warehouse, variant, 0)).TextAsync(),
            await (await admin.AdjustAsync(warehouse, variant, -9)).TextAsync(),
            await (await admin.ReceiveAsync(Guid.NewGuid(), variant, 1)).TextAsync(),
            await (await admin.SendAsync(HttpMethod.Post, "/api/v1/admin/inventory/receive", "{", InventoryApiFixture.NewKey())).TextAsync(),
        };

        Assert.All(bodies, text =>
        {
            foreach (var forbidden in new[] { "Npgsql", "Exception", "SELECT ", "INSERT ", "UPDATE ", "stock_movements", "inventory_balances", "at SSCommerce", "StackTrace" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        });
    }

    private sealed class ThrowingInventoryService(string message) : IInventoryService
    {
        public Task<StockOperationResult> ApplyStockOperationAsync(StockCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task<WarehouseWriteResult> CreateWarehouseAsync(Guid actorUserId, NewWarehouse warehouse, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task<WarehouseWriteResult> UpdateWarehouseAsync(Guid actorUserId, Guid id, WarehouseChange change, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    // ---------- Gövde: bozuk JSON, yanlış tip, boyut sınırı (Development ve Production aynı) ----------

    public static TheoryData<bool, string, string> BadBodies => new()
    {
        { false, "malformed json", "{" },
        { true, "malformed json", "{" },
        { false, "wrong type: quantity string", """{"quantity":"abc"}""" },
        { true, "wrong type: quantity string", """{"quantity":"abc"}""" },
        { false, "wrong type: warehouseId number", """{"warehouseId":12,"quantity":1}""" },
        { true, "wrong type: warehouseId number", """{"warehouseId":12,"quantity":1}""" },
        { false, "wrong type: quantity fraction", """{"quantity":1.5}""" },
        { true, "wrong type: quantity fraction", """{"quantity":1.5}""" },
        { false, "array instead of object", "[]" },
        { true, "array instead of object", "[]" },
        { false, "null literal", "null" },
        { true, "null literal", "null" },
        { false, "empty body", "" },
        { true, "empty body", "" },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Bad_json_bodies_get_400_validation_failed_in_both_environments(bool production, string caseName, string body)
    {
        _ = caseName;
        var factory = production ? fixture.ProductionFactory : fixture.DevelopmentFactory;
        var admin = await fixture.AdminAsync(factory);

        foreach (var url in new[] { "/api/v1/admin/inventory/receive", "/api/v1/admin/inventory/adjust", "/api/v1/admin/warehouses" })
        {
            var response = await admin.SendAsync(HttpMethod.Post, url, body, InventoryApiFixture.NewKey());
            await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_non_json_content_type_gets_415_unsupported_media_type_and_nothing_is_read(bool production)
    {
        var admin = await fixture.AdminAsync(production ? fixture.ProductionFactory : fixture.DevelopmentFactory);

        var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/admin/inventory/receive", "quantity=1", InventoryApiFixture.NewKey(), contentType: "text/plain");

        // Accepts<T>("application/json") yönlendirme katmanında 415 üretir (Auth uçlarıyla aynı davranış); gövdeye hiç inilmez.
        await ApiAssert.ProblemAsync(response, 415, "unsupported_media_type");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_body_over_16_KB_gets_400_validation_failed_without_being_processed(bool production)
    {
        var admin = await fixture.AdminAsync(production ? fixture.ProductionFactory : fixture.DevelopmentFactory);
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var padding = new string('x', 17 * 1024);
        var body = $$"""{"warehouseId":"{{warehouse}}","variantId":"{{variant}}","quantity":1,"reason":"x","note":"{{padding}}"}""";

        var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/admin/inventory/receive", body, InventoryApiFixture.NewKey());

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        Assert.Equal(0, await fixture.MovementCountAsync(variant));

        // Sınırın hemen altı (bilinmeyen büyük alanla) yine de kabul edilir: sınır 16 KB, 413 değil.
        var small = $$"""{"warehouseId":"{{warehouse}}","variantId":"{{variant}}","quantity":1,"reason":"x","note":"{{new string('x', 15 * 1024)}}"}""";
        Assert.Equal(
            HttpStatusCode.Created,
            (await admin.SendAsync(HttpMethod.Post, "/api/v1/admin/inventory/receive", small, InventoryApiFixture.NewKey())).StatusCode);
    }

    [Fact]
    public async Task A_chunked_body_without_Content_Length_is_also_capped_at_16_KB()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var body = $$"""{"warehouseId":"{{warehouse}}","variantId":"{{variant}}","quantity":1,"reason":"x","note":"{{new string('x', 20 * 1024)}}"}""";

        var response = await admin.SendUnknownLengthAsync("/api/v1/admin/inventory/receive", body, InventoryApiFixture.NewKey());

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    // ---------- Rate limit (mutation) ----------

    [Fact]
    public async Task Mutation_requests_over_the_limit_get_429_rate_limited_per_IP_and_reads_are_not_counted()
    {
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Security:RateLimit:MutationPermitLimit"] = "3",
                ["Security:RateLimit:WindowSeconds"] = "600", // test sırasında pencere dolmaz (flaky olmaz)
            });
        var admin = await fixture.LoginAsync(factory, AppRoles.Admin);
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 5; i++)
        {
            last = await admin.ReceiveAsync(warehouse, variant, 1);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
        await ApiAssert.ProblemAsync(last!, 429, "rate_limited");
        Assert.True(last!.Headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds is > 0 and <= 600);
        Assert.Equal(3, await fixture.OnHandAsync(warehouse, variant)); // reddedilenler hiçbir şey yazmadı

        // Okuma uçları `mutation` politikasında DEĞİL.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/warehouses")).StatusCode);
        // Warehouse yazması da aynı kovada: limit dolu.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await admin.PostAsync("/api/v1/admin/warehouses", new { code = "RL-1", name = "x" })).StatusCode);
    }

    [Fact]
    public void Every_non_read_admin_endpoint_carries_the_mutation_rate_limit_policy()
    {
        var endpoints = fixture.DevelopmentFactory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/admin/", StringComparison.Ordinal))
            .ToList();
        var writes = endpoints
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods.Any(m => m is "POST" or "PUT" or "PATCH" or "DELETE"))
            .ToList();

        Assert.True(writes.Count >= 4, "Beklenen stok/depo yazma uçları bulunamadı; test boş geçmesin.");
        Assert.All(writes, endpoint =>
            Assert.Contains(
                endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>(),
                attribute => attribute.PolicyName == "mutation"));
    }
}

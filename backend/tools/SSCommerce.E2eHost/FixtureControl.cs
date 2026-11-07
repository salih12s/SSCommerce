using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Inventory;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.E2eHost;

/// <summary>
/// E2E TEST FIXTURE (yalnız bu araç). Üretim API'sinde böyle bir uç YOKTUR (SSCommerce.Api'ye hiçbir şey eklenmez).
/// İki iş yapar: (1) demo seed'den sonra <c>E2E-DEPO</c> deposu ve SKU sırasına göre belirlenimli stok kurar,
/// (2) fiyat/stok/aktiflik/sipariş durumu (personel yürütmesi) değiştiren senaryolar için YALNIZ <c>127.0.0.1:5082</c>'ye bağlı küçük bir kontrol ucu açar.
/// Yazdığı tek yer bu sürecin kendi GUID'li test veritabanıdır (bağlantı dizesi çağıran Program'dan gelir). Stok yazması bir TEST FIXTURE'ıdır:
/// doğrudan <c>inventory_balances</c> satırıdır, hareket (stock_movements) bırakmaz ve gerçek stok diye sunulmaz.
/// İstisna iletileri ASLA yazdırılmaz (Npgsql iletileri bağlantı dizesi içerebilir).
/// </summary>
internal static partial class FixtureControl
{
    public const string WarehouseCode = "E2E-DEPO";
    public const string Url = "http://127.0.0.1:5082";
    public const int Port = 5082;

    // SKU sırası (ordinal) mod 4 → stok: 0 = stok yok, 1 = 3 adet, 2 = 12 adet, 3 = 50 adet (cap 20 üstü dahil her durum görünür).
    private static readonly int[] StockBySkuIndexMod4 = [0, 3, 12, 50];

    [GeneratedRegex(@"^[A-Z0-9]+(-[A-Z0-9]+)*\z")]
    private static partial Regex SkuPattern();

    public static int StockForSkuIndex(int index) => StockBySkuIndexMod4[index % 4];

    /// <summary>Depoyu ve belirlenimli bakiyeleri kurar. Tüm SKU'lar ordinal sıralanır; sıra numarası (0 tabanlı) mod 4 stoğu belirler.</summary>
    public static async Task SeedStockAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var warehouse = new NpgsqlCommand(
            "INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES (@id, @code, @name, true, now(), now()) ON CONFLICT (code) DO NOTHING",
            connection))
        {
            warehouse.Parameters.AddWithValue("id", Guid.CreateVersion7());
            warehouse.Parameters.AddWithValue("code", WarehouseCode);
            warehouse.Parameters.AddWithValue("name", "E2E test deposu (gerçek stok değildir)");
            await warehouse.ExecuteNonQueryAsync();
        }

        var skus = new List<string>();
        await using (var read = new NpgsqlCommand("SELECT sku FROM product_variants", connection))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                skus.Add(reader.GetString(0));
            }
        }

        skus.Sort(StringComparer.Ordinal);
        for (var index = 0; index < skus.Count; index++)
        {
            var quantity = StockForSkuIndex(index);
            if (quantity == 0)
            {
                continue;
            }

            await UpsertStockAsync(connection, skus[index], quantity);
        }
    }

    /// <param name="url">Yalnız testler için: varsayılan <see cref="Url"/> (sabit 5082). Test, çakışmasın diye <c>http://127.0.0.1:0</c> verip gerçek adresi <c>app.Urls</c>'tan okur.</param>
    public static WebApplication StartControlServer(string connectionString, string url = Url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();

        // Savunma derinliği: yalnız loopback istemciler. (Kestrel zaten 127.0.0.1'e bağlıdır.)
        app.Use(async (context, next) =>
        {
            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) ||
                !HttpMethods.IsPost(context.Request.Method) ||
                !context.Request.HasJsonContentType())
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next();
        });

        app.MapPost("/fixture/stock", (StockRequest request) => RunAsync(connectionString, async connection =>
        {
            if (!ValidSku(request.Sku) || request.Quantity is null or < 0 or > InventoryRules.MaxOnHandQuantity)
            {
                return Results.BadRequest();
            }

            return await UpsertStockAsync(connection, request.Sku!, request.Quantity.Value) ? Results.NoContent() : Results.NotFound();
        }));

        app.MapPost("/fixture/price", (PriceRequest request) => RunAsync(connectionString, async connection =>
        {
            if (!ValidSku(request.Sku) || request.PriceMinor is null or < 0 || request.PriceMinor > Money.ToMinor(Money.MaxAmount))
            {
                return Results.BadRequest();
            }

            await using var command = new NpgsqlCommand("UPDATE product_variants SET price = @price, updated_at = now() WHERE sku = @sku", connection);
            command.Parameters.AddWithValue("price", Money.FromMinor(request.PriceMinor.Value));
            command.Parameters.AddWithValue("sku", request.Sku!);
            return await command.ExecuteNonQueryAsync() > 0 ? Results.NoContent() : Results.NotFound();
        }));

        app.MapPost("/fixture/active", (ActiveRequest request) => RunAsync(connectionString, async connection =>
        {
            if (!ValidSku(request.Sku) || request.IsActive is null)
            {
                return Results.BadRequest();
            }

            await using var command = new NpgsqlCommand("UPDATE product_variants SET is_active = @active, updated_at = now() WHERE sku = @sku", connection);
            command.Parameters.AddWithValue("active", request.IsActive.Value);
            command.Parameters.AddWithValue("sku", request.Sku!);
            return await command.ExecuteNonQueryAsync() > 0 ? Results.NoContent() : Results.NotFound();
        }));

        // Personel yürütmesini (işleme alma, kargolama, tamamlama) taklit eder: üretim API'sinde Playwright'ın çağırabileceği personel oturumu
        // kurmak gerekmesin. YALNIZ yasal ileri geçiş (Domain geçiş tablosu); 'cancelled' kabul edilmez çünkü iptal stok iadesi yazar —
        // iptal gerçek müşteri ucundan denenir. Geçmiş satırı da yazılır (aktör: siparişin sahibi, tür 'staff').
        app.MapPost("/fixture/order-status", (OrderStatusRequest request) => RunAsync(connectionString, async connection =>
        {
            if (request.OrderId is not { } orderId || orderId == Guid.Empty ||
                !OrderStatuses.TryFromWire(request.Status, out var target) || target == OrderStatus.Cancelled)
            {
                return Results.BadRequest();
            }

            await using var transaction = await connection.BeginTransactionAsync();
            OrderStatus from;
            Guid ownerId;
            await using (var read = new NpgsqlCommand("SELECT status, user_id FROM orders WHERE id = @id FOR UPDATE", connection, transaction))
            {
                read.Parameters.AddWithValue("id", orderId);
                await using var rows = await read.ExecuteReaderAsync();
                if (!await rows.ReadAsync())
                {
                    return Results.NotFound();
                }

                from = OrderStatuses.FromWire(rows.GetString(0));
                ownerId = rows.GetGuid(1);
            }

            if (!OrderStateMachine.IsKnownTransition(from, target))
            {
                return Results.Conflict();
            }

            await using (var update = new NpgsqlCommand("UPDATE orders SET status = @status, updated_at = now() WHERE id = @id", connection, transaction))
            {
                update.Parameters.AddWithValue("status", OrderStatuses.ToWire(target));
                update.Parameters.AddWithValue("id", orderId);
                await update.ExecuteNonQueryAsync();
            }

            await using (var history = new NpgsqlCommand(
                """
                INSERT INTO order_status_history (id, order_id, from_status, to_status, actor_user_id, actor_kind, reason, occurred_at)
                VALUES (@id, @order, @from, @to, @actor, 'staff', 'e2e fixture', now())
                """,
                connection,
                transaction))
            {
                history.Parameters.AddWithValue("id", Guid.CreateVersion7());
                history.Parameters.AddWithValue("order", orderId);
                history.Parameters.AddWithValue("from", OrderStatuses.ToWire(from));
                history.Parameters.AddWithValue("to", OrderStatuses.ToWire(target));
                history.Parameters.AddWithValue("actor", ownerId);
                await history.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Results.NoContent();
        }));

        // Personel rolü ataması (Faz 8 E2E): HTTP'de personel rolü verme yolu yoktur (kayıt hep Customer); Playwright, kayıtlı bir hesaba Admin/Warehouse
        // rolünü bu loopback ucundan verir. YALNIZ tam eşleşen "Admin" ya da "Warehouse" (başka rol reddedilir). Üretim API'sinde böyle bir uç YOKTUR.
        // Güvenlik damgası yenilenir: hesabın eski cookie'si düşer, kullanıcı yeniden giriş yapar (gerçek rol API'siyle aynı davranış). E-posta yazdırılmaz.
        app.MapPost("/fixture/role", (RoleRequest request) => RunAsync(connectionString, async connection =>
        {
            var roleId = request.Role switch
            {
                "Admin" => AppRoles.AdminId,
                "Warehouse" => AppRoles.WarehouseId,
                _ => Guid.Empty,
            };
            if (roleId == Guid.Empty || string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254)
            {
                return Results.BadRequest();
            }

            await using var transaction = await connection.BeginTransactionAsync();
            Guid? userId;
            await using (var find = new NpgsqlCommand("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = @email", connection, transaction))
            {
                find.Parameters.AddWithValue("email", request.Email.Trim().ToUpperInvariant());
                userId = await find.ExecuteScalarAsync() as Guid?;
            }

            if (userId is null)
            {
                return Results.NotFound();
            }

            await using (var grant = new NpgsqlCommand(
                "INSERT INTO \"AspNetUserRoles\" (\"UserId\", \"RoleId\") VALUES (@user, @role) ON CONFLICT DO NOTHING",
                connection,
                transaction))
            {
                grant.Parameters.AddWithValue("user", userId.Value);
                grant.Parameters.AddWithValue("role", roleId);
                await grant.ExecuteNonQueryAsync();
            }

            await using (var stamp = new NpgsqlCommand(
                "UPDATE \"AspNetUsers\" SET \"SecurityStamp\" = gen_random_uuid()::text, \"ConcurrencyStamp\" = gen_random_uuid()::text WHERE \"Id\" = @user",
                connection,
                transaction))
            {
                stamp.Parameters.AddWithValue("user", userId.Value);
                await stamp.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Results.NoContent();
        }));

        app.Start();
        return app;
    }

    private static bool ValidSku(string? sku) => sku is { Length: > 0 and <= CatalogRules.SkuMaxLength } && SkuPattern().IsMatch(sku);

    private static async Task<IResult> RunAsync(string connectionString, Func<NpgsqlConnection, Task<IResult>> action)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return await action(connection);
        }
        catch (Exception)
        {
            // İleti yazdırılmaz/döndürülmez (bağlantı dizesi sızabilir).
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>E2E-DEPO'daki bakiyeyi mutlak değere ayarlar (yoksa ekler). Hareket YAZMAZ. SKU yoksa false.</summary>
    private static async Task<bool> UpsertStockAsync(NpgsqlConnection connection, string sku, int quantity)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at)
            SELECT @id, w.id, v.id, @quantity, 1, now(), now()
            FROM warehouses w, product_variants v
            WHERE w.code = @code AND v.sku = @sku
            ON CONFLICT (warehouse_id, product_variant_id) DO UPDATE
            SET on_hand_quantity = EXCLUDED.on_hand_quantity, version = inventory_balances.version + 1, updated_at = now()
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("code", WarehouseCode);
        command.Parameters.AddWithValue("sku", sku);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    private sealed record StockRequest(string? Sku, int? Quantity);

    private sealed record PriceRequest(string? Sku, long? PriceMinor);

    private sealed record ActiveRequest(string? Sku, bool? IsActive);

    private sealed record OrderStatusRequest(Guid? OrderId, string? Status);

    private sealed record RoleRequest(string? Email, string? Role);
}

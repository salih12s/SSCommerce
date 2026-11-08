using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Application.Addresses;
using SSCommerce.Application.Carts;
using SSCommerce.Application.Checkout;
using SSCommerce.Application.Favorites;
using SSCommerce.Application.Inventory;
using SSCommerce.Application.Orders;
using SSCommerce.Application.Showcase;
using SSCommerce.Domain.Inventory;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Infrastructure.Showcase;

/// <summary>Beklenen kural hatası (çıkış 1). İleti SABİT metindir: parola, e-posta, kimlik ya da bağlantı bilgisi taşımaz.</summary>
public sealed class ShowcaseSeedException(string message) : Exception(message);

/// <summary>Bir koşuda YENİ yazılan satır sayıları (ikinci koşuda hepsi 0) ve vitrin müşterisinin toplamları.</summary>
public sealed record ShowcaseSeedResult(
    int CatalogRows,
    int AccountsCreated,
    int WarehousesCreated,
    int StockReceipts,
    int AddressesCreated,
    int FavoritesAdded,
    int CartLinesAdded,
    int OrdersPlaced,
    int OrderTransitions,
    int OrdersTotal,
    int FavoritesTotal,
    int AddressesTotal)
{
    public int NewTotal =>
        CatalogRows + AccountsCreated + WarehousesCreated + StockReceipts + AddressesCreated + FavoritesAdded + CartLinesAdded + OrdersPlaced + OrderTransitions;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"Vitrin verisi tamam. Yeni: {CatalogRows} katalog satırı, {AccountsCreated} hesap, {WarehousesCreated} depo, {StockReceipts} stok girişi, {AddressesCreated} adres, {FavoritesAdded} favori, {CartLinesAdded} sepet satırı, {OrdersPlaced} sipariş, {OrderTransitions} durum geçişi{(NewTotal == 0 ? " (zaten güncel; değişiklik yok)." : ".")} Vitrin müşterisi toplamı: {OrdersTotal} sipariş, {FavoritesTotal} favori, {AddressesTotal} adres.");
}

/// <summary>
/// Vitrin verisi (VT-2, D-092). Her adım GERÇEK uygulama servislerinden geçer (stok: envanter servisi, sipariş: önizleme + sipariş verme + durum servisi),
/// yani stok hareketleri, tahsisler ve durum geçmişi gerçek akışla yazılır ve <c>deploy/db/invariants.sql</c> değişmezleri korunur.
/// <list type="bullet">
/// <item>İdempotent: her adım "var mı?" bakar (doğal anahtarlar: e-posta, adres etiketi, ürün, sipariş Idempotency-Key'i, stok işlem anahtarı).</item>
/// <item>Sipariş verme servisi doğrudan çağrılır; <c>Ordering:Enabled</c> yalnız HTTP ucundaki kapıdır (503), servis düzeyinde etkisi yoktur. Bu yüzden seed o anahtardan bağımsızdır.</item>
/// <item>Tarihler: <see cref="ShowcaseClock"/> ile satırlar baştan geçmiş bir anla yazılır (siparişler/hareketler/geçmiş UPDATE ile değiştirilemez, trigger'lar reddeder).
/// Saat bir <see cref="ShowcaseClock"/> değilse (testler) hepsi gerçek anla yazılır, davranış aynıdır.</item>
/// <item>Parola: <see cref="RandomNumberGenerator"/> ile üretilir, Identity hash'ler, kimseye gösterilmez ve hiçbir yere yazılmaz.</item>
/// </list>
/// </summary>
internal sealed class ShowcaseSeeder(IServiceProvider services, TimeProvider time)
{
    private const int StockPerVariant = 50;
    private const string ReceiveReason = "Vitrin başlangıç stoğu";

    private sealed record OrderRecipe(
        string KeySuffix,
        string AddressLabel,
        (string Sku, int Quantity)[] Lines,
        TimeSpan PlacedAgo,
        OrderStatus Target,
        TimeSpan[] StepsAgo);

    // Kronolojik sıra (en eskiden yeniye). Açık sipariş (placed + processing) en çok 2 olur: Ordering:MaxOpenOrdersPerUser (3) hiçbir adımda dolmaz.
    private static readonly OrderRecipe[] Recipes =
    [
        new("completed", "Ev", [("SSC-3001-STD", 1), ("SSC-6001-15", 1)], TimeSpan.FromDays(12), OrderStatus.Completed,
            [TimeSpan.FromDays(12) - TimeSpan.FromHours(3), TimeSpan.FromDays(11), TimeSpan.FromDays(9)]),
        new("cancelled", "Ev", [("SSC-3003", 1), ("SSC-6002-65", 1)], TimeSpan.FromDays(6), OrderStatus.Cancelled,
            [TimeSpan.FromDays(6) - TimeSpan.FromHours(2)]),
        new("shipped", "İş", [("SSC-3002-B", 1), ("SSC-5003-L", 2)], TimeSpan.FromDays(4), OrderStatus.Shipped,
            [TimeSpan.FromDays(4) - TimeSpan.FromHours(2), TimeSpan.FromDays(3)]),
        new("processing", "Ev", [("SSC-4001-3", 1), ("SSC-4002", 1)], TimeSpan.FromHours(30), OrderStatus.Processing,
            [TimeSpan.FromHours(28)]),
        new("placed", "Ev", [("SSC-5001-TR", 1), ("SSC-5002-KBS", 1)], TimeSpan.FromHours(3), OrderStatus.Placed, []),
    ];

    private static readonly (string Sku, int Quantity)[] CartLines = [("SSC-3003", 1), ("SSC-6001-10", 2)];

    private static readonly int[] FavoriteProductIndexes = [0, 2, 4, 5, 7, 8];

    private readonly ShowcaseClock? _clock = time as ShowcaseClock;
    private DateTimeOffset _anchor;

    private int _catalogRows, _accounts, _warehouses, _receipts, _addresses, _favorites, _cartLines, _ordersPlaced, _transitions;

    public async Task<ShowcaseSeedResult> RunAsync(CancellationToken ct)
    {
        _anchor = time.GetUtcNow();

        // Katalog (varsa dokunmaz) ve hesaplar: en eski an.
        At(TimeSpan.FromDays(14));
        using (var scope = services.CreateScope())
        {
            var catalog = await new DemoCatalogSeeder(scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>()).SeedAsync(ct);
            _catalogRows = catalog.Total;
        }

        At(TimeSpan.FromDays(14) - TimeSpan.FromMinutes(5));
        var customerId = await EnsureUserAsync(ShowcaseAccounts.CustomerEmail, ShowcaseAccounts.RoleName(ShowcaseAccounts.Customer), ct);
        var adminId = await EnsureUserAsync(ShowcaseAccounts.AdminEmail, ShowcaseAccounts.RoleName(ShowcaseAccounts.Admin), ct);
        var warehouseUserId = await EnsureUserAsync(ShowcaseAccounts.WarehouseEmail, ShowcaseAccounts.RoleName(ShowcaseAccounts.Warehouse), ct);

        At(TimeSpan.FromDays(14) - TimeSpan.FromMinutes(30));
        var warehouseId = await EnsureWarehouseAsync(adminId, ct);

        At(TimeSpan.FromDays(14) - TimeSpan.FromHours(1));
        await ReceiveStockAsync(adminId, warehouseId, ct);

        At(TimeSpan.FromDays(14) - TimeSpan.FromHours(2));
        var addressIds = await EnsureAddressesAsync(customerId, ct);

        await EnsureFavoritesAsync(customerId, ct);

        foreach (var recipe in Recipes)
        {
            await EnsureOrderAsync(recipe, customerId, warehouseUserId, addressIds[recipe.AddressLabel], ct);
        }

        At(TimeSpan.FromHours(2));
        await EnsureCartAsync(customerId, ct);

        return await SummarizeAsync(customerId, ct);
    }

    private void At(TimeSpan ago) => _clock?.Set(_anchor - ago);

    // ---- Hesaplar ----

    private async Task<Guid> EnsureUserAsync(string email, string role, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            // Başkasının önceden açtığı bir hesaba yetki VERİLMEZ: beklenen rolde değilse dur.
            if (!await users.IsInRoleAsync(existing, role))
            {
                throw new ShowcaseSeedException("vitrin e-postalarından biri başka bir hesaba ait ve beklenen rolde değil; hiçbir yetki verilmedi.");
            }

            return existing.Id;
        }

        var user = new AppUser { UserName = email, Email = email, CreatedAt = time.GetUtcNow() };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var created = await users.CreateAsync(user, NewPassword());
        if (!created.Succeeded)
        {
            throw new ShowcaseSeedException($"vitrin hesabı oluşturulamadı (hata kodları: {string.Join(',', created.Errors.Select(e => e.Code))}).");
        }

        var roled = await users.AddToRoleAsync(user, role);
        if (!roled.Succeeded)
        {
            throw new ShowcaseSeedException($"vitrin hesabına rol verilemedi (hata kodları: {string.Join(',', roled.Errors.Select(e => e.Code))}).");
        }

        await transaction.CommitAsync(ct);
        _accounts++;
        return user.Id;
    }

    /// <summary>Kimsenin bilmediği parola: 40 karakter, CSPRNG; politikayı (harf + rakam, >= 10) sağlayana kadar yeniden üretilir. Hiçbir yere yazılmaz.</summary>
    private static string NewPassword()
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        while (true)
        {
            var candidate = RandomNumberGenerator.GetString(Alphabet, 40);
            if (candidate.Any(char.IsDigit) && candidate.Any(char.IsLetter))
            {
                return candidate;
            }
        }
    }

    // ---- Depo ve stok ----

    private async Task<Guid> EnsureWarehouseAsync(Guid adminId, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();

        var active = await db.Warehouses.AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.Code).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        if (active is { } id)
        {
            return id;
        }

        if (await db.Warehouses.AnyAsync(ct))
        {
            throw new ShowcaseSeedException("depolar var ama hiçbiri aktif değil; vitrin stoğu için aktif bir depo gerekir.");
        }

        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        var result = await inventory.CreateWarehouseAsync(adminId, new NewWarehouse("ANA", "Ana Depo"), ct);
        if (result.Status != WarehouseWriteStatus.Created || result.Warehouse is null)
        {
            throw new ShowcaseSeedException($"depo oluşturulamadı ({result.Status}).");
        }

        _warehouses++;
        return result.Warehouse.Id;
    }

    private async Task ReceiveStockAsync(Guid adminId, Guid warehouseId, CancellationToken ct)
    {
        List<Guid> variantIds;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
            variantIds = await db.ProductVariants.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Sku).Select(v => v.Id).ToListAsync(ct);
        }

        foreach (var variantId in variantIds)
        {
            using var scope = services.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            var result = await inventory.ApplyStockOperationAsync(
                new StockCommand(
                    StockOperations.Receive,
                    warehouseId,
                    variantId,
                    StockPerVariant,
                    StockOperationType.Receiving,
                    ReceiveReason,
                    Reference: null,
                    OperationKey: $"showcase-receive-{warehouseId:N}-{variantId:N}",
                    ActorUserId: adminId),
                ct);
            switch (result.Status)
            {
                case StockOperationStatus.Applied:
                    _receipts++;
                    break;
                case StockOperationStatus.Replayed:
                    break;
                default:
                    throw new ShowcaseSeedException($"stok girişi reddedildi ({result.Status}).");
            }
        }
    }

    // ---- Müşteri verisi: adres, favori, sepet ----

    private async Task<Dictionary<string, Guid>> EnsureAddressesAsync(Guid customerId, CancellationToken ct)
    {
        var wanted = new[]
        {
            new AddressInput("Ev", "Demo Müşteri", "5550000001", "İstanbul", "Kadıköy", "Örnek Mahallesi, Demo Sokak No: 1 Daire: 2 (kurgusal adres)", "34710"),
            new AddressInput("İş", "Demo Müşteri", "5550000002", "Ankara", "Çankaya", "Örnek İş Merkezi, Demo Cadde No: 10 Kat: 3 (kurgusal adres)", "06680"),
        };

        using var scope = services.CreateScope();
        var addresses = scope.ServiceProvider.GetRequiredService<IAddressService>();
        var existing = await addresses.ListAsync(customerId, ct);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var input in wanted)
        {
            var found = existing.FirstOrDefault(a => a.Label == input.Label);
            if (found is not null)
            {
                ids[input.Label] = found.Id;
                continue;
            }

            var created = await addresses.CreateAsync(customerId, input, ct);
            if (created.Status != AddressOperationStatus.Ok || created.Address is null)
            {
                throw new ShowcaseSeedException($"vitrin adresi eklenemedi ({created.Status}).");
            }

            ids[input.Label] = created.Address.Id;
            _addresses++;
        }

        return ids;
    }

    private async Task EnsureFavoritesAsync(Guid customerId, CancellationToken ct)
    {
        var slugs = DemoCatalogData.Categories.SelectMany(c => c.Products).Select(p => p.Slug).ToList();
        var wanted = FavoriteProductIndexes.Where(i => i < slugs.Count).Select(i => slugs[i]).ToList();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
        var favorites = scope.ServiceProvider.GetRequiredService<IFavoriteService>();
        var productIds = await db.Products.AsNoTracking().Where(p => wanted.Contains(p.Slug)).ToDictionaryAsync(p => p.Slug, p => p.Id, ct);
        var current = (await favorites.ListIdsAsync(customerId, ct)).ProductIds.ToHashSet();

        var step = 0;
        foreach (var slug in wanted)
        {
            if (!productIds.TryGetValue(slug, out var productId) || current.Contains(productId))
            {
                continue;
            }

            // Eklenme anları günlere yayılır (en yeni favori en üstte görünür).
            At(TimeSpan.FromDays(10) - TimeSpan.FromDays(step++));
            var status = await favorites.AddAsync(customerId, productId, ct);
            if (status != FavoriteWriteStatus.Ok)
            {
                throw new ShowcaseSeedException($"vitrin favorisi eklenemedi ({status}).");
            }

            _favorites++;
        }
    }

    private async Task EnsureCartAsync(Guid customerId, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var cart = scope.ServiceProvider.GetRequiredService<ICartService>();
        var current = await cart.GetAsync(customerId, ct);
        if (current.Cart is { Items.Count: > 0 })
        {
            return; // Sepette bir şey varsa (önceki koşu ya da ziyaretçi) ezilmez.
        }

        await AddCartLinesAsync(scope, customerId, CartLines, ct);
        _cartLines += CartLines.Length;
    }

    private static async Task AddCartLinesAsync(IServiceScope scope, Guid customerId, (string Sku, int Quantity)[] lines, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
        var cart = scope.ServiceProvider.GetRequiredService<ICartService>();
        var skus = lines.Select(l => l.Sku).ToList();
        var variants = await db.ProductVariants.AsNoTracking().Where(v => skus.Contains(v.Sku)).ToDictionaryAsync(v => v.Sku, v => v.Id, ct);
        foreach (var (sku, quantity) in lines)
        {
            if (!variants.TryGetValue(sku, out var variantId))
            {
                throw new ShowcaseSeedException("vitrin sepeti için gereken demo ürün varyantı katalogda yok.");
            }

            var added = await cart.AddItemAsync(customerId, new AddCartItemCommand(variantId, quantity), ct);
            if (added.Status != CartOperationStatus.Ok)
            {
                throw new ShowcaseSeedException($"vitrin sepetine ürün eklenemedi ({added.Status}).");
            }
        }
    }

    // ---- Siparişler ----

    private async Task EnsureOrderAsync(OrderRecipe recipe, Guid customerId, Guid warehouseUserId, Guid addressId, CancellationToken ct)
    {
        var key = $"showcase-order-{recipe.KeySuffix}";

        Guid orderId;
        OrderStatus current;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
            var existing = await db.Orders.AsNoTracking()
                .Where(o => o.UserId == customerId && o.IdempotencyKey == key)
                .Select(o => new { o.Id, o.Status })
                .FirstOrDefaultAsync(ct);
            if (existing is null)
            {
                orderId = await PlaceAsync(scope, recipe, key, customerId, addressId, ct);
                current = OrderStatus.Placed;
            }
            else
            {
                orderId = existing.Id;
                current = existing.Status;
            }
        }

        // Hedefe giden yol; siparişin durumu yolun dışındaysa (ör. ziyaretçi iptal ettiyse) dokunulmaz.
        var path = recipe.Target == OrderStatus.Cancelled
            ? new[] { OrderStatus.Placed, OrderStatus.Cancelled }
            : new[] { OrderStatus.Placed, OrderStatus.Processing, OrderStatus.Shipped, OrderStatus.Completed };
        var position = Array.IndexOf(path, current);
        var targetPosition = Array.IndexOf(path, recipe.Target);
        if (position < 0)
        {
            return;
        }

        for (var next = position + 1; next <= targetPosition; next++)
        {
            At(recipe.StepsAgo[next - 1]);
            using var scope = services.CreateScope();
            var statuses = scope.ServiceProvider.GetRequiredService<IOrderStatusService>();
            var command = path[next] == OrderStatus.Cancelled
                ? new OrderStatusCommand(orderId, customerId, OrderStatus.Cancelled, OrderActor.Customer, customerId, Reason: null)
                : new OrderStatusCommand(orderId, OwnerUserId: null, path[next], OrderActor.Warehouse, warehouseUserId, Reason: null);
            var result = await statuses.ChangeStatusAsync(command, ct);
            if (result.Status != OrderOperationStatus.Changed)
            {
                throw new ShowcaseSeedException($"vitrin siparişi durumu ilerletilemedi ({result.Status}).");
            }

            _transitions++;
        }
    }

    /// <summary>Sepeti bu siparişin satırlarına getirir, önizlemeden quote alır, sipariş servisiyle verir.</summary>
    private async Task<Guid> PlaceAsync(IServiceScope scope, OrderRecipe recipe, string key, Guid customerId, Guid addressId, CancellationToken ct)
    {
        var cart = scope.ServiceProvider.GetRequiredService<ICartService>();

        // Yarım kalmış bir koşuda sepette artık satır olabilir: sipariş yalnız bu tarifin satırlarını içermeli.
        At(recipe.PlacedAgo + TimeSpan.FromMinutes(10));
        var before = await cart.GetAsync(customerId, ct);
        foreach (var item in before.Cart?.Items ?? [])
        {
            var removed = await cart.RemoveItemAsync(customerId, item.Id, ct);
            if (removed.Status != CartOperationStatus.Ok)
            {
                throw new ShowcaseSeedException($"vitrin sepeti temizlenemedi ({removed.Status}).");
            }
        }

        await AddCartLinesAsync(scope, customerId, recipe.Lines, ct);

        At(recipe.PlacedAgo + TimeSpan.FromMinutes(2));
        var preview = await scope.ServiceProvider.GetRequiredService<ICheckoutPreviewService>().PreviewAsync(customerId, addressId, ct);
        if (preview.Status != CheckoutPreviewStatus.Ok || preview.Preview is not { CanProceed: true, Quote: { } quote })
        {
            throw new ShowcaseSeedException($"vitrin sipariş önizlemesi ilerleyemedi ({preview.Status}).");
        }

        At(recipe.PlacedAgo);
        var placed = await scope.ServiceProvider.GetRequiredService<IOrderPlacementService>()
            .PlaceAsync(new PlaceOrderCommand(customerId, key, addressId, quote), ct);
        if (placed.Status != OrderOperationStatus.Placed || placed.Order is null)
        {
            throw new ShowcaseSeedException($"vitrin siparişi verilemedi ({placed.Status}).");
        }

        _ordersPlaced++;
        return placed.Order.Id;
    }

    private async Task<ShowcaseSeedResult> SummarizeAsync(Guid customerId, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
        var orders = await db.Orders.AsNoTracking().CountAsync(o => o.UserId == customerId, ct);
        var favorites = await db.CustomerFavorites.AsNoTracking().CountAsync(f => f.UserId == customerId, ct);
        var addresses = await db.CustomerAddresses.AsNoTracking().CountAsync(a => a.UserId == customerId, ct);
        return new ShowcaseSeedResult(
            _catalogRows, _accounts, _warehouses, _receipts, _addresses, _favorites, _cartLines, _ordersPlaced, _transitions, orders, favorites, addresses);
    }
}

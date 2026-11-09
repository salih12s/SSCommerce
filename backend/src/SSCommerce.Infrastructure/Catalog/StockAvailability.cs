using SSCommerce.Application.Catalog;
using SSCommerce.Domain.Inventory;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Catalog;

/// <summary>
/// Public stok tanımının TEK kaynağı (D-048): bir varyantın "eldeki"si YALNIZ AKTİF depoların bakiyeleri toplamıdır; bakiye satırı yoksa 0.
/// Public katalog (<see cref="CatalogQueries"/>) ve sepet/checkout (Carts) bu sınıfı kullanır; iki yerde ayrı tanım yazılmaz, böylece
/// "vitrinde stokta" ile "sepette stokta" asla ayrışmaz. Cap (<see cref="InventoryRules.PublicAvailabilityCap"/>) hesaplanan değere
/// <see cref="Cap"/> ile uygulanır; cap üstü gerçek miktar yanıta çıkmaz.
/// </summary>
internal static class StockAvailability
{
    /// <summary>
    /// Aktif depolardaki bakiye satırları. Sorguya <c>db</c> kapanımıyla girer (EF bunu tek bir tablo kaynağı olarak çevirir) ve
    /// çağıran <c>.Where(b =&gt; b.ProductVariantId == v.Id)</c> ile ilişkilendirir; her kullanım yeri tek SQL alt sorgusudur (N+1 yok).
    /// </summary>
    public static IQueryable<InventoryBalance> ActiveBalances(SSCommerceDbContext db) =>
        db.InventoryBalances.Where(b => db.Warehouses.Any(w => w.Id == b.WarehouseId && w.IsActive));

    /// <summary>Ham toplamı public cap'e indirir (negatif olamaz).</summary>
    public static int Cap(long availableOnHand) => PublicAvailability.Cap(availableOnHand);
}

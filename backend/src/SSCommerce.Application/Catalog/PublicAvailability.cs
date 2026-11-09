using SSCommerce.Domain.Inventory;

namespace SSCommerce.Application.Catalog;

/// <summary>
/// Public stok miktarının TEK cap kuralı (D-048): <c>min(toplam, PublicAvailabilityCap)</c>, negatif olamaz. Katalog yanıtları ve sepet
/// hesabı (CartCalculator) bunu kullanır; cap üstü gerçek miktar hiçbir yanıtta yer almaz. Toplamın kendisi (yalnız aktif depolar)
/// Infrastructure'da <c>StockAvailability</c> tarafından tek tanımla hesaplanır.
/// </summary>
public static class PublicAvailability
{
    public static int Cap(long availableOnHand) => (int)Math.Min(Math.Max(availableOnHand, 0), InventoryRules.PublicAvailabilityCap);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SSCommerce.Infrastructure.Orders;

/// <summary>
/// Sipariş stok defteri sabitleri: tek yerde. <c>operation_key</c> değerleri <c>stock_movements</c> üzerindeki UNIQUE indeksle tekilleştirilir;
/// <c>order-</c> öneki admin anahtarlarına kapalıdır (<c>InventoryRequestValidator.ReservedKeyPrefix</c>), böylece bir admin anahtarı
/// sonradan gelen bir düşümü/iadeyi ele geçiremez. Bir dağıtım için bir düşüm ve en çok BİR iade anahtarı vardır: çift iade DB'de imkânsızdır.
/// </summary>
internal static class OrderLedger
{
    public const string PlacementReason = "Sipariş stok düşümü";
    public const string CancellationReason = "Sipariş iptali stok iadesi";

    public static string PlacementKey(Guid allocationId) => $"order-place-{allocationId:N}";

    public static string CancellationKey(Guid allocationId) => $"order-cancel-{allocationId:N}";

    /// <summary>İsteğin anlamı: hangi sipariş, hangi dağıtım, kaç adet. Aktör ve zaman dışarıda (stok parmak izi ilkesiyle aynı).</summary>
    public static string PlacementFingerprint(Guid orderId, Guid allocationId, int delta) =>
        Hash($"v1|order|{orderId:N}|{allocationId:N}|{delta.ToString(CultureInfo.InvariantCulture)}");

    public static string CancellationFingerprint(Guid orderId, Guid allocationId, int delta) =>
        Hash($"v1|order-cancel|{orderId:N}|{allocationId:N}|{delta.ToString(CultureInfo.InvariantCulture)}");

    private static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SSCommerce.Application.Carts;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Ordering;

namespace SSCommerce.Application.Orders;

/// <summary>
/// Fiyat onayı (quote, D-060): sunucunun sepet hesabından üretilen, SIR İÇERMEYEN özet. Önizleme ve sipariş verme AYNI fonksiyonu AYNI
/// <see cref="CartDto"/> üzerinde çağırır (tek kaynak); biri değişirse öteki de değişir, ikisi ayrışamaz.
/// Kanonik metin: <c>v1|TRY|&lt;subtotalMinor&gt;|&lt;satır&gt;;&lt;satır&gt;...</c>, satır = <c>&lt;varyantId:N&gt;:&lt;adet&gt;:&lt;birimFiyatKuruş&gt;</c>,
/// satırlar varyant kimliğine (ordinal) göre sıralı. Bu yüzden birbirini götüren iki fiyat değişimi (toplam aynı) de quote'u değiştirir.
/// Bir sır taşımaz: sahte quote ancak gerçek güncel değere eşitse işe yarar, yani fiyatı belirlemez, yalnız "bunu gördün" onayıdır.
/// </summary>
public static class OrderQuote
{
    public const string Version = "v1";

    /// <summary>Tüm satırlar <c>available</c> olmalıdır (fiyatlar dolu). Aksi halde null: önizleme "ilerlenemez" der, quote üretmez.</summary>
    public static string? Compute(CartDto cart)
    {
        ArgumentNullException.ThrowIfNull(cart);
        if (cart.Items.Count == 0 || cart.Items.Any(i => i.Status != CartLineStatus.Available || i.UnitPriceMinor is null))
        {
            return null;
        }

        return Compute(cart.SubtotalMinor, cart.Items.Select(i => (i.VariantId, i.Quantity, i.UnitPriceMinor!.Value)));
    }

    public static string Compute(long subtotalMinor, IEnumerable<(Guid VariantId, int Quantity, long UnitPriceMinor)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var canonical = new StringBuilder(Version)
            .Append('|').Append(Money.Currency)
            .Append('|').Append(subtotalMinor.ToString(CultureInfo.InvariantCulture))
            .Append('|');

        var first = true;
        foreach (var line in lines
                     .Select(l => (Key: l.VariantId.ToString("N"), l.Quantity, l.UnitPriceMinor))
                     .OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                canonical.Append(';');
            }

            first = false;
            canonical.Append(line.Key)
                .Append(':').Append(line.Quantity.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(line.UnitPriceMinor.ToString(CultureInfo.InvariantCulture));
        }

        return Hash(canonical.ToString());
    }

    internal static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

/// <summary>
/// İsteğin parmak izi (D-060): <c>SHA-256("v1|&lt;addressId:N&gt;|&lt;quote&gt;")</c>. SEPET DURUMU BİLEREK DIŞARIDADIR: sepet temizlendikten ya da fiyat
/// değiştikten sonra aynı istek yine aynı parmak izini üretir, böylece yanıtı kaybolan istek tekrar edilince kopya sipariş değil replay olur.
/// Kullanıcı kimliği de dışarıdadır: anahtar zaten kullanıcı başına tektir.
/// </summary>
public static class OrderFingerprint
{
    public static string Compute(Guid addressId, string quote) =>
        OrderQuote.Hash($"{OrderQuote.Version}|{addressId:N}|{quote}");
}

/// <summary>
/// Veritabanının ZORLAMADIĞI iki sipariş değişmezini kodda zorlar (saf; servis yazmadan önce çağırır, test doğrudan çağırır):
/// <list type="bullet">
/// <item><c>orders.item_subtotal</c> = kalem ara toplamlarının toplamı (CHECK yalnız satır içi <c>unit_price * quantity</c>'yi bilir).</item>
/// <item>Her dağıtım, kendi kaleminin VARYANTINA aittir ve parçaların toplamı kalem adedine eşittir (FK yalnız var olmayı garanti eder).</item>
/// </list>
/// İhlal bir programlama hatasıdır: istisna fırlar, transaction geri alınır, hiçbir şey yazılmaz.
/// </summary>
public static class OrderInvariants
{
    public sealed record ItemFacts(Guid ItemId, Guid VariantId, int Quantity, decimal LineSubtotal);

    public sealed record AllocationFacts(Guid ItemId, Guid VariantId, int Quantity);

    public static void Verify(decimal itemSubtotal, IReadOnlyCollection<ItemFacts> items, IReadOnlyCollection<AllocationFacts> allocations)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(allocations);

        if (items.Sum(i => i.LineSubtotal) != itemSubtotal)
        {
            throw new OrderRuleException("Sipariş ara toplamı kalem ara toplamlarının toplamına eşit değil.");
        }

        var byId = items.ToDictionary(i => i.ItemId);
        foreach (var allocation in allocations)
        {
            if (!byId.TryGetValue(allocation.ItemId, out var item))
            {
                throw new OrderRuleException("Dağıtım var olmayan bir kaleme bağlı.");
            }

            if (allocation.VariantId != item.VariantId)
            {
                throw new OrderRuleException("Dağıtımın varyantı kalemin varyantıyla aynı değil.");
            }
        }

        foreach (var item in items)
        {
            if (allocations.Where(a => a.ItemId == item.ItemId).Sum(a => (long)a.Quantity) != item.Quantity)
            {
                throw new OrderRuleException("Dağıtım adetlerinin toplamı kalem adedine eşit değil.");
            }
        }
    }
}

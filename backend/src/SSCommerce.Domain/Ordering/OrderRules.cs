using System.Text.RegularExpressions;
using SSCommerce.Domain.Cart;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Customers;

namespace SSCommerce.Domain.Ordering;

/// <summary>
/// Sipariş doğrulama kuralları ve sabitleri (D-059..D-064, docs/CONTRACT_PHASE_7.md). Aynı sınırlar veritabanında CHECK olarak da vardır
/// (savunma derinliği); bir sabiti değiştirirsen EF konfigürasyonundaki CHECK için yeni migration gerekir.
/// Para <c>decimal</c> (TL, ölçek 2); kuruş dönüşümü yalnız API katmanındadır (<see cref="Money"/>).
/// </summary>
public static partial class OrderRules
{
    /// <summary><c>orders.order_number</c> identity başlangıcı; arayüzde <c>SS-100001</c> görünür, rotalarda kullanılmaz (D-064).</summary>
    public const long InitialOrderNumber = 100001;

    public const string OrderNumberPrefix = "SS-";

    public const string Currency = Money.Currency;

    /// <summary>Faz 7'de kargo hesaplanmaz: sipariş her zaman bu durumdadır, <c>shipping_amount</c> ve <c>grand_total</c> NULL kalır (D-061).</summary>
    public const string ShippingNotCalculated = "shippingNotCalculated";

    /// <summary>Bir satırdaki en büyük adet; sepet satır sınırıyla aynıdır. DB: <c>ck_order_items_quantity_range</c>.</summary>
    public const int MaxLineQuantity = CartRules.MaxLineQuantity;

    /// <summary>Durum geçmişi nedeni üst sınırı. Müşteriye gösterilmez.</summary>
    public const int ReasonMaxLength = 200;

    public const int IdempotencyKeyMinLength = 16;
    public const int IdempotencyKeyMaxLength = 128;

    /// <summary>İstek parmak izi (SHA-256 hex): 64 küçük harf/rakam.</summary>
    public const int RequestFingerprintLength = 64;

    public const string IdempotencyKeyPattern = "^[A-Za-z0-9_-]{16,128}$";
    public const string RequestFingerprintPattern = "^[0-9a-f]{64}$";

    // .NET'te "$" sondaki satır sonundan önce de eşleşir; "\z" yalnız metnin gerçek sonudur. PostgreSQL "~" "$" ile tam sonu ister:
    // *Pattern sabitleri (DB CHECK) DEĞİŞMEZ, yalnız .NET regex'i sıkılaştırılır.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{16,128}\z")]
    private static partial Regex IdempotencyKeyRegex();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex RequestFingerprintRegex();

    /// <summary>Veritabanı sıra numarasını arayüz biçimine çevirir: 100001 → <c>SS-100001</c>.</summary>
    public static string FormatOrderNumber(long orderNumber) =>
        orderNumber < InitialOrderNumber
            ? throw new OrderRuleException("Sipariş numarası geçersiz.")
            : OrderNumberPrefix + orderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string RequireIdempotencyKey(string? value)
    {
        if (value is null || !IdempotencyKeyRegex().IsMatch(value))
        {
            throw new OrderRuleException(
                $"Idempotency anahtarı geçersiz (harf, rakam, _ ve -; {IdempotencyKeyMinLength}-{IdempotencyKeyMaxLength} karakter).");
        }

        return value;
    }

    public static string RequireRequestFingerprint(string? value)
    {
        if (value is null || !RequestFingerprintRegex().IsMatch(value))
        {
            throw new OrderRuleException("İstek parmak izi 64 karakterlik küçük harfli SHA-256 hex değeri olmalı.");
        }

        return value;
    }

    public static int RequireQuantity(int quantity)
    {
        if (quantity is < 1 or > MaxLineQuantity)
        {
            throw new OrderRuleException($"Adet 1 ile {MaxLineQuantity} arasında olmalı.");
        }

        return quantity;
    }

    /// <summary>Birim fiyat kopyası: 0 ≤ tutar ≤ <see cref="Money.MaxAmount"/>, en çok 2 ondalık basamak.</summary>
    public static decimal RequireUnitPrice(decimal price) => Wrap(() => Money.EnsureValid(price));

    /// <summary>Satır ara toplamı = birim fiyat × adet. Tutar <see cref="Money.MaxAmount"/>'ı aşarsa kural ihlalidir.</summary>
    public static decimal ComputeLineSubtotal(decimal unitPrice, int quantity)
    {
        var validPrice = RequireUnitPrice(unitPrice);
        var validQuantity = RequireQuantity(quantity);
        return Wrap(() => Money.EnsureValid(validPrice * validQuantity));
    }

    /// <summary>Sipariş ara toplamı: 0 ≤ tutar ≤ <see cref="Money.MaxAmount"/>.</summary>
    public static decimal RequireItemSubtotal(decimal subtotal) => Wrap(() => Money.EnsureValid(subtotal));

    /// <summary>Satır ara toplamlarının toplamı; üst sınırı aşarsa ya da liste boşsa kural ihlalidir (boş sipariş yoktur).</summary>
    public static decimal SumLineSubtotals(IEnumerable<decimal> lineSubtotals)
    {
        ArgumentNullException.ThrowIfNull(lineSubtotals);
        var total = 0m;
        var count = 0;
        foreach (var subtotal in lineSubtotals)
        {
            total += Wrap(() => Money.EnsureValid(subtotal));
            count++;
            if (total > Money.MaxAmount)
            {
                throw new OrderRuleException("Sipariş tutarı izin verilen üst sınırı aşıyor.");
            }
        }

        return count == 0 ? throw new OrderRuleException("Sipariş en az bir satır içermeli.") : RequireItemSubtotal(total);
    }

    /// <summary>Boş ya da <c>null</c> → <c>null</c>; doluysa kırpılmış metin, en çok <see cref="ReasonMaxLength"/> karakter.</summary>
    public static string? OptionalReason(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > ReasonMaxLength)
        {
            throw new OrderRuleException($"Neden: en çok {ReasonMaxLength} karakter.");
        }

        return trimmed;
    }

    // ---- Katalog / adres kuralları: ihlal sipariş kural istisnası olarak yükselir (tek istisna türü, servis tek yerde yakalar) ----

    internal static string RequireSku(string? value) => Wrap(() => CatalogRules.RequireSku(value));

    internal static string RequireCatalogText(string? value, int maxLength, string what) =>
        Wrap(() => CatalogRules.RequireText(value, maxLength, what));

    internal static string RequireSlug(string? value, int maxLength, string what) =>
        Wrap(() => CatalogRules.RequireSlug(value, maxLength, what));

    internal static string RequireImagePath(string? value, int maxLength, string what) =>
        Wrap(() => CatalogRules.RequireImagePath(value, maxLength, what));

    internal static string RequireAddressText(string? value, int maxLength, string what) =>
        Wrap(() => AddressRules.RequireText(value, maxLength, what));

    internal static string RequirePhone(string? value) => Wrap(() => AddressRules.NormalizePhone(value));

    internal static string RequireProvince(string? value) => Wrap(() => AddressRules.RequireProvince(value));

    internal static string? OptionalPostalCode(string? value) => Wrap(() => AddressRules.OptionalPostalCode(value));

    private static T Wrap<T>(Func<T> rule)
    {
        try
        {
            return rule();
        }
        catch (Exception exception) when (exception is CatalogRuleException or AddressRuleException or CartRuleException)
        {
            throw new OrderRuleException(exception.Message);
        }
    }
}

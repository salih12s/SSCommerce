using System.Text.RegularExpressions;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Domain.Inventory;

/// <summary>
/// Stok doğrulama kuralları ve sabitleri (D-045..D-048, docs/CONTRACT_PHASES_5_6.md). Aynı sınırlar veritabanında CHECK olarak da
/// vardır (savunma derinliği); bir sabiti değiştirirsen EF konfigürasyonundaki CHECK için yeni migration gerekir.
/// </summary>
public static partial class InventoryRules
{
    /// <summary>Bir (depo, varyant) bakiyesinin üst sınırı. DB: <c>ck_inventory_balances_on_hand_range</c>.</summary>
    public const int MaxOnHandQuantity = 1_000_000;

    /// <summary>Tek hareketin mutlak değer üst sınırı. DB: <c>ck_stock_movements_quantity_delta_range</c>.</summary>
    public const int MaxAbsoluteDelta = 1_000_000;

    /// <summary>Public yanıtta gösterilen en yüksek stok (sepet satır sınırıyla aynı). DB'de karşılığı yok.</summary>
    public const int PublicAvailabilityCap = 20;

    public const int WarehouseCodeMaxLength = 32;
    public const int WarehouseNameMaxLength = 100;
    public const int ReasonMaxLength = 500;
    public const int ReferenceMaxLength = 128;
    public const int OperationKeyMinLength = 16;
    public const int OperationKeyMaxLength = 128;

    /// <summary>Kanonik isteğin SHA-256 özeti: 64 küçük harfli hex karakter.</summary>
    public const int RequestFingerprintLength = 64;

    public const string WarehouseCodePattern = "^[A-Z0-9][A-Z0-9-]{1,31}$";
    public const string OperationKeyPattern = "^[A-Za-z0-9_-]{16,128}$";
    public const string RequestFingerprintPattern = "^[0-9a-f]{64}$";

    // .NET'te "$" sondaki satır sonundan önce de eşleşir; "\z" yalnız metnin gerçek sonudur. PostgreSQL "~" "$" ile tam sonu ister:
    // *Pattern sabitleri (DB CHECK) DEĞİŞMEZ, yalnız .NET regex'i sıkılaştırılır.
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9-]{1,31}\z")]
    private static partial Regex WarehouseCodeRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{16,128}\z")]
    private static partial Regex OperationKeyRegex();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex RequestFingerprintRegex();

    public static string RequireWarehouseCode(string? value)
    {
        if (value is null || !WarehouseCodeRegex().IsMatch(value))
        {
            throw new InventoryRuleException("Depo kodu geçersiz (büyük harf, rakam ve tire; 2-32 karakter, tireyle başlayamaz).");
        }

        return value;
    }

    public static string RequireWarehouseName(string? value) => RequireText(value, WarehouseNameMaxLength, "Depo adı");

    public static string RequireReason(string? value) => RequireText(value, ReasonMaxLength, "Neden");

    public static string? OptionalReference(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > ReferenceMaxLength)
        {
            throw new InventoryRuleException($"Referans: en çok {ReferenceMaxLength} karakter.");
        }

        return trimmed;
    }

    public static string RequireOperationKey(string? value)
    {
        if (value is null || !OperationKeyRegex().IsMatch(value))
        {
            throw new InventoryRuleException($"İşlem anahtarı geçersiz (harf, rakam, _ ve -; {OperationKeyMinLength}-{OperationKeyMaxLength} karakter).");
        }

        return value;
    }

    public static string RequireRequestFingerprint(string? value)
    {
        if (value is null || !RequestFingerprintRegex().IsMatch(value))
        {
            throw new InventoryRuleException("İstek parmak izi 64 karakterlik küçük harfli SHA-256 hex değeri olmalı.");
        }

        return value;
    }

    /// <summary>Hareketteki SKU kopyası katalog SKU kuralına uyar; ihlal stok kural istisnası olarak yükselir.</summary>
    public static string RequireSku(string? value)
    {
        try
        {
            return CatalogRules.RequireSku(value);
        }
        catch (CatalogRuleException exception)
        {
            throw new InventoryRuleException(exception.Message);
        }
    }

    public static int RequireOnHand(int value)
    {
        if (value is < 0 or > MaxOnHandQuantity)
        {
            throw new InventoryRuleException($"Eldeki miktar 0 ile {MaxOnHandQuantity} arasında olmalı.");
        }

        return value;
    }

    public static int RequireQuantityDelta(int value)
    {
        if (value == 0 || value < -MaxAbsoluteDelta || value > MaxAbsoluteDelta)
        {
            throw new InventoryRuleException($"Miktar değişimi 0 olamaz ve mutlak değeri en çok {MaxAbsoluteDelta} olabilir.");
        }

        return value;
    }

    private static string RequireText(string? value, int maxLength, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            throw new InventoryRuleException($"{what}: boş olamaz, en çok {maxLength} karakter.");
        }

        return trimmed;
    }
}

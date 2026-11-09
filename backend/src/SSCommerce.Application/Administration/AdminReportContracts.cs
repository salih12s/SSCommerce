using System.Globalization;
using System.Text.Json;
using SSCommerce.Application.Inventory;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Administration;

namespace SSCommerce.Application.Administration;

// Pano ve raporlar (Faz 8, docs/CONTRACT_PHASE_8.md "Dashboard and reports"). SALT OKUNUR: hiçbir GET bir şey yazmaz.
// Para kuruştur; "gelir/ciro/tahsilat" alanı YOKTUR (ödeme kaydı yok): yalnız `unpaidSubtotalMinor` (ödenmemiş sipariş ara toplamı).

public sealed record DashboardOrdersDto(long Placed, long Processing, long Shipped, long Completed, long Cancelled);

/// <summary>Açık = placed + processing. <c>UnpaidSubtotalMinor</c> kalem ara toplamlarının toplamıdır (kargo/vergi yok, ödeme alınmadı).</summary>
public sealed record DashboardOpenOrdersDto(long Count, long UnpaidSubtotalMinor, long AgedCount, int AgedHours, DateTimeOffset? OldestCreatedAt);

public sealed record DashboardCatalogDto(long Products, long ActiveProducts, long Variants, long ActiveVariants);

/// <summary>Satılabilir varyant (varyant + ürün + kategori aktif) ve AKTİF depolar. Az: 0 &lt; stok ≤ eşik. Tükenen: stok = 0 (ya da bakiye satırı yok).</summary>
public sealed record DashboardStockDto(int Threshold, long LowStockVariantCount, long OutOfStockVariantCount);

public sealed record DashboardOrderEventDto(
    Guid OrderId,
    string OrderNumber,
    string? FromStatus,
    string ToStatus,
    string ActorKind,
    DateTimeOffset OccurredAt);

public sealed record DashboardDto(
    DateTimeOffset GeneratedAt,
    DashboardOrdersDto Orders,
    DashboardOpenOrdersDto OpenOrders,
    DashboardCatalogDto Catalog,
    DashboardStockDto Stock,
    IReadOnlyList<AdminOrderSummaryDto> AgedOpenOrders,
    IReadOnlyList<StockMovementDto> RecentMovements,
    IReadOnlyList<DashboardOrderEventDto> RecentOrderEvents);

public sealed record DashboardQuery(int LowStockThreshold, int AgedHours);

public sealed record LowStockRowDto(Guid VariantId, string Sku, string ProductName, string DisplayName, long OnHandTotal);

public sealed record LowStockQuery(int Threshold, int Page, int PageSize);

/// <summary><c>Changes</c> ham JSON nesnesidir (kişisel veri içermez). <c>ActorEmail</c> yalnız aktör şu an personelse dolu.</summary>
public sealed record AuditRowDto(
    Guid Id,
    Guid ActorUserId,
    string? ActorEmail,
    string Action,
    string TargetType,
    Guid TargetId,
    JsonElement Changes,
    DateTimeOffset OccurredAt);

public sealed record AuditQuery(string? TargetType, Guid? TargetId, int Page, int PageSize);

public interface IDashboardQueries
{
    /// <summary>Tek bağlantıda ARDIŞIK sorgular (paralel değil); listeler en çok 10 satır.</summary>
    Task<DashboardDto> GetDashboardAsync(DashboardQuery query, CancellationToken cancellationToken);

    Task<PagedDto<LowStockRowDto>> GetLowStockAsync(LowStockQuery query, CancellationToken cancellationToken);

    Task<PagedDto<AuditRowDto>> GetAuditAsync(AuditQuery query, CancellationToken cancellationToken);
}

/// <summary>Pano/rapor sorgu parametrelerinin SAF doğrulaması.</summary>
public static class AdminReportValidator
{
    public const int DefaultLowStockThreshold = 5;
    public const int DefaultAgedHours = 48;
    public const int MinThreshold = 1;
    public const int MaxThreshold = 1000;
    public const int MinAgedHours = 1;
    public const int MaxAgedHours = 720;
    public const int DefaultPage = 1;

    /// <summary>Rapor sayfa üst sınırı (pano değil, sayfalı raporlar): 1..1000.</summary>
    public const int MaxPage = 1000;

    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public static RequestValidation<DashboardQuery> ValidateDashboard(string? lowStockThreshold, string? agedHours)
    {
        var errors = new Dictionary<string, string[]>();
        var threshold = ParseInt(lowStockThreshold, "lowStockThreshold", DefaultLowStockThreshold, MinThreshold, MaxThreshold, errors);
        var hours = ParseInt(agedHours, "agedHours", DefaultAgedHours, MinAgedHours, MaxAgedHours, errors);
        return errors.Count > 0
            ? RequestValidation<DashboardQuery>.Invalid(errors)
            : RequestValidation<DashboardQuery>.Valid(new DashboardQuery(threshold, hours));
    }

    public static RequestValidation<LowStockQuery> ValidateLowStock(string? threshold, string? page, string? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        var parsedThreshold = ParseInt(threshold, "threshold", DefaultLowStockThreshold, MinThreshold, MaxThreshold, errors);
        var parsedPage = ParseInt(page, "page", DefaultPage, 1, MaxPage, errors);
        var parsedSize = ParseInt(pageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<LowStockQuery>.Invalid(errors)
            : RequestValidation<LowStockQuery>.Valid(new LowStockQuery(parsedThreshold, parsedPage, parsedSize));
    }

    public static RequestValidation<AuditQuery> ValidateAudit(string? targetType, string? targetId, string? page, string? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        string? type = null;
        var rawType = targetType?.Trim();
        if (!string.IsNullOrEmpty(rawType))
        {
            if (AdminAuditRules.TargetTypes.Contains(rawType, StringComparer.Ordinal))
            {
                type = rawType;
            }
            else
            {
                errors["targetType"] = [$"targetType şunlardan biri olmalıdır: {string.Join(", ", AdminAuditRules.TargetTypes)}."];
            }
        }

        Guid? id = null;
        var rawId = targetId?.Trim();
        if (!string.IsNullOrEmpty(rawId))
        {
            if (Guid.TryParse(rawId, out var parsed) && parsed != Guid.Empty)
            {
                id = parsed;
            }
            else
            {
                errors["targetId"] = ["targetId geçerli bir kimlik olmalıdır."];
            }
        }

        var parsedPage = ParseInt(page, "page", DefaultPage, 1, MaxPage, errors);
        var parsedSize = ParseInt(pageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<AuditQuery>.Invalid(errors)
            : RequestValidation<AuditQuery>.Valid(new AuditQuery(type, id, parsedPage, parsedSize));
    }

    private static int ParseInt(string? raw, string name, int defaultValue, int min, int max, Dictionary<string, string[]> errors)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return defaultValue;
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            errors[name] = [$"{name} tam sayı olmalıdır."];
            return defaultValue;
        }

        if (value < min || value > max)
        {
            errors[name] = [$"{name} {min} ile {max} arasında olmalıdır."];
            return defaultValue;
        }

        return (int)value;
    }
}

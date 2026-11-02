using System.Globalization;
using SSCommerce.Application.Common;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Ordering;

namespace SSCommerce.Application.Orders;

/// <summary>
/// Sipariş isteklerinin doğrulaması: SAF fonksiyonlar (girdi → çıktı, veritabanı/ağ yok). Alan kuralları <see cref="OrderRules"/>'tadır
/// (DB CHECK'leriyle aynı sabitler). Tüm alanları tek geçişte doğrular, ilk hatada durmaz. Hata iletileri girdiyi (anahtar, adres, quote) YANSITMAZ.
/// </summary>
public static class OrderRequestValidator
{
    public const string IdempotencyKeyField = "Idempotency-Key";
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 10;
    public const int MaxPage = 10_000;
    public const int MaxPageSize = 50;
    public const int QuoteLength = 64;

    public static RequestValidation<PlaceOrderCommand> ValidatePlace(PlaceOrderRequest request, string? idempotencyKey, Guid userId)
    {
        var errors = new Dictionary<string, string[]>();

        string? key = null;
        if (idempotencyKey is null)
        {
            errors[IdempotencyKeyField] = [$"{IdempotencyKeyField} başlığı zorunludur."];
        }
        else
        {
            try
            {
                key = OrderRules.RequireIdempotencyKey(idempotencyKey);
            }
            catch (OrderRuleException exception)
            {
                errors[IdempotencyKeyField] = [exception.Message];
            }
        }

        var addressId = Guid.Empty;
        if (request.AddressId is not { } id || id == Guid.Empty)
        {
            errors["addressId"] = ["addressId zorunludur."];
        }
        else
        {
            addressId = id;
        }

        string? quote = null;
        if (string.IsNullOrEmpty(request.Quote))
        {
            errors["quote"] = ["quote zorunludur."];
        }
        else if (!IsLowerHex(request.Quote, QuoteLength))
        {
            errors["quote"] = [$"quote {QuoteLength} karakterlik küçük harfli onaltılık değer olmalıdır."];
        }
        else
        {
            quote = request.Quote;
        }

        return errors.Count > 0
            ? RequestValidation<PlaceOrderCommand>.Invalid(errors)
            : RequestValidation<PlaceOrderCommand>.Valid(new PlaceOrderCommand(userId, key!, addressId, quote!));
    }

    /// <summary>Rota kimliği. Bozuk ya da boş kimlik 400'dür (var olmayan sipariş değil, geçersiz istek).</summary>
    public static (Guid? Id, Dictionary<string, string[]>? Errors) ValidateOrderId(string? raw) =>
        Guid.TryParse(raw, out var id) && id != Guid.Empty
            ? (id, null)
            : (null, new Dictionary<string, string[]> { ["id"] = ["id geçerli bir kimlik olmalıdır."] });

    public static RequestValidation<OrderListQuery> ValidateList(string? page, string? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        var parsedPage = ParseInt(page, "page", DefaultPage, 1, MaxPage, errors);
        var parsedPageSize = ParseInt(pageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<OrderListQuery>.Invalid(errors)
            : RequestValidation<OrderListQuery>.Valid(new OrderListQuery(parsedPage, parsedPageSize));
    }

    public static RequestValidation<AdminOrderListQuery> ValidateAdminList(string? status, string? page, string? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        OrderStatus? parsedStatus = null;
        var rawStatus = status?.Trim();
        if (!string.IsNullOrEmpty(rawStatus))
        {
            if (OrderStatuses.TryFromWire(rawStatus, out var known))
            {
                parsedStatus = known;
            }
            else
            {
                errors["status"] = [$"status şunlardan biri olmalıdır: {string.Join(", ", OrderStatuses.All)}."];
            }
        }

        var parsedPage = ParseInt(page, "page", DefaultPage, 1, MaxPage, errors);
        var parsedPageSize = ParseInt(pageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<AdminOrderListQuery>.Invalid(errors)
            : RequestValidation<AdminOrderListQuery>.Valid(new AdminOrderListQuery(parsedStatus, parsedPage, parsedPageSize));
    }

    /// <summary>Personel durum isteği: hedef durum ve isteğe bağlı neden. Aktör rolü İSTEKTEN alınmaz (endpoint principal'den çıkarır).</summary>
    public static RequestValidation<StaffStatusChange> ValidateStaffStatus(StaffStatusRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        OrderStatus target = default;
        if (string.IsNullOrWhiteSpace(request.Status))
        {
            errors["status"] = ["status zorunludur."];
        }
        else if (!OrderStatuses.TryFromWire(request.Status, out target))
        {
            errors["status"] = [$"status şunlardan biri olmalıdır: {string.Join(", ", OrderStatuses.All)}."];
        }

        string? reason = null;
        if (TextRules.ContainsUnsafeCharacter(request.Reason))
        {
            errors["reason"] = ["reason kontrol, biçim ya da satır ayırıcı karakter içeremez."];
        }
        else
        {
            try
            {
                reason = OrderRules.OptionalReason(request.Reason);
            }
            catch (OrderRuleException exception)
            {
                errors["reason"] = [exception.Message];
            }
        }

        return errors.Count > 0
            ? RequestValidation<StaffStatusChange>.Invalid(errors)
            : RequestValidation<StaffStatusChange>.Valid(new StaffStatusChange(target, reason));
    }

    private static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
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

/// <summary>Doğrulanmış personel durum isteği.</summary>
public sealed record StaffStatusChange(OrderStatus Target, string? Reason);

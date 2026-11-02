using System.Globalization;
using SSCommerce.Application.Common;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Inventory;

namespace SSCommerce.Application.Inventory;

/// <summary>Doğrulama sonucu: geçerliyse <see cref="Value"/> dolu, değilse alan bazlı <see cref="Errors"/>.</summary>
public sealed class RequestValidation<T> where T : class
{
    private RequestValidation(T? value, IReadOnlyDictionary<string, string[]> errors)
    {
        Value = value;
        Errors = errors;
    }

    public T? Value { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public bool IsValid => Value is not null;

    internal static RequestValidation<T> Valid(T value) => new(value, new Dictionary<string, string[]>());

    internal static RequestValidation<T> Invalid(Dictionary<string, string[]> errors) => new(null, errors);
}

/// <summary>
/// Stok isteklerinin doğrulaması: SAF fonksiyonlar (girdi → çıktı, veritabanı/ağ yok). Tüm alanları tek geçişte doğrular, ilk hatada
/// durmaz. Alan kuralları <see cref="InventoryRules"/>'tadır (DB CHECK'leriyle aynı sabitler); burada yalnız hata eşlemesi yapılır.
/// </summary>
public static class InventoryRequestValidator
{
    public const string IdempotencyKeyField = "Idempotency-Key";

    /// <summary>
    /// Sipariş akışına AYRILMIŞ anahtar öneki (<c>order-place-*</c>, <c>order-cancel-*</c>, D-062). Admin bir anahtarı bu önekle gönderirse
    /// sonradan gelen bir sipariş iptalinin tekil iade anahtarını ELE GEÇİREBİLİRDİ (iade hareketi unique ihlaliyle düşerdi).
    /// Büyük/küçük harf duyarsız reddedilir: yanlış bir ipucu bile bırakmamak için.
    /// </summary>
    public const string ReservedKeyPrefix = "order-";
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPage = 10_000;
    public const int MaxPageSize = 100;

    public static RequestValidation<StockCommand> ValidateReceive(ReceiveStockRequest request, string? idempotencyKey, Guid actorUserId)
    {
        var errors = new Dictionary<string, string[]>();
        var warehouseId = RequireGuid(request.WarehouseId, "warehouseId", errors);
        var variantId = RequireGuid(request.VariantId, "variantId", errors);
        var key = RequireKey(idempotencyKey, errors);

        var delta = 0;
        if (request.Quantity is not { } quantity)
        {
            errors["quantity"] = ["Miktar zorunludur."];
        }
        else if (quantity < 1 || quantity > InventoryRules.MaxAbsoluteDelta)
        {
            errors["quantity"] = [$"Miktar 1 ile {InventoryRules.MaxAbsoluteDelta} arasında olmalıdır."];
        }
        else
        {
            delta = (int)quantity;
        }

        var reason = CheckText(request.Reason, () => InventoryRules.RequireReason(request.Reason), "reason", errors);
        var reference = CheckText(request.Reference, () => InventoryRules.OptionalReference(request.Reference), "reference", errors);

        return errors.Count > 0
            ? RequestValidation<StockCommand>.Invalid(errors)
            : RequestValidation<StockCommand>.Valid(new StockCommand(
                StockOperations.Receive, warehouseId, variantId, delta, StockOperationType.Receiving, reason!, reference, key!, actorUserId));
    }

    public static RequestValidation<StockCommand> ValidateAdjust(AdjustStockRequest request, string? idempotencyKey, Guid actorUserId)
    {
        var errors = new Dictionary<string, string[]>();
        var warehouseId = RequireGuid(request.WarehouseId, "warehouseId", errors);
        var variantId = RequireGuid(request.VariantId, "variantId", errors);
        var key = RequireKey(idempotencyKey, errors);

        var delta = 0;
        if (request.QuantityDelta is not { } quantityDelta)
        {
            errors["quantityDelta"] = ["Miktar değişimi zorunludur."];
        }
        else if (quantityDelta == 0 || quantityDelta < -InventoryRules.MaxAbsoluteDelta || quantityDelta > InventoryRules.MaxAbsoluteDelta)
        {
            errors["quantityDelta"] = [$"Miktar değişimi 0 olamaz ve mutlak değeri en çok {InventoryRules.MaxAbsoluteDelta} olabilir."];
        }
        else
        {
            delta = (int)quantityDelta;
        }

        // Yalnız adjustment/correction; "receiving" bu uçta geçersizdir (depo girişi /receive ucundandır).
        var type = StockOperationType.Adjustment;
        if (request.Type is not (StockOperationTypes.Adjustment or StockOperationTypes.Correction))
        {
            errors["type"] = [$"Tür '{StockOperationTypes.Adjustment}' ya da '{StockOperationTypes.Correction}' olmalıdır."];
        }
        else
        {
            type = StockOperationTypes.FromWire(request.Type);
        }

        var reason = CheckText(request.Reason, () => InventoryRules.RequireReason(request.Reason), "reason", errors);
        var reference = CheckText(request.Reference, () => InventoryRules.OptionalReference(request.Reference), "reference", errors);

        return errors.Count > 0
            ? RequestValidation<StockCommand>.Invalid(errors)
            : RequestValidation<StockCommand>.Valid(new StockCommand(
                StockOperations.Adjust, warehouseId, variantId, delta, type, reason!, reference, key!, actorUserId));
    }

    public static RequestValidation<NewWarehouse> ValidateCreateWarehouse(CreateWarehouseRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var code = Check(() => InventoryRules.RequireWarehouseCode(request.Code?.Trim()), "code", errors);
        var name = CheckText(request.Name, () => InventoryRules.RequireWarehouseName(request.Name), "name", errors);
        return errors.Count > 0
            ? RequestValidation<NewWarehouse>.Invalid(errors)
            : RequestValidation<NewWarehouse>.Valid(new NewWarehouse(code!, name!));
    }

    public static RequestValidation<WarehouseChange> ValidateUpdateWarehouse(UpdateWarehouseRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        string? name = null;
        if (request.Name is not null)
        {
            name = CheckText(request.Name, () => InventoryRules.RequireWarehouseName(request.Name), "name", errors);
        }

        if (request.Name is null && request.IsActive is null)
        {
            errors["body"] = ["name ya da isActive alanlarından en az biri gönderilmelidir."];
        }

        return errors.Count > 0
            ? RequestValidation<WarehouseChange>.Invalid(errors)
            : RequestValidation<WarehouseChange>.Valid(new WarehouseChange(name, request.IsActive));
    }

    public static RequestValidation<BalanceListQuery> ValidateBalanceQuery(BalanceListRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var warehouseId = OptionalGuid(request.WarehouseId, "warehouseId", errors);
        var sku = request.Sku?.Trim();
        if (string.IsNullOrEmpty(sku))
        {
            sku = null;
        }
        else if (sku.Length > CatalogRules.SkuMaxLength || sku.Any(char.IsControl))
        {
            errors["sku"] = ["sku geçersiz."];
        }

        var page = ParseInt(request.Page, "page", DefaultPage, 1, MaxPage, errors);
        var pageSize = ParseInt(request.PageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<BalanceListQuery>.Invalid(errors)
            : RequestValidation<BalanceListQuery>.Valid(new BalanceListQuery(warehouseId, sku, page, pageSize));
    }

    public static RequestValidation<MovementListQuery> ValidateMovementQuery(MovementListRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var warehouseId = OptionalGuid(request.WarehouseId, "warehouseId", errors);
        var variantId = OptionalGuid(request.VariantId, "variantId", errors);
        var page = ParseInt(request.Page, "page", DefaultPage, 1, MaxPage, errors);
        var pageSize = ParseInt(request.PageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<MovementListQuery>.Invalid(errors)
            : RequestValidation<MovementListQuery>.Valid(new MovementListQuery(warehouseId, variantId, page, pageSize));
    }

    private static Guid RequireGuid(Guid? value, string name, Dictionary<string, string[]> errors)
    {
        if (value is not { } id || id == Guid.Empty)
        {
            errors[name] = [$"{name} zorunludur."];
            return Guid.Empty;
        }

        return id;
    }

    private static Guid? OptionalGuid(string? raw, string name, Dictionary<string, string[]> errors)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!Guid.TryParse(text, out var id) || id == Guid.Empty)
        {
            errors[name] = [$"{name} geçerli bir kimlik olmalıdır."];
            return null;
        }

        return id;
    }

    private static string? RequireKey(string? value, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            errors[IdempotencyKeyField] = [$"{IdempotencyKeyField} başlığı zorunludur."];
            return null;
        }

        if (value.StartsWith(ReservedKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            errors[IdempotencyKeyField] = [$"{IdempotencyKeyField} '{ReservedKeyPrefix}' ile başlayamaz: bu önek sipariş akışına ayrılmıştır."];
            return null;
        }

        return Check(() => InventoryRules.RequireOperationKey(value), IdempotencyKeyField, errors);
    }

    /// <summary>Alan kuralı (kırpma/uzunluk) + kontrol karakteri reddi. Kural KIRPILMIŞ değeri döndürür; iç kontrol karakterleri bu noktada yakalanır.</summary>
    private static string? CheckText(string? raw, Func<string?> rule, string field, Dictionary<string, string[]> errors)
    {
        // HAM girdi denetlenir (Trim, U+2028/U+2029 gibi ayırıcıları kenardan atardı): kontrol, biçim (Cf) ve satır/paragraf ayırıcı karakterler yasak.
        if (TextRules.ContainsUnsafeCharacter(raw))
        {
            errors[field] = [$"{field} kontrol, biçim ya da satır ayırıcı karakter (satır sonu, sekme, NUL, U+202E, U+200B...) içeremez."];
            return null;
        }

        return Check(rule, field, errors);
    }

    private static T? Check<T>(Func<T> rule, string field, Dictionary<string, string[]> errors)
    {
        try
        {
            return rule();
        }
        catch (InventoryRuleException exception)
        {
            errors[field] = [exception.Message];
            return default;
        }
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

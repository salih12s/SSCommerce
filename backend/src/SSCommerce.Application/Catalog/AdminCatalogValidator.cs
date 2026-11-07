using System.Globalization;
using System.Text;
using System.Text.Json;
using SSCommerce.Application.Administration;
using SSCommerce.Application.Common;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Application.Catalog;

/// <summary>
/// Admin katalog isteklerinin doğrulaması: SAF fonksiyonlar (JSON öğesi/metin girdi → komut çıktı; veritabanı/ağ yok). Tüm alanları tek geçişte
/// doğrular, ilk hatada durmaz. Alan kuralları Domain'dedir (<see cref="CatalogRules"/>, <see cref="Money"/>; DB CHECK'leriyle aynı sabitler),
/// burada yalnız tip denetimi, kontrol karakteri reddi (<see cref="TextRules"/>) ve hata eşlemesi yapılır. Slug ve SKU yalnız OLUŞTURMADA okunur:
/// PATCH gövdesindeki <c>slug</c>/<c>sku</c> yok sayılır (değişmezdir). Sunucu alanları (<c>id</c>, <c>isDemo</c>, <c>version</c> hariç) kayıtta yoktur.
/// </summary>
public static class AdminCatalogValidator
{
    public const int MaxCategorySortOrder = 1_000_000;
    public const int MaxImages = 8;
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPage = 10_000;
    public const int MaxPageSize = 50;
    public const int MaxSearchLength = 80;

    /// <summary>Özellik tablosu için anahtar+değer UTF-8 bayt üst sınırı: jsonb CHECK'i (16384 bayt) 500 yerine 400 olsun diye ona yaklaşmadan durulur.</summary>
    public const int MaxSpecificationPayloadBytes = 14_000;

    public static readonly string[] ProductStatuses = ["active", "inactive", "all"];

    // ---------- Kategori ----------

    public static RequestValidation<NewCategory> ValidateNewCategory(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!JsonFields.RequireObject(body, errors))
        {
            return RequestValidation<NewCategory>.Invalid(errors);
        }

        var name = RequiredText(body, "name", CatalogRules.CategoryNameMaxLength, "Kategori adı", errors);
        var slug = RequiredSlug(body, CatalogRules.CategorySlugMaxLength, "Kategori slug", errors);
        var parent = JsonFields.Guid(body, "parentId", allowNull: true, errors);
        var sortOrder = JsonFields.Integer(body, "sortOrder", -MaxCategorySortOrder, MaxCategorySortOrder, errors);
        var active = JsonFields.Bool(body, "isActive", errors);

        return errors.Count > 0
            ? RequestValidation<NewCategory>.Invalid(errors)
            : RequestValidation<NewCategory>.Valid(new NewCategory(
                name!, slug!, parent.IsSet ? parent.Value : null, (int)(sortOrder.IsSet ? sortOrder.Value : 0), active.IsSet ? active.Value : true));
    }

    public static RequestValidation<CategoryChange> ValidateCategoryChange(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!JsonFields.RequireObject(body, errors))
        {
            return RequestValidation<CategoryChange>.Invalid(errors);
        }

        var version = JsonFields.RequireVersion(body, errors);
        var name = OptionalNameChange(body, "name", CatalogRules.CategoryNameMaxLength, "Kategori adı", errors);
        var parent = JsonFields.Guid(body, "parentId", allowNull: true, errors);
        var sortOrder = JsonFields.Integer(body, "sortOrder", -MaxCategorySortOrder, MaxCategorySortOrder, errors);
        var active = JsonFields.Bool(body, "isActive", errors);

        return errors.Count > 0
            ? RequestValidation<CategoryChange>.Invalid(errors)
            : RequestValidation<CategoryChange>.Valid(new CategoryChange(version!.Value, name, parent, sortOrder, active));
    }

    // ---------- Ürün ----------

    public static RequestValidation<NewProduct> ValidateNewProduct(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!JsonFields.RequireObject(body, errors))
        {
            return RequestValidation<NewProduct>.Invalid(errors);
        }

        var categoryId = JsonFields.Guid(body, "categoryId", allowNull: false, errors);
        if (!body.TryGetProperty("categoryId", out _))
        {
            errors["categoryId"] = ["categoryId zorunludur."];
        }

        var name = RequiredText(body, "name", CatalogRules.ProductNameMaxLength, "Ürün adı", errors);
        var slug = RequiredSlug(body, CatalogRules.ProductSlugMaxLength, "Ürün slug", errors);
        var brand = OptionalTextValue(body, "brand", CatalogRules.BrandMaxLength, "Marka", errors);
        var description = OptionalTextValue(body, "description", CatalogRules.DescriptionMaxLength, "Açıklama", errors);
        var specifications = SpecificationsValue(body, errors);
        var active = JsonFields.Bool(body, "isActive", errors);

        return errors.Count > 0
            ? RequestValidation<NewProduct>.Invalid(errors)
            : RequestValidation<NewProduct>.Valid(new NewProduct(
                categoryId.Value!.Value,
                name!,
                slug!,
                brand.Value,
                description.Value,
                specifications.Value,
                // Varsayılan pasif: yeni ürün, varyant ve görsel hazır olana kadar vitrine çıkmaz.
                active.IsSet && active.Value));
    }

    public static RequestValidation<ProductChange> ValidateProductChange(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!JsonFields.RequireObject(body, errors))
        {
            return RequestValidation<ProductChange>.Invalid(errors);
        }

        var version = JsonFields.RequireVersion(body, errors);
        var category = JsonFields.Guid(body, "categoryId", allowNull: false, errors);
        var name = OptionalNameChange(body, "name", CatalogRules.ProductNameMaxLength, "Ürün adı", errors);
        var brand = OptionalTextValue(body, "brand", CatalogRules.BrandMaxLength, "Marka", errors);
        var description = OptionalTextValue(body, "description", CatalogRules.DescriptionMaxLength, "Açıklama", errors);
        var specifications = SpecificationsValue(body, errors);
        var active = JsonFields.Bool(body, "isActive", errors);

        return errors.Count > 0
            ? RequestValidation<ProductChange>.Invalid(errors)
            : RequestValidation<ProductChange>.Valid(new ProductChange(version!.Value, category, name, brand, description, specifications, active));
    }

    public static RequestValidation<ImageReplacement> ValidateImageReplacement(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!JsonFields.RequireObject(body, errors))
        {
            return RequestValidation<ImageReplacement>.Invalid(errors);
        }

        var version = JsonFields.RequireVersion(body, errors);
        var images = new List<ImageInput>();
        if (!body.TryGetProperty("images", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            errors["images"] = ["images bir dizi olmalıdır."];
        }
        else if (array.GetArrayLength() > MaxImages)
        {
            errors["images"] = [$"En çok {MaxImages} görsel olabilir."];
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                ReadImage(item, index++, images, seen, errors);
            }

            if (errors.Count == 0 && images.Count > 0 && images.Count(i => i.IsPrimary) != 1)
            {
                errors["images"] = ["Görsel varsa tam olarak biri ana görsel (isPrimary) olmalıdır."];
            }
        }

        return errors.Count > 0
            ? RequestValidation<ImageReplacement>.Invalid(errors)
            : RequestValidation<ImageReplacement>.Valid(new ImageReplacement(version!.Value, images));
    }

    private static void ReadImage(JsonElement item, int index, List<ImageInput> images, HashSet<string> seen, Dictionary<string, string[]> errors)
    {
        var prefix = $"images[{index.ToString(CultureInfo.InvariantCulture)}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors[prefix] = ["Görsel bir nesne olmalıdır."];
            return;
        }

        var local = new Dictionary<string, string[]>();
        var key = JsonFields.String(item, "storageKey", allowNull: false, local);
        var alt = JsonFields.String(item, "altText", allowNull: false, local);
        var primary = JsonFields.Bool(item, "isPrimary", local);
        string? storageKey = null;
        if (!key.IsSet)
        {
            local.TryAdd("storageKey", ["storageKey zorunludur."]);
        }
        else
        {
            try
            {
                storageKey = CatalogRules.RequireImagePath(key.Value, CatalogRules.StorageKeyMaxLength, "Görsel yolu");
            }
            catch (CatalogRuleException exception)
            {
                local["storageKey"] = [exception.Message];
            }
        }

        string? altText = null;
        if (!alt.IsSet)
        {
            local.TryAdd("altText", ["altText zorunludur."]);
        }
        else if (!local.ContainsKey("altText"))
        {
            altText = Check(() => CatalogRules.RequireText(alt.Value, CatalogRules.AltTextMaxLength, "Alt metin"), "altText", local);
        }

        if (storageKey is not null && !seen.Add(storageKey))
        {
            local["storageKey"] = ["Aynı görsel bir kez seçilebilir."];
        }

        if (local.Count > 0)
        {
            foreach (var (field, messages) in local)
            {
                errors[$"{prefix}.{field}"] = messages;
            }

            return;
        }

        images.Add(new ImageInput(storageKey!, altText!, primary.IsSet && primary.Value));
    }

    // ---------- Varyant ----------

    public static RequestValidation<NewVariant> ValidateNewVariant(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!JsonFields.RequireObject(body, errors))
        {
            return RequestValidation<NewVariant>.Invalid(errors);
        }

        string? sku = null;
        var rawSku = JsonFields.String(body, "sku", allowNull: false, errors);
        if (!rawSku.IsSet)
        {
            errors.TryAdd("sku", ["sku zorunludur."]);
        }
        else if (!errors.ContainsKey("sku"))
        {
            sku = Check(() => CatalogRules.RequireSku(rawSku.Value), "sku", errors);
        }

        var displayName = RequiredText(body, "displayName", CatalogRules.DisplayNameMaxLength, "Varyant adı", errors);
        var price = ReadPrice(body, required: true, errors);
        var options = JsonFields.StringMap(body, "options", allowNull: false, errors);
        if (options.IsSet && options.Value is { } rawOptions && !errors.ContainsKey("options"))
        {
            Check(() => VariantOptions.Create(rawOptions), "options", errors);
        }

        var active = JsonFields.Bool(body, "isActive", errors);

        return errors.Count > 0
            ? RequestValidation<NewVariant>.Invalid(errors)
            : RequestValidation<NewVariant>.Valid(new NewVariant(sku!, displayName!, price!.Value, options.Value, active.IsSet ? active.Value : true));
    }

    public static RequestValidation<VariantChange> ValidateVariantChange(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (!JsonFields.RequireObject(body, errors))
        {
            return RequestValidation<VariantChange>.Invalid(errors);
        }

        var version = JsonFields.RequireVersion(body, errors);
        var displayName = OptionalNameChange(body, "displayName", CatalogRules.DisplayNameMaxLength, "Varyant adı", errors);
        var price = ReadPrice(body, required: false, errors);
        var active = JsonFields.Bool(body, "isActive", errors);

        return errors.Count > 0
            ? RequestValidation<VariantChange>.Invalid(errors)
            : RequestValidation<VariantChange>.Valid(new VariantChange(
                version!.Value, displayName, price is { } p ? Patch<long>.Of(p) : Patch<long>.Unset, active));
    }

    // ---------- Liste sorguları ----------

    public static RequestValidation<AdminProductListQuery> ValidateProductList(string? q, string? categoryId, string? status, string? page, string? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        var search = Search(q, errors);
        var category = OptionalGuid(categoryId, "categoryId", errors);
        var parsedStatus = "all";
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim();
            if (ProductStatuses.Contains(normalized, StringComparer.Ordinal))
            {
                parsedStatus = normalized;
            }
            else
            {
                errors["status"] = [$"status şunlardan biri olmalıdır: {string.Join(", ", ProductStatuses)}."];
            }
        }

        var parsedPage = ParseInt(page, "page", DefaultPage, 1, MaxPage, errors);
        var parsedSize = ParseInt(pageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<AdminProductListQuery>.Invalid(errors)
            : RequestValidation<AdminProductListQuery>.Valid(new AdminProductListQuery(search, category, parsedStatus, parsedPage, parsedSize));
    }

    public static RequestValidation<AdminVariantSearchQuery> ValidateVariantSearch(string? q, string? page, string? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        var search = Search(q, errors);
        var parsedPage = ParseInt(page, "page", DefaultPage, 1, MaxPage, errors);
        var parsedSize = ParseInt(pageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        return errors.Count > 0
            ? RequestValidation<AdminVariantSearchQuery>.Invalid(errors)
            : RequestValidation<AdminVariantSearchQuery>.Valid(new AdminVariantSearchQuery(search, parsedPage, parsedSize));
    }

    /// <summary>Rota kimliği. Bozuk ya da boş kimlik 400'dür (var olmayan kayıt değil, geçersiz istek).</summary>
    public static (Guid? Id, Dictionary<string, string[]>? Errors) ValidateId(string? raw) =>
        Guid.TryParse(raw, out var id) && id != Guid.Empty
            ? (id, null)
            : (null, new Dictionary<string, string[]> { ["id"] = ["id geçerli bir kimlik olmalıdır."] });

    /// <summary>Admin fiyat kuralı (1..2^53-1 kuruş) tek yerde: doğrulayıcı ve testler aynı fonksiyonu kullanır.</summary>
    public static bool TryValidatePriceMinor(long minor, out string? error)
    {
        try
        {
            Money.FromAdminPriceMinor(minor);
            error = null;
            return true;
        }
        catch (CatalogRuleException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // ---------- Yardımcılar ----------

    private static long? ReadPrice(JsonElement body, bool required, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("priceMinor", out var element))
        {
            if (required)
            {
                errors["priceMinor"] = ["priceMinor zorunludur."];
            }

            return null;
        }

        // Sayı kuruş cinsinden TAM SAYI olmalı (12.5 ya da "100" değil); 2^53-1 üstü long'a sığsa bile reddedilir.
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var minor))
        {
            errors["priceMinor"] = ["priceMinor kuruş cinsinden tam sayı olmalıdır."];
            return null;
        }

        if (!TryValidatePriceMinor(minor, out var error))
        {
            errors["priceMinor"] = [error!];
            return null;
        }

        return minor;
    }

    private static string? RequiredText(JsonElement body, string field, int maxLength, string what, Dictionary<string, string[]> errors)
    {
        var value = JsonFields.String(body, field, allowNull: false, errors);
        if (!value.IsSet)
        {
            errors.TryAdd(field, [$"{field} zorunludur."]);
            return null;
        }

        return errors.ContainsKey(field) ? null : Check(() => CatalogRules.RequireText(value.Value, maxLength, what), field, errors);
    }

    /// <summary>PATCH adı: yoksa değişmez; varsa dolu ve sınır içinde olmalı (null/boş ad geçersizdir).</summary>
    private static Patch<string?> OptionalNameChange(JsonElement body, string field, int maxLength, string what, Dictionary<string, string[]> errors)
    {
        var value = JsonFields.String(body, field, allowNull: false, errors);
        if (!value.IsSet)
        {
            return Patch<string?>.Unset;
        }

        var text = Check(() => CatalogRules.RequireText(value.Value, maxLength, what), field, errors);
        return text is null ? Patch<string?>.Unset : Patch<string?>.Of(text);
    }

    private static string? RequiredSlug(JsonElement body, int maxLength, string what, Dictionary<string, string[]> errors)
    {
        var value = JsonFields.String(body, "slug", allowNull: false, errors);
        if (!value.IsSet)
        {
            errors.TryAdd("slug", ["slug zorunludur."]);
            return null;
        }

        // Kırpma YOK: " abc" geçersizdir (slug değişmez ve URL'dir).
        return errors.ContainsKey("slug") ? null : Check(() => CatalogRules.RequireSlug(value.Value, maxLength, what), "slug", errors);
    }

    /// <summary>Marka/açıklama: yoksa değişmez, null ya da boş metin temizler, doluysa sınır içinde olmalı.</summary>
    private static Patch<string?> OptionalTextValue(JsonElement body, string field, int maxLength, string what, Dictionary<string, string[]> errors)
    {
        var value = JsonFields.String(body, field, allowNull: true, errors);
        if (!value.IsSet || errors.ContainsKey(field))
        {
            return Patch<string?>.Unset;
        }

        string? text = null;
        try
        {
            text = CatalogRules.OptionalText(value.Value, maxLength, what);
        }
        catch (CatalogRuleException exception)
        {
            errors[field] = [exception.Message];
            return Patch<string?>.Unset;
        }

        return Patch<string?>.Of(text);
    }

    private static Patch<Dictionary<string, string>?> SpecificationsValue(JsonElement body, Dictionary<string, string[]> errors)
    {
        var value = JsonFields.StringMap(body, "specifications", allowNull: true, errors);
        if (!value.IsSet || errors.ContainsKey("specifications") || value.Value is not { } map)
        {
            return value.IsSet && value.Value is null ? value : Patch<Dictionary<string, string>?>.Unset;
        }

        var normalized = Check(() => Product.NormalizeSpecifications(map), "specifications", errors);
        if (errors.ContainsKey("specifications"))
        {
            return Patch<Dictionary<string, string>?>.Unset;
        }

        var bytes = map.Sum(kv => Encoding.UTF8.GetByteCount(kv.Key) + Encoding.UTF8.GetByteCount(kv.Value));
        if (bytes > MaxSpecificationPayloadBytes)
        {
            errors["specifications"] = [$"Teknik özellikler en çok {MaxSpecificationPayloadBytes} bayt olabilir."];
            return Patch<Dictionary<string, string>?>.Unset;
        }

        return Patch<Dictionary<string, string>?>.Of(normalized is null ? null : new Dictionary<string, string>(normalized, StringComparer.Ordinal));
    }

    private static string? Search(string? raw, Dictionary<string, string[]> errors)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > MaxSearchLength || TextRules.ContainsUnsafeCharacter(text))
        {
            errors["q"] = [$"q en çok {MaxSearchLength} karakter olabilir ve kontrol karakteri içeremez."];
            return null;
        }

        return text;
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

    private static T? Check<T>(Func<T> rule, string field, Dictionary<string, string[]> errors)
    {
        try
        {
            return rule();
        }
        catch (CatalogRuleException exception)
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

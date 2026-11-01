using System.Globalization;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Application.Catalog;

/// <summary>İzinli sıralama anahtarları (allowlist). Her birinin ikincil sırası <c>id</c>'dir (deterministik sayfalama).</summary>
public enum ProductSort
{
    /// <summary><c>newest</c>: created_at azalan (varsayılan).</summary>
    Newest,

    /// <summary><c>price_asc</c>: başlangıç fiyatı artan.</summary>
    PriceAsc,

    /// <summary><c>price_desc</c>: başlangıç fiyatı azalan.</summary>
    PriceDesc,

    /// <summary><c>name_asc</c>: ürün adı artan (veritabanı collation'ına göre).</summary>
    NameAsc,
}

/// <summary>
/// HAM sorgu dizesi değerleri. Hepsi string'tir çünkü tip dönüşümü (ör. <c>page=abc</c>) minimal API'nin varsayılan
/// bağlama hatasına (gövdesiz 400 / Development'ta istisna) bırakılmaz; <see cref="ProductListQueryValidator"/> kendi
/// alan bazlı hata sözlüğünü üretir.
/// </summary>
public sealed record ProductListRequest(
    string? Category,
    string? Q,
    string? MinPriceMinor,
    string? MaxPriceMinor,
    string? Sort,
    string? Page,
    string? PageSize);

/// <summary>Doğrulanmış ve normalize edilmiş liste sorgusu. Yalnızca <see cref="ProductListQueryValidator"/> üretir.</summary>
/// <param name="CategorySlug">Ham slug, kırpılmaz (biçimi burada doğrulanmaz: bozuk/bilinmeyen kategori 404'tür, 400 değil). Boş/boşluk ise null.</param>
/// <param name="Search">Kırpılmış arama metni; boşsa null. Joker kaçışı Infrastructure'da yapılır.</param>
public sealed record ProductListQuery(
    string? CategorySlug,
    string? Search,
    long? MinPriceMinor,
    long? MaxPriceMinor,
    ProductSort Sort,
    int Page,
    int PageSize);

public sealed class ProductListQueryResult
{
    private ProductListQueryResult(ProductListQuery? query, IReadOnlyDictionary<string, string[]> errors)
    {
        Query = query;
        Errors = errors;
    }

    /// <summary>Geçerliyse dolu.</summary>
    public ProductListQuery? Query { get; }

    /// <summary>Alan adı (camelCase sorgu parametresi) → hata iletileri. Geçerliyse boş.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public bool IsValid => Query is not null;

    internal static ProductListQueryResult Valid(ProductListQuery query) =>
        new(query, new Dictionary<string, string[]>());

    internal static ProductListQueryResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        new(null, errors);
}

/// <summary>
/// Liste sorgusu doğrulaması: saf fonksiyon (girdi → çıktı), veritabanı/ağ yok. Tüm alanları tek geçişte doğrular,
/// ilk hatada durmaz (istemci hepsini birden görür).
/// </summary>
public static class ProductListQueryValidator
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPage = 10_000;
    public const int MaxPageSize = 50;
    public const int MaxSearchLength = 100;

    /// <summary>Fiyat filtresi üst sınırı: <see cref="Money.MaxAmount"/>'un kuruşu (2^53-1, JS güvenli tam sayı). Aşan değer decimal dönüşümünde patlardı.</summary>
    public static readonly long MaxPriceMinor = Money.ToMinor(Money.MaxAmount);

    private static readonly IReadOnlyDictionary<string, ProductSort> SortKeys = new Dictionary<string, ProductSort>(StringComparer.Ordinal)
    {
        ["newest"] = ProductSort.Newest,
        ["price_asc"] = ProductSort.PriceAsc,
        ["price_desc"] = ProductSort.PriceDesc,
        ["name_asc"] = ProductSort.NameAsc,
    };

    public static ProductListQueryResult Validate(ProductListRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        var page = ParseInt(request.Page, "page", DefaultPage, 1, MaxPage, errors);
        var pageSize = ParseInt(request.PageSize, "pageSize", DefaultPageSize, 1, MaxPageSize, errors);
        var min = ParseOptionalMinor(request.MinPriceMinor, "minPriceMinor", errors);
        var max = ParseOptionalMinor(request.MaxPriceMinor, "maxPriceMinor", errors);
        if (min is not null && max is not null && min > max)
        {
            errors["minPriceMinor"] = ["minPriceMinor, maxPriceMinor değerinden büyük olamaz."];
        }

        var sort = ProductSort.Newest;
        var sortRaw = request.Sort?.Trim();
        if (!string.IsNullOrEmpty(sortRaw) && !SortKeys.TryGetValue(sortRaw, out sort))
        {
            errors["sort"] = [$"Geçersiz sıralama. İzinli değerler: {string.Join(", ", SortKeys.Keys)}."];
        }

        var search = request.Q?.Trim();
        if (string.IsNullOrEmpty(search))
        {
            search = null;
        }
        else if (search.Length > MaxSearchLength)
        {
            errors["q"] = [$"q en çok {MaxSearchLength} karakter olabilir."];
        }
        else if (search.Any(char.IsControl))
        {
            // NUL (\0) PostgreSQL metin parametresinde hata verir; diğer kontrol karakterlerinin aramada anlamı yok.
            errors["q"] = ["q kontrol karakteri içeremez."];
        }

        // Slug KIRPILMAZ: tam eşleşme gerekir (sonda boşluk ya da satır sonu varsa 404 olur; aynı kategori için tek adres, SEO yinelemesi yok).
        var category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category;

        if (errors.Count > 0)
        {
            return ProductListQueryResult.Invalid(errors);
        }

        return ProductListQueryResult.Valid(new ProductListQuery(
            category,
            search,
            min,
            max,
            sort,
            page,
            pageSize));
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

    private static long? ParseOptionalMinor(string? raw, string name, Dictionary<string, string[]> errors)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            errors[name] = [$"{name} kuruş cinsinden tam sayı olmalıdır."];
            return null;
        }

        if (value < 0 || value > MaxPriceMinor)
        {
            errors[name] = [$"{name} 0 ile {MaxPriceMinor} arasında olmalıdır."];
            return null;
        }

        return value;
    }
}

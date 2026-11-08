using SSCommerce.Application.Catalog;
using SSCommerce.Domain.Customers;

namespace SSCommerce.Application.Favorites;

// Favoriler sözleşmesi (MD-1, D-088; docs/PLAN_CUSTOMER_EXPERIENCE.md). Favori listesi KİŞİSELDİR: yanıtlar no-store, loga/URL'ye yazılmaz.
// Alan adları ve birimler katalogdaki ProductListItemDto ile aynıdır (fiyat kuruş, long); ek alanlar addedAt (UTC) ve available'dır.

/// <summary>
/// Favori satırı = ürün liste öğesi alanları + <paramref name="AddedAt"/> + <paramref name="Available"/>.
/// <paramref name="Available"/> = ürün aktif, kategorisi aktif ve en az bir aktif varyantı var (katalogdaki "görünür ürün" tanımı).
/// Satıştan kalkan ürün listede KALIR (<c>available:false</c>, <c>inStock:false</c>), kullanıcı kaldırabilsin diye.
/// </summary>
public sealed record FavoriteItemDto(
    Guid Id,
    string Slug,
    string Name,
    CategoryRefDto Category,
    PrimaryImageDto? PrimaryImage,
    long StartingPriceMinor,
    string Currency,
    bool IsDemo,
    bool InStock,
    DateTimeOffset AddedAt,
    bool Available);

public sealed record FavoriteListDto(IReadOnlyList<FavoriteItemDto> Items);

public sealed record FavoriteIdsDto(IReadOnlyList<Guid> ProductIds);

public enum FavoriteWriteStatus
{
    /// <summary>Eklendi ya da zaten vardı (idempotent), silindi ya da zaten yoktu.</summary>
    Ok,

    /// <summary>Ürün yok, aktif değil, kategorisi pasif ya da aktif varyantı yok.</summary>
    ProductNotFound,

    /// <summary>Kullanıcı zaten <see cref="CustomerFavorite.MaxPerUser"/> favoride ve ürün yeni.</summary>
    LimitReached,

    /// <summary>Kilit zaman aşımı: hiçbir şey yazılmadı (503).</summary>
    Busy,
}

public interface IFavoriteService
{
    /// <summary>Eklenme anına göre azalan. En çok <see cref="CustomerFavorite.MaxPerUser"/> satır; sayfalama yok.</summary>
    Task<FavoriteListDto> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Kalp durumu ve sayaç için hafif uç: yalnız ürün kimlikleri (eklenme anına göre azalan).</summary>
    Task<FavoriteIdsDto> ListIdsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>İdempotent. Kullanıcı başına sıraya sokulur; sınır kilit İÇİNDE, sayım kilitten sonra denetlenir.</summary>
    Task<FavoriteWriteStatus> AddAsync(Guid userId, Guid productId, CancellationToken cancellationToken);

    /// <summary>İdempotent: favoride değilse de <see cref="FavoriteWriteStatus.Ok"/>.</summary>
    Task<FavoriteWriteStatus> RemoveAsync(Guid userId, Guid productId, CancellationToken cancellationToken);
}

public enum FavoriteAddDecision
{
    /// <summary>Zaten favoride: satır yazılmaz, başarı (sınırda da).</summary>
    AlreadyFavorite,

    /// <summary>Yeni satır yazılır.</summary>
    Add,

    /// <summary>Yeni ürün ama sınıra ulaşıldı: 409 favorite_limit.</summary>
    LimitReached,
}

/// <summary>Favori kurallarının saf kısmı (DB'siz birim test edilir).</summary>
public static class FavoriteRules
{
    /// <summary>
    /// Ekleme kararı. Mevcut favori sınırda bile başarıdır (idempotent); yalnız YENİ ürün sınırda reddedilir.
    /// <paramref name="currentCount"/> kilit alındıktan SONRA sayılmış olmalıdır.
    /// </summary>
    public static FavoriteAddDecision DecideAdd(bool alreadyFavorite, int currentCount)
    {
        if (alreadyFavorite)
        {
            return FavoriteAddDecision.AlreadyFavorite;
        }

        return currentCount >= CustomerFavorite.MaxPerUser ? FavoriteAddDecision.LimitReached : FavoriteAddDecision.Add;
    }

    /// <summary>Ürün satışta mı: katalogdaki görünür ürün tanımı (aktif ürün + aktif kategori + en az bir aktif varyant).</summary>
    public static bool IsAvailable(bool productActive, bool categoryActive, bool hasActiveVariant) =>
        productActive && categoryActive && hasActiveVariant;

    /// <summary>
    /// Başlangıç fiyatı (kuruş): en düşük AKTİF varyant; aktif varyant kalmadıysa (satıştan kalkmış ürün) en düşük varyant; hiç varyant yoksa 0.
    /// Aktif varyantlı ürünlerde katalog listesiyle birebir aynıdır.
    /// </summary>
    public static long StartingPriceMinor(long? minActiveMinor, long? minAnyMinor) => minActiveMinor ?? minAnyMinor ?? 0L;
}

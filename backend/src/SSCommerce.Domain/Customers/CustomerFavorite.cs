namespace SSCommerce.Domain.Customers;

/// <summary>
/// Kullanıcının favori ürünü (MD-1, D-088; sözleşme <c>docs/PLAN_CUSTOMER_EXPERIENCE.md</c>). Satır değişmez: eklenir ya da silinir.
/// Anahtar <c>(UserId, ProductId)</c>; aynı ürün kullanıcı başına bir kez bulunur (idempotent ekleme). Kullanıcı başına üst sınır
/// <see cref="MaxPerUser"/>; sınır uygulamada kullanıcı başına sıraya sokularak (pg_advisory_xact_lock) korunur.
/// </summary>
public sealed class CustomerFavorite
{
    /// <summary>Kullanıcı başına en çok favori sayısı. Aşılırsa 409 <c>favorite_limit</c>.</summary>
    public const int MaxPerUser = 100;

    private CustomerFavorite()
    {
    }

    public CustomerFavorite(Guid userId, Guid productId, DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty || productId == Guid.Empty)
        {
            throw new ArgumentException("Kullanıcı ve ürün kimliği zorunlu.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Zaman UTC olmalıdır.", nameof(createdAtUtc));
        }

        UserId = userId;
        ProductId = productId;
        CreatedAt = createdAtUtc;
    }

    public Guid UserId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>Favoriye eklenme anı (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}

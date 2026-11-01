namespace SSCommerce.Domain.Catalog;

/// <summary>
/// Ortak kimlik ve UTC zaman damgaları. Kimlik uygulamada üretilir (UUIDv7: zamana göre sıralı, indeks dostu).
/// Zaman damgalarını yalnızca kalıcılık katmanı (DbContext.SaveChanges) damgalar; saat <see cref="TimeProvider"/>'dan gelir, test edilebilir.
/// </summary>
public abstract class AuditedEntity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void MarkCreated(DateTimeOffset utcNow)
    {
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public void MarkUpdated(DateTimeOffset utcNow) => UpdatedAt = utcNow;
}

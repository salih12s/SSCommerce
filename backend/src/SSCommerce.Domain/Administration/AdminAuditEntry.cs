namespace SSCommerce.Domain.Administration;

/// <summary>
/// Yönetici denetim kaydı satırı (Faz 8). Yalnız eklenir (DB trigger: UPDATE/DELETE/TRUNCATE 23001). Kimlik uygulamada üretilir (UUIDv7),
/// zamanı çağıran verir (TimeProvider); veritabanında varsayılan zaman yoktur. <c>changes</c> parola, özet, damga, belirteç, e-posta, telefon
/// ve adres taşımaz (<see cref="AdminAuditRules.ForbiddenChangeKeys"/>); başarısız bir yazma ya da değişiklik içermeyen yazma satır üretmez.
/// </summary>
public sealed class AdminAuditEntry
{
    private AdminAuditEntry()
    {
        Action = string.Empty;
        TargetType = string.Empty;
        Changes = "{}";
    }

    public AdminAuditEntry(
        Guid actorUserId,
        string action,
        string targetType,
        Guid targetId,
        string changesJson,
        DateTimeOffset occurredAt)
    {
        if (actorUserId == Guid.Empty || targetId == Guid.Empty)
        {
            throw new AdminAuditRuleException("Denetim kaydı için işlemi yapan ve hedef kimliği zorunlu.");
        }

        Id = Guid.CreateVersion7();
        ActorUserId = actorUserId;
        Action = AdminAuditRules.RequireAction(action);
        TargetType = AdminAuditRules.RequireTargetType(targetType);
        TargetId = targetId;
        Changes = AdminAuditRules.RequireChanges(changesJson);
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public Guid ActorUserId { get; private set; }

    /// <summary>Örn. <c>category.create</c>, <c>role.grant</c>, <c>order.delivery_view</c>.</summary>
    public string Action { get; private set; }

    public string TargetType { get; private set; }

    public Guid TargetId { get; private set; }

    /// <summary>jsonb nesne metni (okununca PostgreSQL biçimi: anahtarlar sıralı, ": " ayraçlı).</summary>
    public string Changes { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}

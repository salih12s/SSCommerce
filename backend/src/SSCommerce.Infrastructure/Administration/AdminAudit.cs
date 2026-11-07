using System.Text.Json;
using SSCommerce.Domain.Administration;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Administration;

/// <summary>
/// Denetim satırı kurucusu (Faz 8). Satır, çağıranın DbContext'ine EKLENİR ve onun transaction'ında, değişiklikle AYNI <c>SaveChanges</c> toplu
/// işleminde yazılır: denetim satırı yazılamazsa değişiklik de geri alınır. <c>changes</c> bir JSON nesnesidir ve hiçbir zaman parola, özet, damga,
/// belirteç, e-posta, telefon ya da adres taşımaz (DB CHECK ve <see cref="AdminAuditRules"/>). Uzun metinler kısaltılır (8 KB sınırı 500 üretmesin).
/// </summary>
internal static class AdminAudit
{
    /// <summary>Denetim değerlerinde metin üst sınırı; fazlası kırpılır ve uzunluk eklenir.</summary>
    public const int MaxTextLength = 200;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static AdminAuditEntry Add(
        SSCommerceDbContext db,
        TimeProvider clock,
        Guid actorUserId,
        string action,
        string targetType,
        Guid targetId,
        object changes)
    {
        var entry = new AdminAuditEntry(actorUserId, action, targetType, targetId, JsonSerializer.Serialize(changes, Options), clock.GetUtcNow());
        db.AdminAuditLog.Add(entry);
        return entry;
    }

    /// <summary>Uzun metni kısaltır (denetim satırı küçük kalsın). Null olduğu gibi döner.</summary>
    public static string? Clip(string? value) =>
        value is null || value.Length <= MaxTextLength ? value : value[..MaxTextLength] + $"… ({value.Length} karakter)";
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSCommerce.Domain.Administration;

/// <summary>
/// <c>admin_audit_log</c> doğrulama kuralları (Faz 8, docs/CONTRACT_PHASE_8.md "Persistence" 2). Aynı sınırlar veritabanında CHECK olarak da
/// vardır (savunma derinliği); bir sabiti değiştirirsen EF konfigürasyonundaki CHECK için yeni migration gerekir.
/// Domain kontrolü ÖN kontroldür: boyutu jsonb metin biçimine yakın (": " ve ", " ayraçlı) ölçer; kesin karar veritabanı CHECK'indedir.
/// </summary>
public static partial class AdminAuditRules
{
    public const int ActionMaxLength = 64;

    /// <summary><c>octet_length(changes::text)</c> üst sınırı.</summary>
    public const int ChangesMaxBytes = 8192;

    /// <summary>DB CHECK <c>ck_admin_audit_log_action_format</c> deseni (PostgreSQL "$" tam sonu ister).</summary>
    public const string ActionPattern = @"^[a-z]+(\.[a-z_]+)+$";

    public const string TargetCategory = "category";
    public const string TargetProduct = "product";
    public const string TargetVariant = "variant";
    public const string TargetWarehouse = "warehouse";
    public const string TargetUser = "user";
    public const string TargetOrder = "order";

    public static readonly IReadOnlyList<string> TargetTypes =
        [TargetCategory, TargetProduct, TargetVariant, TargetWarehouse, TargetUser, TargetOrder];

    /// <summary>Üst düzey anahtar olarak asla yazılmayan alanlar (parola, özet, damga, belirteç, e-posta, telefon, adres). DB CHECK ile aynı liste.</summary>
    public static readonly IReadOnlyList<string> ForbiddenChangeKeys =
        ["password", "password_hash", "security_stamp", "token", "email", "phone", "address", "street_address"];

    private static readonly JsonSerializerOptions StringSizeOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // .NET'te "$" sondaki satır sonundan önce de eşleşir; "\z" yalnız metnin gerçek sonudur.
    [GeneratedRegex(@"^[a-z]+(\.[a-z_]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ActionRegex();

    public static string RequireAction(string? action)
    {
        if (string.IsNullOrEmpty(action) || action.Length > ActionMaxLength || !ActionRegex().IsMatch(action))
        {
            throw new AdminAuditRuleException("Denetim eylemi geçersiz.");
        }

        return action;
    }

    public static string RequireTargetType(string? targetType)
    {
        if (targetType is null || !TargetTypes.Contains(targetType, StringComparer.Ordinal))
        {
            throw new AdminAuditRuleException("Denetim hedef türü geçersiz.");
        }

        return targetType;
    }

    /// <summary>Gövde bir JSON nesnesi olmalı, yasak üst düzey anahtar taşımamalı ve <see cref="ChangesMaxBytes"/> baytı aşmamalı.</summary>
    public static string RequireChanges(string? changesJson)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
        {
            throw new AdminAuditRuleException("Denetim değişiklik gövdesi zorunlu.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(changesJson);
        }
        catch (JsonException)
        {
            throw new AdminAuditRuleException("Denetim değişiklik gövdesi geçerli JSON değil.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AdminAuditRuleException("Denetim değişiklik gövdesi bir JSON nesnesi olmalı.");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (ForbiddenChangeKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new AdminAuditRuleException("Denetim değişiklik gövdesi yasak bir anahtar içeriyor.");
                }
            }

            if (MeasureJsonbText(root) > ChangesMaxBytes)
            {
                throw new AdminAuditRuleException($"Denetim değişiklik gövdesi en çok {ChangesMaxBytes} bayt olabilir.");
            }
        }

        return changesJson;
    }

    /// <summary>jsonb metin biçiminin bayt uzunluğu: ayraçlar ": " ve ", " (boşluklu), metinler UTF-8 ve tırnaklı.</summary>
    private static long MeasureJsonbText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                long objectSize = 2;
                var first = true;
                foreach (var property in element.EnumerateObject())
                {
                    objectSize += (first ? 0 : 2) + StringSize(property.Name) + 2 + MeasureJsonbText(property.Value);
                    first = false;
                }

                return objectSize;
            case JsonValueKind.Array:
                long arraySize = 2;
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    arraySize += (firstItem ? 0 : 2) + MeasureJsonbText(item);
                    firstItem = false;
                }

                return arraySize;
            case JsonValueKind.String:
                return StringSize(element.GetString()!);
            case JsonValueKind.True:
            case JsonValueKind.Null:
                return 4;
            case JsonValueKind.False:
                return 5;
            default:
                return Encoding.UTF8.GetByteCount(element.GetRawText());
        }
    }

    private static long StringSize(string value) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, StringSizeOptions));
}

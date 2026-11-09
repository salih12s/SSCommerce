namespace SSCommerce.Domain.Administration;

/// <summary>Yönetici denetim kaydı kural ihlali (geçersiz eylem, hedef türü, değişiklik gövdesi). Mesaj sır ve kişisel veri içermez.</summary>
public sealed class AdminAuditRuleException(string message) : Exception(message);

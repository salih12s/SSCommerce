namespace SSCommerce.Domain.Catalog;

/// <summary>Katalog iş kuralı ihlali (geçersiz slug, fiyat, seçenek vb.). Mesaj kullanıcıya gösterilebilir, sır içermez.</summary>
public sealed class CatalogRuleException(string message) : Exception(message);

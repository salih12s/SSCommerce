namespace SSCommerce.Domain.Customers;

/// <summary>Adres iş kuralı ihlali (geçersiz telefon, il, posta kodu vb.). Mesaj kullanıcıya gösterilebilir; girilen adres/telefon değerini İÇERMEZ.</summary>
public sealed class AddressRuleException(string message) : Exception(message);

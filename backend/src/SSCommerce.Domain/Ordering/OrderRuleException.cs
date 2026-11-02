namespace SSCommerce.Domain.Ordering;

/// <summary>Sipariş iş kuralı ihlali (geçersiz anahtar, durum geçişi, tutar vb.). Mesaj kullanıcıya gösterilebilir, sır ve kişisel veri içermez.</summary>
public sealed class OrderRuleException(string message) : Exception(message);

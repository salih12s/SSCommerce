namespace SSCommerce.Domain.Cart;

/// <summary>Sepet iş kuralı ihlali (geçersiz adet vb.). Mesaj kullanıcıya gösterilebilir, sır içermez.</summary>
public sealed class CartRuleException(string message) : Exception(message);

namespace SSCommerce.Domain.Inventory;

/// <summary>Stok iş kuralı ihlali (geçersiz depo kodu, miktar, neden vb.). Mesaj kullanıcıya gösterilebilir, sır içermez.</summary>
public sealed class InventoryRuleException(string message) : Exception(message);

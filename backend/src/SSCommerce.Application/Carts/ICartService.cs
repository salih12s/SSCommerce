namespace SSCommerce.Application.Carts;

/// <summary>
/// Sepet sözleşmesi. Kullanıcı kimliği çağıran tarafından YALNIZ kimlik doğrulanmış principal'den verilir; her sorgu kullanıcıya kapsamlıdır
/// (başkasının satırı "yok" ile aynı: <see cref="CartOperationStatus.ItemNotFound"/>). Sepet stok AYIRMAZ, stok düşmez (D-050).
/// </summary>
public interface ICartService
{
    /// <summary>Okuma: hiçbir şey oluşturmaz. Sepet yoksa boş sepet. Tek sorgu (satır sayısından bağımsız).</summary>
    Task<CartOperationResult> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Varyant satırda varsa adedi ARTIRIR, yoksa ekler.</summary>
    Task<CartOperationResult> AddItemAsync(Guid userId, AddCartItemCommand command, CancellationToken cancellationToken);

    /// <summary>Mutlak adet (1..20). Yalnız ARTIŞ aktiflik ve stok denetlenir; azaltma her zaman serbesttir (D-050).</summary>
    Task<CartOperationResult> SetQuantityAsync(Guid userId, Guid itemId, int quantity, CancellationToken cancellationToken);

    Task<CartOperationResult> RemoveItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
}

using SSCommerce.Domain.Catalog;

namespace SSCommerce.Domain.Cart;

/// <summary>
/// Kullanıcı başına tek sepet (D-049; <c>ux_carts_user_id</c>). Misafir sepeti yoktur. Sepet fiyat ya da stok TAŞIMAZ:
/// tutar her okumada güncel katalog fiyatından, stok durumu stok tablolarından hesaplanır. Satırlar <see cref="CartItem"/>'dır.
/// Yazmalar kullanıcı başına <c>SELECT … FOR UPDATE</c> ile sıralanır (D-050), bu yüzden mutasyon metodu yoktur.
/// </summary>
public sealed class Cart : AuditedEntity
{
    private Cart()
    {
    }

    public Cart(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new CartRuleException("Kullanıcı kimliği zorunlu.");
        }

        UserId = userId;
    }

    public Guid UserId { get; private set; }
}

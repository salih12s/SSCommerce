using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Carts;
using SSCommerce.Application.Checkout;
using SSCommerce.Infrastructure.Addresses;
using SSCommerce.Infrastructure.Carts;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Checkout;

/// <summary>
/// Checkout preview (F6.4, D-054): SALT OKUNUR BY CONSTRUCTION. Yalnız iki AsNoTracking okuması (adres, sepet satırları) yapılır;
/// <c>SaveChanges</c> ve transaction YOKTUR, sipariş/ödeme/rezervasyon tabloları da yoktur. Bu sınıfa yazan bir çağrı eklenirse preview
/// testi (bakiye, hareket, sepet satırı ve updated_at değişmez) kırmızı olur.
/// Adres sahipliği sorguda (<c>user_id</c>): başkasının adresi "yok" ile aynıdır (404). Ödenecek kesin tutar UYDURULMAZ.
/// </summary>
internal sealed class CheckoutPreviewService(SSCommerceDbContext db, CartLineReader reader, TimeProvider clock) : ICheckoutPreviewService
{
    public async Task<CheckoutPreviewResult> PreviewAsync(Guid userId, Guid addressId, CancellationToken cancellationToken)
    {
        var address = await db.CustomerAddresses
            .AsNoTracking()
            .Where(a => a.Id == addressId && a.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);
        if (address is null)
        {
            return new CheckoutPreviewResult(CheckoutPreviewStatus.AddressNotFound);
        }

        var lines = await reader.LoadAsync(userId, cancellationToken);
        if (lines.Count == 0)
        {
            return new CheckoutPreviewResult(CheckoutPreviewStatus.CartEmpty);
        }

        var calculation = CartCalculator.Calculate(lines);
        if (calculation.TooLarge)
        {
            return new CheckoutPreviewResult(CheckoutPreviewStatus.TotalTooLarge);
        }

        var cart = calculation.Cart!;
        var issues = new List<CheckoutIssueDto>();
        foreach (var item in cart.Items)
        {
            if (item.Status != CartLineStatus.Available)
            {
                issues.Add(new CheckoutIssueDto(item.Status, item.Id));
            }

            if (item.PriceChanged)
            {
                issues.Add(new CheckoutIssueDto(CartLineStatus.PriceChanged, item.Id));
            }
        }

        return new CheckoutPreviewResult(
            CheckoutPreviewStatus.Ok,
            new CheckoutPreviewDto(
                cart.Items,
                AddressService.ToDto(address),
                cart.ItemCount,
                cart.SubtotalMinor,
                ShippingMinor: null,
                ShippingStatuses.NotCalculated,
                GrandTotalMinor: null,
                cart.Currency,
                CanProceed: cart.Items.All(i => i.Status == CartLineStatus.Available),
                issues,
                OrderingAvailable: false,
                clock.GetUtcNow()));
    }
}

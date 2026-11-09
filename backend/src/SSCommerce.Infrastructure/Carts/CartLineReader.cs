using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Carts;
using SSCommerce.Infrastructure.Catalog;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Carts;

/// <summary>
/// Kullanıcının sepet satırlarını TEK sorguyla okur (satır sayısından bağımsız; N+1 yok): satır + varyant + ürün + kategori +
/// ana görsel + stok toplamı. Sahiplik sorguda (<c>carts.user_id = @user</c>); başka kullanıcının satırı bu sorguya giremez.
/// Stok tanımı katalogla ORTAKTIR (<see cref="StockAvailability"/>). Sıralama kararlı: (created_at, id).
/// Salt okunur (AsNoTracking): preview de bunu kullanır. Açık bir transaction varsa onun içinde, yoksa kendi başına çalışır.
/// </summary>
internal sealed class CartLineReader(SSCommerceDbContext db)
{
    public async Task<IReadOnlyList<CartLineData>> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await (
            from cart in db.Carts.AsNoTracking()
            where cart.UserId == userId
            join item in db.CartItems on cart.Id equals item.CartId
            join variant in db.ProductVariants on item.ProductVariantId equals variant.Id
            join product in db.Products on variant.ProductId equals product.Id
            join category in db.Categories on product.CategoryId equals category.Id
            orderby item.CreatedAt, item.Id
            select new
            {
                ItemId = item.Id,
                VariantId = variant.Id,
                variant.Sku,
                ProductSlug = product.Slug,
                ProductName = product.Name,
                VariantName = variant.DisplayName,
                ImageUrl = db.ProductImages.Where(i => i.ProductId == product.Id && i.IsPrimary).Select(i => i.StorageKey).FirstOrDefault(),
                ImageAlt = db.ProductImages.Where(i => i.ProductId == product.Id && i.IsPrimary).Select(i => i.AltText).FirstOrDefault(),
                item.Quantity,
                Hint = item.UnitPriceAtAdd,
                Current = variant.Price,
                Purchasable = variant.IsActive && product.IsActive && category.IsActive,
                Available = StockAvailability.ActiveBalances(db)
                    .Where(b => b.ProductVariantId == variant.Id)
                    .Sum(b => (long?)b.OnHandQuantity) ?? 0L,
            }).ToListAsync(cancellationToken);

        return rows
            .Select(r => new CartLineData(
                r.ItemId,
                r.VariantId,
                r.Sku,
                r.ProductSlug,
                r.ProductName,
                r.VariantName,
                r.ImageUrl,
                r.ImageAlt,
                r.Quantity,
                r.Hint,
                r.Current,
                r.Purchasable,
                r.Available))
            .ToList();
    }
}

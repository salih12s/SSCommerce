using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Inventory;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Inventory;

/// <summary>
/// Admin stok okuma sorguları (<see cref="IInventoryQueries"/>): AsNoTracking + projection. Sayfalı uçlar 1 sayım + 1 sayfa sorgusudur.
/// Sıralamalar deterministiktir (son anahtar id): sayfalar arasında kayma/tekrar olmaz.
/// </summary>
internal sealed class InventoryQueries(SSCommerceDbContext db) : IInventoryQueries
{
    public async Task<IReadOnlyList<WarehouseDto>> GetWarehousesAsync(CancellationToken cancellationToken) =>
        await db.Warehouses
            .AsNoTracking()
            .OrderBy(w => w.Code).ThenBy(w => w.Id)
            .Select(w => new WarehouseDto(w.Id, w.Code, w.Name, w.IsActive, w.CreatedAt, w.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<PagedDto<InventoryBalanceDto>> GetBalancesAsync(BalanceListQuery query, CancellationToken cancellationToken)
    {
        var rows =
            from balance in db.InventoryBalances.AsNoTracking()
            join warehouse in db.Warehouses on balance.WarehouseId equals warehouse.Id
            join variant in db.ProductVariants on balance.ProductVariantId equals variant.Id
            select new { balance, warehouse.Code, variant.Sku };

        if (query.WarehouseId is { } warehouseId)
        {
            rows = rows.Where(r => r.balance.WarehouseId == warehouseId);
        }

        if (query.Sku is { } sku)
        {
            rows = rows.Where(r => r.Sku == sku);
        }

        var totalCount = await rows.LongCountAsync(cancellationToken);
        var items = await Page(rows.OrderBy(r => r.Code).ThenBy(r => r.Sku).ThenBy(r => r.balance.Id), query.Page, query.PageSize, totalCount)
            .Select(r => new InventoryBalanceDto(
                r.balance.WarehouseId,
                r.Code,
                r.balance.ProductVariantId,
                r.Sku,
                r.balance.OnHandQuantity,
                r.balance.Version,
                r.balance.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new PagedDto<InventoryBalanceDto>(items, query.Page, query.PageSize, totalCount, TotalPages(totalCount, query.PageSize));
    }

    public async Task<PagedDto<StockMovementDto>> GetMovementsAsync(MovementListQuery query, CancellationToken cancellationToken)
    {
        var rows = db.StockMovements.AsNoTracking();
        if (query.WarehouseId is { } warehouseId)
        {
            rows = rows.Where(m => m.WarehouseId == warehouseId);
        }

        if (query.VariantId is { } variantId)
        {
            rows = rows.Where(m => m.ProductVariantId == variantId);
        }

        var totalCount = await rows.LongCountAsync(cancellationToken);
        var page = await Page(rows.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id), query.Page, query.PageSize, totalCount)
            .ToListAsync(cancellationToken);

        var items = page.Select(m => InventoryService.ToDto(m, replayed: false)).ToList();
        return new PagedDto<StockMovementDto>(items, query.Page, query.PageSize, totalCount, TotalPages(totalCount, query.PageSize));
    }

    /// <summary>Toplamın ötesindeki sayfa hata değil, boş sayfadır (katalogla aynı kural).</summary>
    private static IQueryable<T> Page<T>(IOrderedQueryable<T> ordered, int page, int pageSize, long totalCount)
    {
        var skip = (long)(page - 1) * pageSize;
        return skip >= totalCount ? ordered.Take(0) : ordered.Skip((int)skip).Take(pageSize);
    }

    private static int TotalPages(long totalCount, int pageSize) => (int)((totalCount + pageSize - 1) / pageSize);
}

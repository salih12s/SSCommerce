namespace SSCommerce.Application.Inventory;

/// <summary>
/// Stok YAZMA sözleşmesi (admin). Uygulaması Infrastructure'dadır. Stok işlemi tek transaction'dır ve atomiktir
/// (docs/CONTRACT_PHASES_5_6.md "Atomik akış", D-046): reddedilen istek anahtarı tüketmez ve hareket bırakmaz.
/// </summary>
public interface IInventoryService
{
    Task<StockOperationResult> ApplyStockOperationAsync(StockCommand command, CancellationToken cancellationToken);

    /// <summary>Kod tekrarı <see cref="WarehouseWriteStatus.CodeTaken"/>; yarışta da veritabanı unique indeksi karar verir.</summary>
    Task<WarehouseWriteResult> CreateWarehouseAsync(NewWarehouse warehouse, CancellationToken cancellationToken);

    Task<WarehouseWriteResult> UpdateWarehouseAsync(Guid id, WarehouseChange change, CancellationToken cancellationToken);
}

/// <summary>Stok OKUMA sözleşmesi (admin). AsNoTracking + projection; sayfalı uçlar sayım + sayfa = 2 sorgu.</summary>
public interface IInventoryQueries
{
    Task<IReadOnlyList<WarehouseDto>> GetWarehousesAsync(CancellationToken cancellationToken);

    Task<PagedDto<InventoryBalanceDto>> GetBalancesAsync(BalanceListQuery query, CancellationToken cancellationToken);

    /// <summary>En yeni önce (occurred_at, id azalan).</summary>
    Task<PagedDto<StockMovementDto>> GetMovementsAsync(MovementListQuery query, CancellationToken cancellationToken);
}

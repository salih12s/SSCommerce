namespace SSCommerce.Application.Inventory;

/// <summary>
/// Stok YAZMA sözleşmesi (admin). Uygulaması Infrastructure'dadır. Stok işlemi tek transaction'dır ve atomiktir
/// (docs/CONTRACT_PHASES_5_6.md "Atomik akış", D-046): reddedilen istek anahtarı tüketmez ve hareket bırakmaz.
/// </summary>
public interface IInventoryService
{
    Task<StockOperationResult> ApplyStockOperationAsync(StockCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Kod tekrarı <see cref="WarehouseWriteStatus.CodeTaken"/>; yarışta da veritabanı unique indeksi karar verir. Depo ve <c>warehouse.create</c> denetim
    /// satırı AYNI transaction'da yazılır (Faz 8).
    /// </summary>
    Task<WarehouseWriteResult> CreateWarehouseAsync(Guid actorUserId, NewWarehouse warehouse, CancellationToken cancellationToken);

    /// <summary>Depo satırı kilitlenir; yalnız gerçekten değişen alan varsa <c>warehouse.update</c> denetim satırı yazılır (aynı transaction).</summary>
    Task<WarehouseWriteResult> UpdateWarehouseAsync(Guid actorUserId, Guid id, WarehouseChange change, CancellationToken cancellationToken);
}

/// <summary>Stok OKUMA sözleşmesi (admin). AsNoTracking + projection; sayfalı uçlar sayım + sayfa = 2 sorgu (+ hareketlerde tek toplu aktör sorgusu).</summary>
public interface IInventoryQueries
{
    Task<IReadOnlyList<WarehouseDto>> GetWarehousesAsync(CancellationToken cancellationToken);

    Task<PagedDto<InventoryBalanceDto>> GetBalancesAsync(BalanceListQuery query, CancellationToken cancellationToken);

    /// <summary>En yeni önce (occurred_at, id azalan).</summary>
    Task<PagedDto<StockMovementDto>> GetMovementsAsync(MovementListQuery query, CancellationToken cancellationToken);
}

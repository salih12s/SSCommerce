namespace SSCommerce.Application.Inventory;

// Admin stok yanıt sözleşmesi (docs/CONTRACT_PHASES_5_6.md, D-045..D-047). Public katalog yanıtlarında depo bilgisi YOKTUR (D-048);
// bu tipler yalnız /api/v1/admin altında döner.

public sealed record WarehouseDto(Guid Id, string Code, string Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Bakiye satırı. <paramref name="Version"/> tanı alanıdır (iyimser kilit değil, D-046).</summary>
public sealed record InventoryBalanceDto(
    Guid WarehouseId,
    string WarehouseCode,
    Guid VariantId,
    string Sku,
    int OnHandQuantity,
    long Version,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Stok hareketi. <paramref name="Replayed"/>: aynı <c>Idempotency-Key</c> + aynı içerikle gelen tekrar isteğin yanıtıdır
/// (ilk sonuç, bakiye değişmedi). Listelerde her zaman false.
/// <paramref name="ActorUserId"/>: işlemi İLK uygulayan yönetici (denetim kaydı). Tekrar (replay) yanıtında da ORİJİNAL aktördür,
/// tekrarı gönderen kullanıcı değil.
/// </summary>
public sealed record StockMovementDto(
    Guid Id,
    Guid WarehouseId,
    Guid VariantId,
    string Sku,
    int QuantityDelta,
    string OperationType,
    string Reason,
    string? Reference,
    int OnHandAfter,
    DateTimeOffset OccurredAt,
    Guid ActorUserId,
    bool Replayed);

public sealed record PagedDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount, int TotalPages);

namespace SSCommerce.Domain.Inventory;

/// <summary>
/// Stok hareketi denetim kaydı. DEĞİŞMEZ: veritabanı trigger'ı UPDATE/DELETE/TRUNCATE'i reddeder (D-047), bu yüzden tüm alanlar
/// yalnız yapıcıda atanır ve <c>AuditedEntity</c> kullanılmaz (updated_at yoktur; zaman <see cref="OccurredAt"/>'tir).
/// Zamanı çağıran verir (TimeProvider); SKU o anki kopyadır.
/// </summary>
public sealed class StockMovement
{
    private StockMovement()
    {
        Sku = string.Empty;
        Reason = string.Empty;
        OperationKey = string.Empty;
        RequestFingerprint = string.Empty;
    }

    public StockMovement(
        Guid warehouseId,
        Guid productVariantId,
        string sku,
        int quantityDelta,
        StockOperationType operationType,
        Guid actorUserId,
        string reason,
        string? reference,
        string operationKey,
        string requestFingerprint,
        int onHandAfter,
        DateTimeOffset occurredAt)
    {
        if (warehouseId == Guid.Empty || productVariantId == Guid.Empty || actorUserId == Guid.Empty)
        {
            throw new InventoryRuleException("Depo, varyant ve işlemi yapan kullanıcı kimliği zorunlu.");
        }

        Id = Guid.CreateVersion7();
        WarehouseId = warehouseId;
        ProductVariantId = productVariantId;
        Sku = InventoryRules.RequireSku(sku);
        QuantityDelta = InventoryRules.RequireQuantityDelta(quantityDelta);
        _ = StockOperationTypes.ToWire(operationType); // tanımsız enum değerini erken reddet
        if (operationType == StockOperationType.Receiving && quantityDelta < 0)
        {
            throw new InventoryRuleException("Depo girişi (receiving) pozitif olmalı.");
        }

        // Sipariş hareketlerinin işareti türden belli: yerleştirme stok düşer, iptal stok iade eder.
        if (operationType == StockOperationType.OrderPlacement && quantityDelta >= 0)
        {
            throw new InventoryRuleException("Sipariş stok tüketimi (order_placement) negatif olmalı.");
        }

        if (operationType == StockOperationType.OrderCancellation && quantityDelta <= 0)
        {
            throw new InventoryRuleException("Sipariş iptal iadesi (order_cancellation) pozitif olmalı.");
        }

        OperationType = operationType;
        ActorUserId = actorUserId;
        Reason = InventoryRules.RequireReason(reason);
        Reference = InventoryRules.OptionalReference(reference);
        OperationKey = InventoryRules.RequireOperationKey(operationKey);
        RequestFingerprint = InventoryRules.RequireRequestFingerprint(requestFingerprint);
        OnHandAfter = InventoryRules.RequireOnHand(onHandAfter);
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public string Sku { get; private set; }

    public int QuantityDelta { get; private set; }

    public StockOperationType OperationType { get; private set; }

    public Guid ActorUserId { get; private set; }

    public string Reason { get; private set; }

    public string? Reference { get; private set; }

    public string OperationKey { get; private set; }

    public string RequestFingerprint { get; private set; }

    public int OnHandAfter { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}

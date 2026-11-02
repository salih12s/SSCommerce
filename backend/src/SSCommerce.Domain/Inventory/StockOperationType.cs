namespace SSCommerce.Domain.Inventory;

/// <summary>Stok hareketi türü. Veritabanında küçük harfli metin olarak saklanır (<see cref="StockOperationTypes"/>).</summary>
public enum StockOperationType
{
    /// <summary>Depo girişi: delta her zaman pozitif.</summary>
    Receiving = 1,

    /// <summary>Sayım ya da hasar gibi artı/eksi düzeltme.</summary>
    Adjustment = 2,

    /// <summary>Önceki bir hatanın düzeltmesi (artı/eksi).</summary>
    Correction = 3,

    /// <summary>Sipariş verildiğinde stok tüketimi (D-061): delta her zaman negatif. Yalnız sipariş servisi üretir.</summary>
    OrderPlacement = 4,

    /// <summary>Sipariş iptalinde stok iadesi (D-062): delta her zaman pozitif. Yalnız sipariş servisi üretir.</summary>
    OrderCancellation = 5,
}

/// <summary>
/// <see cref="StockOperationType"/> ile veritabanı metni arasındaki tek dönüşüm noktası.
/// Metinler <c>ck_stock_movements_operation_type</c> CHECK'iyle aynı kalmalıdır.
/// </summary>
public static class StockOperationTypes
{
    public const string Receiving = "receiving";
    public const string Adjustment = "adjustment";
    public const string Correction = "correction";
    public const string OrderPlacement = "order_placement";
    public const string OrderCancellation = "order_cancellation";

    /// <summary>
    /// DB CHECK'inin kabul ettiği bütün metinler. Genel yönetici girdisi bunun alt kümesini kabul eder (yalnız adjustment/correction,
    /// bkz. InventoryRequestValidator); sipariş türleri yalnız sipariş servisinden gelir.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Receiving, Adjustment, Correction, OrderPlacement, OrderCancellation];

    public static string ToWire(StockOperationType type) => type switch
    {
        StockOperationType.Receiving => Receiving,
        StockOperationType.Adjustment => Adjustment,
        StockOperationType.Correction => Correction,
        StockOperationType.OrderPlacement => OrderPlacement,
        StockOperationType.OrderCancellation => OrderCancellation,
        _ => throw new InventoryRuleException("Geçersiz stok işlem türü."),
    };

    public static StockOperationType FromWire(string value) => TryFromWire(value, out var type)
        ? type
        : throw new InventoryRuleException("Geçersiz stok işlem türü.");

    public static bool TryFromWire(string? value, out StockOperationType type)
    {
        switch (value)
        {
            case Receiving:
                type = StockOperationType.Receiving;
                return true;
            case Adjustment:
                type = StockOperationType.Adjustment;
                return true;
            case Correction:
                type = StockOperationType.Correction;
                return true;
            case OrderPlacement:
                type = StockOperationType.OrderPlacement;
                return true;
            case OrderCancellation:
                type = StockOperationType.OrderCancellation;
                return true;
            default:
                type = default;
                return false;
        }
    }
}

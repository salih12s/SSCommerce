namespace SSCommerce.Domain.Ordering;

/// <summary>Sipariş durumu (D-063). Veritabanında ve API'de küçük harfli metin olarak saklanır (<see cref="OrderStatuses"/>).</summary>
public enum OrderStatus
{
    /// <summary>Sipariş verildi, stok düşüldü, ödeme alınmadı (D-061).</summary>
    Placed = 1,

    /// <summary>Depo hazırlıyor.</summary>
    Processing = 2,

    /// <summary>Kargoya verildi.</summary>
    Shipped = 3,

    /// <summary>Teslim edildi. Son durum.</summary>
    Completed = 4,

    /// <summary>İptal edildi, stok iade edildi. Son durum.</summary>
    Cancelled = 5,
}

/// <summary>
/// <see cref="OrderStatus"/> ile veritabanı/API metni arasındaki tek dönüşüm noktası.
/// Metinler <c>ck_orders_status</c> ve <c>ck_order_status_history_*</c> CHECK'leriyle aynı kalmalıdır.
/// </summary>
public static class OrderStatuses
{
    public const string Placed = "placed";
    public const string Processing = "processing";
    public const string Shipped = "shipped";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Placed, Processing, Shipped, Completed, Cancelled];

    public static string ToWire(OrderStatus status) => status switch
    {
        OrderStatus.Placed => Placed,
        OrderStatus.Processing => Processing,
        OrderStatus.Shipped => Shipped,
        OrderStatus.Completed => Completed,
        OrderStatus.Cancelled => Cancelled,
        _ => throw new OrderRuleException("Geçersiz sipariş durumu."),
    };

    public static OrderStatus FromWire(string value) => TryFromWire(value, out var status)
        ? status
        : throw new OrderRuleException("Geçersiz sipariş durumu.");

    public static bool TryFromWire(string? value, out OrderStatus status)
    {
        switch (value)
        {
            case Placed:
                status = OrderStatus.Placed;
                return true;
            case Processing:
                status = OrderStatus.Processing;
                return true;
            case Shipped:
                status = OrderStatus.Shipped;
                return true;
            case Completed:
                status = OrderStatus.Completed;
                return true;
            case Cancelled:
                status = OrderStatus.Cancelled;
                return true;
            default:
                status = default;
                return false;
        }
    }
}

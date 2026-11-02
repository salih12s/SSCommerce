namespace SSCommerce.Domain.Ordering;

/// <summary><see cref="OrderStateMachine.Evaluate"/> sonucu. Servis katmanı bunu HTTP kodlarına çevirir.</summary>
public enum OrderTransitionDecision
{
    /// <summary>Geçiş yapılabilir.</summary>
    Allowed = 1,

    /// <summary>Sipariş zaten hedef durumda: yazma yok, istek yinelemedir (200 <c>replayed</c>).</summary>
    SameStatus = 2,

    /// <summary>Tabloda böyle bir geçiş yok ya da müşteri için sipariş artık iptal edilebilir durumda değil (409 <c>order_transition_invalid</c>).</summary>
    InvalidTransition = 3,

    /// <summary>Geçiş tabloda var ama bu rol yapamaz: yalnız Warehouse'un iptali ya da müşterinin iptal dışı hedefi (403 <c>forbidden</c>, D-063).</summary>
    Forbidden = 4,
}

/// <summary>
/// Sipariş durum makinesi: geçiş tablosunun ve "kim ne yapabilir" kararının TEK kaynağı (D-063, docs/CONTRACT_PHASE_7.md).
/// <code>
/// placed -> processing | cancelled
/// processing -> shipped | cancelled
/// shipped -> completed
/// completed, cancelled: son durum
/// </code>
/// Yetki: Müşteri yalnız <c>placed -> cancelled</c>; Warehouse yalnız ileri geçişler (iptal stok yazar, D-045 gereği Admin ister);
/// Admin ileri geçişler ve <c>placed/processing -> cancelled</c>.
/// </summary>
public static class OrderStateMachine
{
    // Geçiş tablosu. Sıra anlamsızdır.
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Transitions = new()
    {
        [OrderStatus.Placed] = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.Processing] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Completed],
        [OrderStatus.Completed] = [],
        [OrderStatus.Cancelled] = [],
    };

    /// <summary>Verilen durumdan tabloya göre gidilebilen durumlar (rolden bağımsız).</summary>
    public static IReadOnlyList<OrderStatus> NextStatuses(OrderStatus from) =>
        Transitions.TryGetValue(from, out var next) ? next : throw new OrderRuleException("Geçersiz sipariş durumu.");

    public static bool IsTerminal(OrderStatus status) => NextStatuses(status).Count == 0;

    /// <summary>Tabloda <paramref name="from"/> → <paramref name="to"/> kenarı var mı? (Rolden ve "aynı durum"dan bağımsız.)</summary>
    public static bool IsKnownTransition(OrderStatus from, OrderStatus to) =>
        Transitions.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    /// <summary>
    /// Kararın sırası: (1) aynı durum → <see cref="OrderTransitionDecision.SameStatus"/>; (2) tabloda kenar yok →
    /// <see cref="OrderTransitionDecision.InvalidTransition"/>; (3) rol kuralı.
    /// Rol kuralı: Admin her kenara sahiptir; Warehouse iptal edemez (<see cref="OrderTransitionDecision.Forbidden"/>);
    /// Müşteri yalnız iptal edebilir: <c>placed</c> dışındaki durumdan iptal durum çatışmasıdır
    /// (<see cref="OrderTransitionDecision.InvalidTransition"/>), iptal dışı hedef ise yetki sorunudur (<see cref="OrderTransitionDecision.Forbidden"/>).
    /// "Aynı durum" yetkiden ÖNCE değerlendirilir (sözleşme: tekrar istek yazmaz, 200 replayed); rol kapısını servis daha erken uygulayabilir.
    /// Bilinmeyen enum değeri <see cref="OrderRuleException"/> fırlatır.
    /// </summary>
    public static OrderTransitionDecision Evaluate(OrderStatus from, OrderStatus to, OrderActor actor)
    {
        _ = OrderStatuses.ToWire(from);
        _ = OrderStatuses.ToWire(to);
        _ = OrderActorKinds.FromActor(actor);

        if (from == to)
        {
            return OrderTransitionDecision.SameStatus;
        }

        if (!IsKnownTransition(from, to))
        {
            return OrderTransitionDecision.InvalidTransition;
        }

        var cancelling = to == OrderStatus.Cancelled;
        return actor switch
        {
            OrderActor.Admin => OrderTransitionDecision.Allowed,
            OrderActor.Warehouse => cancelling ? OrderTransitionDecision.Forbidden : OrderTransitionDecision.Allowed,
            // Müşteri: tabloda kenar varsa sorun yalnız iptal dışı hedefte (yetki) ya da placed dışı kaynakta (durum) olabilir.
            _ => !cancelling
                ? OrderTransitionDecision.Forbidden
                : from == OrderStatus.Placed ? OrderTransitionDecision.Allowed : OrderTransitionDecision.InvalidTransition,
        };
    }

    /// <summary>Müşteri bu siparişi şu durumda iptal edebilir mi? (<c>OrderDto.canCancel</c> kaynağı.)</summary>
    public static bool CanCustomerCancel(OrderStatus from) =>
        Evaluate(from, OrderStatus.Cancelled, OrderActor.Customer) == OrderTransitionDecision.Allowed;

    /// <summary>Hedef durum stok iadesi gerektirir mi? Yalnız iptal (D-062).</summary>
    public static bool RequiresRestock(OrderStatus to) => to == OrderStatus.Cancelled;
}

namespace SSCommerce.Domain.Ordering;

/// <summary>
/// Sipariş durum geçmişi satırı. Yalnız eklenir (trigger, D-064). İlk satır <c>null → placed</c>'dir; sonrakiler durum makinesindeki
/// bir kenara denk gelmelidir. Aktör kimliği, türü ve neden müşteri yanıtlarına çıkmaz. Zamanı çağıran verir (TimeProvider).
/// </summary>
public sealed class OrderStatusHistoryEntry
{
    private OrderStatusHistoryEntry()
    {
    }

    public OrderStatusHistoryEntry(
        Guid orderId,
        OrderStatus? fromStatus,
        OrderStatus toStatus,
        Guid actorUserId,
        OrderActorKind actorKind,
        string? reason,
        DateTimeOffset occurredAt)
    {
        if (orderId == Guid.Empty || actorUserId == Guid.Empty)
        {
            throw new OrderRuleException("Sipariş ve işlemi yapan kullanıcı kimliği zorunlu.");
        }

        _ = OrderStatuses.ToWire(toStatus);
        if (fromStatus is null)
        {
            if (toStatus != OrderStatus.Placed)
            {
                throw new OrderRuleException("İlk geçmiş satırı yalnız 'placed' olabilir.");
            }
        }
        else if (!OrderStateMachine.IsKnownTransition(fromStatus.Value, toStatus))
        {
            throw new OrderRuleException("Bu durum geçişi geçersiz.");
        }

        Id = Guid.CreateVersion7();
        OrderId = orderId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ActorUserId = actorUserId;
        _ = OrderActorKinds.ToWire(actorKind); // tanımsız enum değerini erken reddet
        ActorKind = actorKind;
        Reason = OrderRules.OptionalReason(reason);
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>İlk satırda <c>null</c>.</summary>
    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }

    public Guid ActorUserId { get; private set; }

    public OrderActorKind ActorKind { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}

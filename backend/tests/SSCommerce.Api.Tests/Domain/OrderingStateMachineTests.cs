using SSCommerce.Domain.Ordering;

namespace SSCommerce.Api.Tests.Domain;

/// <summary>
/// Sipariş durum makinesinin (D-063) TAM matrisi: her durum × her hedef × her rol = 5 × 5 × 3 = 75 durum. Beklenenler, üretim kodundaki
/// mantığı tekrar yazmak yerine sözleşmedeki tablodan AÇIK kümeler olarak yazılmıştır (kodla aynı hatayı paylaşmasın diye).
/// </summary>
public class OrderStateMachineTests
{
    private const OrderStatus P = OrderStatus.Placed;
    private const OrderStatus Pr = OrderStatus.Processing;
    private const OrderStatus S = OrderStatus.Shipped;
    private const OrderStatus Co = OrderStatus.Completed;
    private const OrderStatus C = OrderStatus.Cancelled;

    // Sözleşmedeki geçiş tablosu: placed -> processing | cancelled; processing -> shipped | cancelled; shipped -> completed.
    private static readonly HashSet<(OrderStatus, OrderStatus)> Edges = [(P, Pr), (P, C), (Pr, S), (Pr, C), (S, Co)];

    // Sözleşmedeki rol tablosu.
    private static readonly HashSet<(OrderStatus, OrderStatus)> CustomerAllowed = [(P, C)];
    private static readonly HashSet<(OrderStatus, OrderStatus)> WarehouseAllowed = [(P, Pr), (Pr, S), (S, Co)];
    private static readonly HashSet<(OrderStatus, OrderStatus)> AdminAllowed = [(P, Pr), (P, C), (Pr, S), (Pr, C), (S, Co)];

    // Tabloda kenarı olan ama rolün yapamadığı geçişler: yetki (403) mi, durum çatışması (409) mı?
    private static readonly HashSet<(OrderStatus, OrderStatus)> CustomerForbidden = [(P, Pr), (Pr, S), (S, Co)]; // iptal dışı hedef: yetki
    private static readonly HashSet<(OrderStatus, OrderStatus)> CustomerConflict = [(Pr, C)];                      // iptal ama placed değil: durum
    private static readonly HashSet<(OrderStatus, OrderStatus)> WarehouseForbidden = [(P, C), (Pr, C)];            // iptal stok yazar: Admin ister

    public static TheoryData<OrderStatus, OrderStatus, OrderActor, OrderTransitionDecision> FullMatrix()
    {
        var data = new TheoryData<OrderStatus, OrderStatus, OrderActor, OrderTransitionDecision>();
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                foreach (var actor in Enum.GetValues<OrderActor>())
                {
                    data.Add(from, to, actor, Expected(from, to, actor));
                }
            }
        }

        return data;
    }

    private static OrderTransitionDecision Expected(OrderStatus from, OrderStatus to, OrderActor actor)
    {
        if (from == to)
        {
            return OrderTransitionDecision.SameStatus;
        }

        if (!Edges.Contains((from, to)))
        {
            return OrderTransitionDecision.InvalidTransition;
        }

        var edge = (from, to);
        return actor switch
        {
            OrderActor.Customer when CustomerAllowed.Contains(edge) => OrderTransitionDecision.Allowed,
            OrderActor.Customer when CustomerForbidden.Contains(edge) => OrderTransitionDecision.Forbidden,
            OrderActor.Customer when CustomerConflict.Contains(edge) => OrderTransitionDecision.InvalidTransition,
            OrderActor.Warehouse when WarehouseAllowed.Contains(edge) => OrderTransitionDecision.Allowed,
            OrderActor.Warehouse when WarehouseForbidden.Contains(edge) => OrderTransitionDecision.Forbidden,
            OrderActor.Admin when AdminAllowed.Contains(edge) => OrderTransitionDecision.Allowed,
            _ => throw new InvalidOperationException($"Test tablosu eksik: {from} -> {to} ({actor})"),
        };
    }

    [Fact]
    public void The_matrix_has_every_combination_exactly_once()
    {
        Assert.Equal(75, FullMatrix().Count());
        Assert.Equal(5, Enum.GetValues<OrderStatus>().Length);
        Assert.Equal(3, Enum.GetValues<OrderActor>().Length);
    }

    [Theory]
    [MemberData(nameof(FullMatrix))]
    public void Evaluate_matches_the_contract_for_every_status_target_and_actor(
        OrderStatus from,
        OrderStatus to,
        OrderActor actor,
        OrderTransitionDecision expected) =>
        Assert.Equal(expected, OrderStateMachine.Evaluate(from, to, actor));

    [Fact]
    public void The_matrix_contains_the_expected_number_of_each_decision()
    {
        var all = from f in Enum.GetValues<OrderStatus>()
                  from t in Enum.GetValues<OrderStatus>()
                  from a in Enum.GetValues<OrderActor>()
                  select OrderStateMachine.Evaluate(f, t, a);

        var counts = all.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(15, counts[OrderTransitionDecision.SameStatus]); // 5 durum × 3 rol
        Assert.Equal(1 + 3 + 5, counts[OrderTransitionDecision.Allowed]); // müşteri 1, depo 3, admin 5
        Assert.Equal(3 + 2, counts[OrderTransitionDecision.Forbidden]); // müşteri 3 (iptal dışı), depo 2 (iptaller)
        Assert.Equal(75 - 15 - 9 - 5, counts[OrderTransitionDecision.InvalidTransition]);
    }

    [Fact]
    public void Terminal_states_allow_nothing_for_anyone()
    {
        foreach (var terminal in new[] { Co, C })
        {
            Assert.True(OrderStateMachine.IsTerminal(terminal));
            Assert.Empty(OrderStateMachine.NextStatuses(terminal));
            foreach (var to in Enum.GetValues<OrderStatus>().Where(s => s != terminal))
            {
                foreach (var actor in Enum.GetValues<OrderActor>())
                {
                    Assert.Equal(OrderTransitionDecision.InvalidTransition, OrderStateMachine.Evaluate(terminal, to, actor));
                }
            }
        }

        Assert.False(OrderStateMachine.IsTerminal(P));
        Assert.False(OrderStateMachine.IsTerminal(Pr));
        Assert.False(OrderStateMachine.IsTerminal(S));
    }

    [Fact]
    public void Next_statuses_equal_the_contract_table()
    {
        Assert.Equivalent(new[] { Pr, C }, OrderStateMachine.NextStatuses(P));
        Assert.Equivalent(new[] { S, C }, OrderStateMachine.NextStatuses(Pr));
        Assert.Equivalent(new[] { Co }, OrderStateMachine.NextStatuses(S));
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                Assert.Equal(Edges.Contains((from, to)), OrderStateMachine.IsKnownTransition(from, to));
            }
        }
    }

    [Theory]
    [InlineData(OrderStatus.Placed, true)]
    [InlineData(OrderStatus.Processing, false)]
    [InlineData(OrderStatus.Shipped, false)]
    [InlineData(OrderStatus.Completed, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    public void A_customer_can_cancel_only_a_placed_order(OrderStatus status, bool expected) =>
        Assert.Equal(expected, OrderStateMachine.CanCustomerCancel(status));

    [Fact]
    public void Only_cancelling_requires_a_restock()
    {
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.Equal(status == C, OrderStateMachine.RequiresRestock(status));
        }
    }

    [Fact]
    public void Undefined_enum_values_are_rejected()
    {
        Assert.Throws<OrderRuleException>(() => OrderStateMachine.Evaluate((OrderStatus)0, P, OrderActor.Admin));
        Assert.Throws<OrderRuleException>(() => OrderStateMachine.Evaluate(P, (OrderStatus)99, OrderActor.Admin));
        Assert.Throws<OrderRuleException>(() => OrderStateMachine.Evaluate(P, Pr, (OrderActor)0));
        Assert.Throws<OrderRuleException>(() => OrderStateMachine.NextStatuses((OrderStatus)42));
    }

    [Theory]
    [InlineData(OrderActor.Customer, OrderActorKind.Customer)]
    [InlineData(OrderActor.Warehouse, OrderActorKind.Staff)]
    [InlineData(OrderActor.Admin, OrderActorKind.Staff)]
    public void Actors_map_to_the_history_actor_kind(OrderActor actor, OrderActorKind expected) =>
        Assert.Equal(expected, OrderActorKinds.FromActor(actor));
}

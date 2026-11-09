namespace SSCommerce.Domain.Ordering;

/// <summary>
/// Durum değişikliğini yapan rol (D-063). Yetki kararının girdisidir; kalıcı kayıtta yalnız <see cref="OrderActorKind"/> tutulur
/// (müşteri ya da personel), çünkü Warehouse/Admin ayrımı kullanıcının o anki rolünden okunabilir ve geçmişe sızmaz.
/// </summary>
public enum OrderActor
{
    Customer = 1,
    Warehouse = 2,
    Admin = 3,
}

/// <summary>Durum geçmişinde saklanan aktör türü. DB: <c>ck_order_status_history_actor_kind</c>.</summary>
public enum OrderActorKind
{
    Customer = 1,
    Staff = 2,
}

/// <summary><see cref="OrderActorKind"/> ile veritabanı metni arasındaki tek dönüşüm noktası.</summary>
public static class OrderActorKinds
{
    public const string Customer = "customer";
    public const string Staff = "staff";

    public static readonly IReadOnlyList<string> All = [Customer, Staff];

    public static string ToWire(OrderActorKind kind) => kind switch
    {
        OrderActorKind.Customer => Customer,
        OrderActorKind.Staff => Staff,
        _ => throw new OrderRuleException("Geçersiz aktör türü."),
    };

    public static OrderActorKind FromWire(string value) => value switch
    {
        Customer => OrderActorKind.Customer,
        Staff => OrderActorKind.Staff,
        _ => throw new OrderRuleException("Geçersiz aktör türü."),
    };

    /// <summary>Rolü geçmiş kaydındaki türe çevirir: Warehouse ve Admin "staff"tır.</summary>
    public static OrderActorKind FromActor(OrderActor actor) => actor switch
    {
        OrderActor.Customer => OrderActorKind.Customer,
        OrderActor.Warehouse or OrderActor.Admin => OrderActorKind.Staff,
        _ => throw new OrderRuleException("Geçersiz sipariş aktörü."),
    };
}

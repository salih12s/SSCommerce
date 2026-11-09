using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Customers;

namespace SSCommerce.Domain.Ordering;

/// <summary>
/// Sipariş başlığı (D-059..D-064). Ödenmemiş operasyon kaydıdır: ödeme, <c>paid</c> bayrağı, kargo ya da genel toplam YOKTUR (D-061).
/// <c>orders</c> satırı sonradan DEĞİŞMEZ; yalnız <see cref="Status"/> ve <c>updated_at</c> değişebilir ve bunu veritabanı trigger'ı
/// zorlar (<c>trg_orders_restrict_update</c>). Teslimat adresi sipariş anındaki KOPYADIR (adrese FK yok): adres sonradan düzenlense ya da
/// silinse de sipariş aynı kalır. Kalemler, stok dağıtımı ve durum geçmişi ayrı değişmez tablolardır; kimlikle bağlanır.
/// Kişisel veridir (alıcı adı, telefon, adres): loga ve URL'ye yazılmaz.
/// </summary>
public sealed class Order : AuditedEntity
{
    private Order()
    {
        IdempotencyKey = string.Empty;
        RequestFingerprint = string.Empty;
        DeliveryLabel = string.Empty;
        DeliveryRecipientName = string.Empty;
        DeliveryPhone = string.Empty;
        DeliveryProvince = string.Empty;
        DeliveryDistrict = string.Empty;
        DeliveryStreetAddress = string.Empty;
        Currency = OrderRules.Currency;
        ShippingStatus = OrderRules.ShippingNotCalculated;
    }

    /// <summary>
    /// Teslimat adresini kullanıcının kayıtlı adresinden kopyalar. Adres başka kullanıcıya aitse reddedilir (sahiplik son kez burada
    /// denetlenir). Sıra numarası (<see cref="OrderNumber"/>) veritabanında atanır; ekleme sonrası EF geri okur.
    /// </summary>
    public Order(Guid userId, string idempotencyKey, string requestFingerprint, CustomerAddress address, decimal itemSubtotal)
        : this(
            userId,
            idempotencyKey,
            requestFingerprint,
            (address ?? throw new OrderRuleException("Teslimat adresi zorunlu.")).UserId == userId
                ? address.Label
                : throw new OrderRuleException("Teslimat adresi bu kullanıcıya ait değil."),
            address.RecipientName,
            address.Phone,
            address.Province,
            address.District,
            address.StreetAddress,
            address.PostalCode,
            itemSubtotal)
    {
    }

    public Order(
        Guid userId,
        string idempotencyKey,
        string requestFingerprint,
        string deliveryLabel,
        string deliveryRecipientName,
        string deliveryPhone,
        string deliveryProvince,
        string deliveryDistrict,
        string deliveryStreetAddress,
        string? deliveryPostalCode,
        decimal itemSubtotal)
        : this()
    {
        if (userId == Guid.Empty)
        {
            throw new OrderRuleException("Kullanıcı kimliği zorunlu.");
        }

        UserId = userId;
        IdempotencyKey = OrderRules.RequireIdempotencyKey(idempotencyKey);
        RequestFingerprint = OrderRules.RequireRequestFingerprint(requestFingerprint);
        DeliveryLabel = OrderRules.RequireAddressText(deliveryLabel, AddressRules.LabelMaxLength, "Adres adı");
        DeliveryRecipientName = OrderRules.RequireAddressText(deliveryRecipientName, AddressRules.RecipientNameMaxLength, "Alıcı adı");
        DeliveryPhone = OrderRules.RequirePhone(deliveryPhone);
        DeliveryProvince = OrderRules.RequireProvince(deliveryProvince);
        DeliveryDistrict = OrderRules.RequireAddressText(deliveryDistrict, AddressRules.DistrictMaxLength, "İlçe");
        DeliveryStreetAddress = OrderRules.RequireAddressText(deliveryStreetAddress, AddressRules.StreetAddressMaxLength, "Açık adres");
        DeliveryPostalCode = OrderRules.OptionalPostalCode(deliveryPostalCode);
        ItemSubtotal = OrderRules.RequireItemSubtotal(itemSubtotal);
        Status = OrderStatus.Placed;
    }

    /// <summary>Veritabanı identity'si (100001'den). Yeni siparişte 0'dır; ekleme sonrası dolar. Rotada kullanılmaz (D-064).</summary>
    public long OrderNumber { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Kullanıcıya özel idempotency anahtarı; <c>(user_id, idempotency_key)</c> benzersizdir.</summary>
    public string IdempotencyKey { get; private set; }

    /// <summary>SHA-256 hex: <c>v1|addressId|quote</c>. Aynı anahtar farklı parmak izi → 409.</summary>
    public string RequestFingerprint { get; private set; }

    public OrderStatus Status { get; private set; }

    public string DeliveryLabel { get; private set; }

    public string DeliveryRecipientName { get; private set; }

    /// <summary>Normalize: tam 10 hane, <c>5</c> ile başlar.</summary>
    public string DeliveryPhone { get; private set; }

    public string DeliveryProvince { get; private set; }

    public string DeliveryDistrict { get; private set; }

    public string DeliveryStreetAddress { get; private set; }

    public string? DeliveryPostalCode { get; private set; }

    /// <summary>Her zaman <c>TRY</c>.</summary>
    public string Currency { get; private set; }

    /// <summary>Kalemlerin ara toplamı (TL, ölçek 2). Kargo ve vergi dahil DEĞİL.</summary>
    public decimal ItemSubtotal { get; private set; }

    /// <summary>Faz 7'de her zaman <see cref="OrderRules.ShippingNotCalculated"/>.</summary>
    public string ShippingStatus { get; private set; }

    /// <summary>Faz 7'de her zaman <c>null</c> (kargo uydurulmaz, D-061). DB CHECK'i de NULL ister.</summary>
    public decimal? ShippingAmount { get; private set; }

    /// <summary>Faz 7'de her zaman <c>null</c> (kesin ödenecek tutar uydurulmaz, D-061). DB CHECK'i de NULL ister.</summary>
    public decimal? GrandTotal { get; private set; }

    /// <summary>"SS-100001" biçimi. Yalnız ekleme sonrası (numara atandıktan sonra) çağrılır.</summary>
    public string DisplayNumber => OrderRules.FormatOrderNumber(OrderNumber);

    /// <summary>Müşteri bu siparişi şu an iptal edebilir mi? (<c>OrderDto.canCancel</c>)</summary>
    public bool CanCustomerCancel => OrderStateMachine.CanCustomerCancel(Status);

    /// <summary>İlk durum geçmişi satırı: <c>null → placed</c>, aktör siparişi veren müşteridir.</summary>
    public OrderStatusHistoryEntry CreateInitialHistory(DateTimeOffset occurredAt) =>
        new(Id, null, OrderStatus.Placed, UserId, OrderActorKind.Customer, null, occurredAt);

    /// <summary>
    /// Durumu durum makinesi üzerinden değiştirir ve eklenecek geçmiş satırını döndürür. Karar <see cref="OrderStateMachine.Evaluate"/>'tir:
    /// yalnız <see cref="OrderTransitionDecision.Allowed"/> işlenir; aynı durum, geçersiz geçiş ve yetkisiz rol
    /// <see cref="OrderRuleException"/> fırlatır (servis bunları önce <c>Evaluate</c> ile ayırıp 200 replayed / 409 / 403 üretir)
    /// ve durum DEĞİŞMEZ. <c>updated_at</c>'i SaveChanges damgalar. Stok iadesi bu metodun işi değildir (<see cref="OrderStateMachine.RequiresRestock"/>).
    /// </summary>
    public OrderStatusHistoryEntry ChangeStatus(OrderStatus target, OrderActor actor, Guid actorUserId, string? reason, DateTimeOffset occurredAt)
    {
        var decision = OrderStateMachine.Evaluate(Status, target, actor);
        if (decision != OrderTransitionDecision.Allowed)
        {
            throw new OrderRuleException(decision switch
            {
                OrderTransitionDecision.SameStatus => "Sipariş zaten bu durumda.",
                OrderTransitionDecision.Forbidden => "Bu rol siparişi bu duruma geçiremez.",
                _ => "Bu durum geçişi geçersiz.",
            });
        }

        // Geçmiş satırı önce kurulur: kurucu doğrulaması (aktör kimliği, neden uzunluğu) başarısızsa durum değişmemiş olur.
        var entry = new OrderStatusHistoryEntry(Id, Status, target, actorUserId, OrderActorKinds.FromActor(actor), reason, occurredAt);
        Status = target;
        return entry;
    }
}

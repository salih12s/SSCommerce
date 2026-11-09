using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Ordering;

namespace SSCommerce.Application.Orders;

// Sipariş sözleşmesi (docs/CONTRACT_PHASE_7.md, D-059..D-064). Para kuruştur (long, "...Minor"). Fiyat, adet, durum ve sahip YALNIZ sunucudan gelir:
// istek gövdesinde userId/priceMinor/status/paid gibi alanlar kayıtta yoktur, yok sayılır.
// Sipariş ÖDENMEMİŞ operasyon kaydıdır: kargo ve genel toplam uydurulmaz (shippingMinor ve grandTotalMinor null).

/// <param name="Id">Sipariş kimliği (rota için). Sipariş numarası rotada KULLANILMAZ.</param>
/// <param name="OrderNumber">Görünen numara, örn. <c>SS-100001</c> (metin).</param>
/// <param name="ItemCount">Kalem adetlerinin toplamı.</param>
/// <param name="ShippingMinor">Faz 7'de her zaman null.</param>
/// <param name="GrandTotalMinor">Faz 7'de her zaman null.</param>
/// <param name="CanCancel">Müşteri için sunucu hesabı: yalnız <c>placed</c> siparişte true.</param>
/// <param name="Replayed">Aynı Idempotency-Key ile tekrar (ya da zaten iptal/aynı durum): hiçbir şey yazılmadı.</param>
public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Currency,
    int ItemCount,
    long SubtotalMinor,
    long? ShippingMinor,
    string ShippingStatus,
    long? GrandTotalMinor,
    IReadOnlyList<OrderItemDto> Items,
    OrderAddressDto DeliveryAddress,
    IReadOnlyList<OrderStatusHistoryDto> StatusHistory,
    bool CanCancel,
    bool Replayed);

public sealed record OrderItemDto(
    Guid Id,
    Guid VariantId,
    string Sku,
    string ProductName,
    string VariantName,
    string ProductSlug,
    string? ImageUrl,
    string? ImageAlt,
    long UnitPriceMinor,
    int Quantity,
    long LineTotalMinor);

/// <summary>Sipariş anındaki teslimat adresi KOPYASI. Kişisel veridir: loga, URL'ye ve hata iletisine yazılmaz.</summary>
public sealed record OrderAddressDto(
    string Label,
    string RecipientName,
    string Phone,
    string Province,
    string District,
    string StreetAddress,
    string? PostalCode);

/// <summary>Müşteri görünümü: işlemi yapanın kimliği, türü ve nedeni GİZLİDİR.</summary>
public sealed record OrderStatusHistoryDto(string? FromStatus, string ToStatus, DateTimeOffset OccurredAt);

/// <summary>Müşteri sipariş listesi satırı. <see cref="PreviewItems"/> en çok 3 kalem içerir (liste ekranı için).</summary>
public sealed record OrderSummaryDto(
    Guid Id,
    string OrderNumber,
    string Status,
    DateTimeOffset CreatedAt,
    string Currency,
    int ItemCount,
    long SubtotalMinor,
    string ShippingStatus,
    bool CanCancel,
    IReadOnlyList<OrderPreviewItemDto> PreviewItems);

public sealed record OrderPreviewItemDto(string ProductName, string VariantName, string? ImageUrl, string? ImageAlt, int Quantity);

// ---- Personel (staff) görünümü: KİŞİSEL VERİ YOKTUR (teslimat adresi, kullanıcı kimliği, e-posta, telefon) ----

/// <summary>Personel sipariş listesi satırı. Müşteriyi tanıtan hiçbir alan taşımaz.</summary>
public sealed record AdminOrderSummaryDto(
    Guid Id,
    string OrderNumber,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Currency,
    int ItemCount,
    long SubtotalMinor,
    string ShippingStatus);

/// <summary>
/// Personelin durum değiştirme yanıtı: <see cref="OrderDto"/> ile aynı ama TESLİMAT ADRESİ YOK (personel sipariş detayı Faz 8'dedir).
/// Geçmişte işlemi yapan türü ve neden görünür; kimlik asla.
/// </summary>
public sealed record AdminOrderDto(
    Guid Id,
    string OrderNumber,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Currency,
    int ItemCount,
    long SubtotalMinor,
    long? ShippingMinor,
    string ShippingStatus,
    long? GrandTotalMinor,
    IReadOnlyList<OrderItemDto> Items,
    IReadOnlyList<AdminOrderHistoryDto> StatusHistory,
    bool Replayed,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AdminOrderAllocationDto>? Allocations = null);

/// <summary>Stok dağıtımı özeti (Faz 8, personel sipariş detayı): hangi SKU hangi depodan kaç adet düşüldü. Kişisel veri YOKTUR.</summary>
public sealed record AdminOrderAllocationDto(string Sku, string WarehouseCode, int Quantity);

/// <summary>
/// Teslimat görünümü (Faz 8): YALNIZ Admin, POST ile (denetim izi bırakır). Kişisel veridir: loga, URL'ye ve listelere girmez.
/// </summary>
public sealed record DeliveryViewDto(
    string RecipientName,
    string Phone,
    string Province,
    string District,
    string StreetAddress,
    string? PostalCode,
    string Label);

public sealed record AdminOrderHistoryDto(string? FromStatus, string ToStatus, DateTimeOffset OccurredAt, string ActorKind, string? Reason);

// ---- İstekler ----

/// <summary>HAM istek gövdesi. userId/priceMinor/status/paid kayıtta yoktur, yok sayılır.</summary>
public sealed record PlaceOrderRequest(Guid? AddressId, string? Quote);

public sealed record StaffStatusRequest(string? Status, string? Reason);

/// <summary>Doğrulanmış sipariş verme komutu. <paramref name="UserId"/> yalnız kimlik doğrulanmış principal'den gelir.</summary>
public sealed record PlaceOrderCommand(Guid UserId, string IdempotencyKey, Guid AddressId, string Quote);

/// <param name="OwnerUserId">Doluysa kayıt yalnız bu kullanıcının siparişiyle sınırlanır (müşteri iptali). Personelde null.</param>
public sealed record OrderStatusCommand(
    Guid OrderId,
    Guid? OwnerUserId,
    OrderStatus Target,
    OrderActor Actor,
    Guid ActorUserId,
    string? Reason);

public sealed record OrderListQuery(int Page, int PageSize);

public sealed record AdminOrderListQuery(OrderStatus? Status, int Page, int PageSize, bool OpenOnly = false);

// ---- Sonuçlar ----

public enum OrderOperationStatus
{
    /// <summary>Sipariş yeni oluştu (201).</summary>
    Placed,

    /// <summary>Durum değişikliği uygulandı (200).</summary>
    Changed,

    /// <summary>Aynı anahtar + aynı içerik ya da zaten hedef durumda: hiçbir şey yazılmadı (200, replayed).</summary>
    Replayed,

    /// <summary>Aynı anahtar, farklı parmak izi (409 idempotency_key_reused).</summary>
    KeyReused,

    CartEmpty,
    CartTotalTooLarge,
    AddressNotFound,

    /// <summary>Sepette pasif satır var; <see cref="OrderOperationResult.ItemIds"/> dolu (409 order_item_inactive).</summary>
    ItemInactive,

    /// <summary>Sunucunun hesapladığı quote gönderilenden farklı (409 order_quote_changed). Hiçbir şey yazılmadı.</summary>
    QuoteChanged,

    /// <summary>Stok yetmiyor; <see cref="OrderOperationResult.ItemIds"/> TÜM eksik satırları listeler (409 insufficient_stock).</summary>
    InsufficientStock,

    /// <summary>Sipariş yok ya da başkasının (aynı yanıt, 404 order_not_found).</summary>
    NotFound,

    /// <summary>Geçiş tablosunda yok (409 order_transition_invalid).</summary>
    TransitionInvalid,

    /// <summary>Rol bu geçişi yapamaz (403 forbidden), örn. Warehouse iptali.</summary>
    Forbidden,

    /// <summary>İptalde iade stok üst sınırını aşardı: tümü geri alındı, sipariş değişmedi (409 stock_limit_exceeded).</summary>
    StockLimitExceeded,

    /// <summary>Kilit zaman aşımı: hiçbir şey yazılmadı, aynı anahtarla tekrar denenebilir (503 busy).</summary>
    Busy,

    /// <summary>Kullanıcının açık (placed + processing) sipariş sayısı üst sınırda (409 open_order_limit, <see cref="OrderOperationResult.Limit"/>). Hiçbir şey yazılmadı.</summary>
    OpenOrderLimit,

    /// <summary>Son 24 saatte verilen sipariş sayısı (durumdan bağımsız) üst sınırda (429 order_daily_limit, <see cref="OrderOperationResult.Limit"/> + <see cref="OrderOperationResult.RetryAfterSeconds"/>). Hiçbir şey yazılmadı.</summary>
    DailyOrderLimit,
}

/// <param name="Order">Müşteri görünümü (sipariş verme, müşteri iptali).</param>
/// <param name="StaffOrder">Personel görünümü (adres yok).</param>
/// <param name="Limit">Yalnız <see cref="OrderOperationStatus.OpenOrderLimit"/> ve <see cref="OrderOperationStatus.DailyOrderLimit"/>: uygulanan üst sınır.</param>
/// <param name="RetryAfterSeconds">Yalnız <see cref="OrderOperationStatus.DailyOrderLimit"/>: Retry-After saniyesi (1..86400).</param>
/// <param name="ItemIds">Yalnız <see cref="OrderOperationStatus.ItemInactive"/> ve <see cref="OrderOperationStatus.InsufficientStock"/>: SEPET satır kimlikleri.</param>
public sealed record OrderOperationResult(
    OrderOperationStatus Status,
    OrderDto? Order = null,
    AdminOrderDto? StaffOrder = null,
    IReadOnlyList<Guid>? ItemIds = null,
    int? Limit = null,
    int? RetryAfterSeconds = null);

// ---- Servis sözleşmeleri (uygulama Infrastructure'da) ----

/// <summary>
/// Sipariş verme: tek READ COMMITTED transaction, sıra <c>docs/CONTRACT_PHASE_7.md</c>'dedir. Her ret rollback'tir; anahtar yalnız
/// başarılı siparişle yazılır.
/// </summary>
public interface IOrderPlacementService
{
    Task<OrderOperationResult> PlaceAsync(PlaceOrderCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Durum geçişi ve iptal: tek READ COMMITTED transaction; <c>orders</c> satırı FOR UPDATE ile iptal-iptal ve iptal-sevkiyat yarışını sıraya sokar.
/// Geçiş izni yalnız Domain'deki <see cref="OrderStateMachine"/>'dedir.
/// </summary>
public interface IOrderStatusService
{
    Task<OrderOperationResult> ChangeStatusAsync(OrderStatusCommand command, CancellationToken cancellationToken);
}

/// <summary>Salt okunur sorgular (AsNoTracking). Müşteri sorguları HER ZAMAN <c>user_id</c> ile sınırlıdır.</summary>
public interface IOrderQueries
{
    Task<OrderDto?> GetAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);

    Task<PagedDto<OrderSummaryDto>> ListAsync(Guid userId, OrderListQuery query, CancellationToken cancellationToken);

    Task<PagedDto<AdminOrderSummaryDto>> ListForStaffAsync(AdminOrderListQuery query, CancellationToken cancellationToken);

    /// <summary>Personel sipariş detayı (Faz 8): kalemler, geçmiş ve stok dağıtımları (SKU, depo kodu, adet). Teslimat adresi ve müşteri kimliği YOKTUR.</summary>
    Task<AdminOrderDto?> GetForStaffAsync(Guid orderId, CancellationToken cancellationToken);
}

// ---- Teslimat görünümü (Faz 8) ----

public enum DeliveryViewStatus
{
    /// <summary>Teslimat bilgisi okundu ve <c>order.delivery_view</c> denetim satırı AYNI transaction'da yazıldı.</summary>
    Viewed,

    /// <summary>Sipariş yok (404 order_not_found). Denetim satırı yazılmaz.</summary>
    NotFound,

    /// <summary>Kilit zaman aşımı ya da deadlock: hiçbir şey yazılmadı (503 busy).</summary>
    Busy,
}

public sealed record DeliveryViewResult(DeliveryViewStatus Status, DeliveryViewDto? Delivery = null);

/// <summary>
/// Admin'in bir siparişin teslimat bilgisini görmesi. POST'tur çünkü okuma bir DENETİM İZİ yazar (GET hiçbir şey yazmaz). Denetim <c>changes</c> kişisel veri
/// taşımaz. Yanıt kişisel veridir: <c>no-store</c>, listelerde/URL'de/logda asla.
/// </summary>
public interface IDeliveryViewService
{
    Task<DeliveryViewResult> ViewAsync(Guid actorUserId, Guid orderId, CancellationToken cancellationToken);
}

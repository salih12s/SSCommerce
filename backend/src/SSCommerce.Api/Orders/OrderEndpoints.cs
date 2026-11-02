using System.Security.Claims;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Http;
using SSCommerce.Api.Inventory;
using SSCommerce.Api.Security;
using SSCommerce.Application.Inventory;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Orders;

/// <summary>
/// Sipariş uçları (F7, D-059..D-064, docs/CONTRACT_PHASE_7.md). Müşteri uçları <c>/api/v1/orders</c> altında <c>RequireAuthorization()</c> ister;
/// personel uçları <c>/api/v1/admin/orders</c> altında <c>WarehouseOrAdmin</c> ister (iptal ayrıca Admin rolü: Domain durum makinesi karar verir).
/// Origin + CSRF <c>MapApiV1</c> grubundan gelir; yazmalar <c>mutation</c> rate limit politikasındadır; yanıtlar <c>no-store</c>'dur.
/// İstek akışı: HTTP → rate limit → auth → Origin + CSRF → bu sınıf (gövde okuma, HTTP eşlemesi) → <see cref="OrderRequestValidator"/> (saf doğrulama)
/// → servis (tek transaction) → PostgreSQL. Kullanıcı kimliği, sahip, fiyat, adet, durum ve rol YALNIZ sunucudandır: gövdedeki
/// <c>userId</c>/<c>priceMinor</c>/<c>status</c>/<c>paid</c> yok sayılır (personel durum ucunda <c>status</c> hedeftir ve Domain'de doğrulanır).
/// Teslimat bilgisi (telefon/adres) URL'ye ve loga girmez; personel yanıtlarında kişisel veri yoktur.
/// </summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder api)
    {
        var orders = api.MapGroup("/orders").WithTags("Orders").RequireAuthorization();

        orders.MapPost(string.Empty, Place)
            .WithName("PlaceOrder")
            .WithSummary("Sepetten sipariş verir (ödeme alınmaz). Idempotency-Key başlığı ve checkout önizlemesinin quote değeri zorunludur.")
            .WithDescription(
                "Idempotency-Key: ^[A-Za-z0-9_-]{16,128}$ (kullanıcı başına tektir). İlk başarı 201; aynı anahtar + aynı içerik 200 (replayed:true, hiçbir şey yazılmaz); " +
                "aynı anahtar + farklı içerik 409 idempotency_key_reused. Gövde: { addressId, quote }; fiyat/adet/durum sunucudan gelir, fazla alanlar yok sayılır. " +
                "Hatalar: 404 address_not_found; 409 cart_empty, cart_total_too_large, order_item_inactive (itemIds), order_quote_changed, insufficient_stock (itemIds); " +
                "503 busy (hiçbir şey yazılmadı, aynı anahtarla tekrar denenir). Reddedilen istek anahtarı tüketmez.")
            .Accepts<PlaceOrderRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<OrderDto>(StatusCodes.Status201Created)
            .Produces<OrderDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        orders.MapGet(string.Empty, List)
            .WithName("ListOrders")
            .WithSummary("Oturumdaki kullanıcının siparişleri, en yeni önce. pageSize varsayılan 10, en çok 50.")
            .Produces<PagedDto<OrderSummaryDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        orders.MapGet("/{id}", Get)
            .WithName("GetOrder")
            .WithSummary("Kendi siparişinin ayrıntısı. Yok ya da başkasının: aynı 404 order_not_found.")
            .Produces<OrderDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        orders.MapPost("/{id}/cancel", Cancel)
            .WithName("CancelOrder")
            .WithSummary("Müşteri kendi 'placed' siparişini iptal eder; stok özgün depolara iade edilir. Zaten iptal: 200 replayed:true.")
            .WithDescription(
                "Hatalar: 404 order_not_found; 409 order_transition_invalid (placed değil), stock_limit_exceeded (iade üst sınırı aşardı: hiçbir şey değişmedi); 503 busy.")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<OrderDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Personel: /admin/orders. AdminEndpoints grubu AdminOnly'dir; Warehouse da girebilsin diye AYRI grup (WarehouseOrAdmin).
        var staff = api.MapGroup("/admin/orders").WithTags("Admin Orders").RequireAuthorization(AuthPolicies.WarehouseOrAdmin);

        staff.MapGet(string.Empty, StaffList)
            .WithName("ListOrdersForStaff")
            .WithSummary("Personel sipariş listesi (status süzgeci). KİŞİSEL VERİ YOKTUR: müşteri, adres, telefon dönmez.")
            .Produces<PagedDto<AdminOrderSummaryDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        staff.MapPost("/{id}/status", StaffStatus)
            .WithName("ChangeOrderStatus")
            .WithSummary("Personel durum geçişi: { status, reason? }. Yanıt teslimat adresi İÇERMEZ. Aynı durum: 200 replayed:true.")
            .WithDescription(
                "İzinler Domain durum makinesindedir: Warehouse yalnız ileri geçiş yapar; iptal (stok iadesi yazar) Admin'dir, Warehouse için 403 forbidden. " +
                "Hatalar: 404 order_not_found; 409 order_transition_invalid, stock_limit_exceeded; 503 busy.")
            .Accepts<StaffStatusRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminOrderDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    private static async Task<IResult> Place(
        HttpContext http,
        ClaimsPrincipal user,
        IOrderPlacementService placement,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<PlaceOrderRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = OrderRequestValidator.ValidatePlace(body, IdempotencyKey(http.Request), user.GetUserId());
        return validation.IsValid
            ? ToResult(await placement.PlaceAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> List(
        ClaimsPrincipal user,
        IOrderQueries queries,
        CancellationToken cancellationToken,
        string? page = null,
        string? pageSize = null)
    {
        // Sorgu parametreleri string: tip hatası (page=abc) varsayılan bağlama 400'üne değil alan bazlı validation_failed'a düşer.
        var validation = OrderRequestValidator.ValidateList(page, pageSize);
        return validation.IsValid
            ? Results.Ok(await queries.ListAsync(user.GetUserId(), validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> Get(string id, ClaimsPrincipal user, IOrderQueries queries, CancellationToken cancellationToken)
    {
        var (orderId, errors) = OrderRequestValidator.ValidateOrderId(id);
        if (orderId is not { } valid)
        {
            return ApiResults.ValidationFailed(errors!);
        }

        var order = await queries.GetAsync(user.GetUserId(), valid, cancellationToken);
        return order is null ? OrderNotFound() : Results.Ok(order);
    }

    private static async Task<IResult> Cancel(string id, ClaimsPrincipal user, IOrderStatusService statuses, CancellationToken cancellationToken)
    {
        var (orderId, errors) = OrderRequestValidator.ValidateOrderId(id);
        if (orderId is not { } valid)
        {
            return ApiResults.ValidationFailed(errors!);
        }

        var userId = user.GetUserId();
        return ToResult(await statuses.ChangeStatusAsync(
            new OrderStatusCommand(valid, OwnerUserId: userId, OrderStatus.Cancelled, OrderActor.Customer, userId, Reason: null),
            cancellationToken));
    }

    private static async Task<IResult> StaffList(
        IOrderQueries queries,
        CancellationToken cancellationToken,
        string? status = null,
        string? page = null,
        string? pageSize = null)
    {
        var validation = OrderRequestValidator.ValidateAdminList(status, page, pageSize);
        return validation.IsValid
            ? Results.Ok(await queries.ListForStaffAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> StaffStatus(
        string id,
        HttpContext http,
        ClaimsPrincipal user,
        IOrderStatusService statuses,
        CancellationToken cancellationToken)
    {
        var (orderId, idErrors) = OrderRequestValidator.ValidateOrderId(id);
        if (orderId is not { } valid)
        {
            return ApiResults.ValidationFailed(idErrors!);
        }

        var (body, bodyError) = await JsonRequestBody.ReadAsync<StaffStatusRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = OrderRequestValidator.ValidateStaffStatus(body);
        if (!validation.IsValid)
        {
            return ApiResults.ValidationFailed(validation.Errors);
        }

        // Aktör rolü yalnız principal'den: Admin rolü varsa Admin, yoksa Warehouse (grup politikası ikisinden birini garanti eder).
        var actor = user.IsInRole(AppRoles.Admin) ? OrderActor.Admin : OrderActor.Warehouse;
        return ToResult(await statuses.ChangeStatusAsync(
            new OrderStatusCommand(valid, OwnerUserId: null, validation.Value!.Target, actor, user.GetUserId(), validation.Value.Reason),
            cancellationToken));
    }

    /// <summary>Başlık yoksa null; birden çok değer ya da bozuk biçim doğrulayıcıda reddedilir (virgülle birleşen metin desene uymaz).</summary>
    private static string? IdempotencyKey(HttpRequest request) =>
        request.Headers.TryGetValue(InventoryEndpoints.IdempotencyKeyHeader, out var values) && values.Count > 0 ? values.ToString() : null;

    private static IResult ToResult(OrderOperationResult result) => result.Status switch
    {
        OrderOperationStatus.Placed => Results.Created($"/api/v1/orders/{result.Order!.Id}", result.Order),
        OrderOperationStatus.Changed or OrderOperationStatus.Replayed =>
            result.Order is not null ? Results.Ok(result.Order) : Results.Ok(result.StaffOrder),
        OrderOperationStatus.KeyReused => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Bu Idempotency-Key farklı bir istekle daha önce kullanılmış", ApiErrorCodes.IdempotencyKeyReused),
        OrderOperationStatus.CartEmpty => ApiResults.Problem(StatusCodes.Status409Conflict, "Sepet boş", ApiErrorCodes.CartEmpty),
        OrderOperationStatus.CartTotalTooLarge => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Sepet toplamı izin verilen üst sınırı aşıyor", ApiErrorCodes.CartTotalTooLarge),
        OrderOperationStatus.AddressNotFound => ApiResults.Problem(StatusCodes.Status404NotFound, "Adres bulunamadı", ApiErrorCodes.AddressNotFound),
        OrderOperationStatus.ItemInactive => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            "Sepetteki bazı ürünler artık satışta değil",
            ApiErrorCodes.OrderItemInactive,
            ItemIds(result)),
        OrderOperationStatus.QuoteChanged => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Fiyatlar ya da sepet değişti, lütfen siparişi yeniden onaylayın", ApiErrorCodes.OrderQuoteChanged),
        OrderOperationStatus.InsufficientStock => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Bazı ürünler için yeterli stok yok", ApiErrorCodes.InsufficientStock, ItemIds(result)),
        OrderOperationStatus.NotFound => OrderNotFound(),
        OrderOperationStatus.TransitionInvalid => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Sipariş bu durumdan istenen duruma geçirilemez", ApiErrorCodes.OrderTransitionInvalid),
        OrderOperationStatus.Forbidden => ApiResults.Problem(
            StatusCodes.Status403Forbidden, "Bu işlem için yetkiniz yok", ApiErrorCodes.Forbidden),
        OrderOperationStatus.StockLimitExceeded => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Stok iadesi üst sınırı aşardı; sipariş değişmedi", ApiErrorCodes.StockLimitExceeded),
        OrderOperationStatus.Busy => ApiResults.Problem(
            StatusCodes.Status503ServiceUnavailable, "Sistem meşgul, lütfen aynı anahtarla tekrar deneyin", ApiErrorCodes.Busy),
        _ => throw new InvalidOperationException("Bilinmeyen sipariş işlem durumu."),
    };

    private static Dictionary<string, object?> ItemIds(OrderOperationResult result) =>
        new() { ["itemIds"] = result.ItemIds ?? [] };

    private static IResult OrderNotFound() =>
        ApiResults.Problem(StatusCodes.Status404NotFound, "Sipariş bulunamadı", ApiErrorCodes.OrderNotFound);
}

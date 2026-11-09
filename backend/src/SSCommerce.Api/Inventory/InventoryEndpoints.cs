using System.Security.Claims;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Http;
using SSCommerce.Api.Security;
using SSCommerce.Application.Inventory;

namespace SSCommerce.Api.Inventory;

/// <summary>
/// Admin depo ve stok uçları (F5.3, D-045..D-047). <c>/api/v1/admin</c> grubuna bağlanır: AdminOnly, Origin + CSRF ve
/// <c>Cache-Control: no-store</c> otomatik uygulanır. Yazmalar <c>mutation</c> rate limit politikasındadır (IP başına, D-053).
/// İstek akışı: HTTP → rate limit → auth (AdminOnly) → Origin + CSRF → bu sınıf (gövde okuma, HTTP eşlemesi)
/// → <see cref="InventoryRequestValidator"/> (saf doğrulama) → <see cref="IInventoryService"/> (tek transaction) → PostgreSQL.
/// Kullanıcı kimliği yalnız principal'dendir; gövdedeki <c>userId</c>/<c>actor</c>/<c>onHand</c> gibi alanlar yok sayılır.
/// </summary>
public static class InventoryEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder admin)
    {
        admin.MapGet("/warehouses", GetWarehouses)
            .WithTags("Admin Inventory")
            .WithName("ListWarehouses")
            .WithSummary("Aktif ve pasif tüm depolar.")
            .Produces<IReadOnlyList<WarehouseDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        admin.MapPost("/warehouses", CreateWarehouse)
            .WithTags("Admin Inventory")
            .WithName("CreateWarehouse")
            .WithSummary("Depo oluşturur. Kod tekrarı 409 warehouse_code_taken.")
            .Accepts<CreateWarehouseRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<WarehouseDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        admin.MapPatch("/warehouses/{id:guid}", UpdateWarehouse)
            .WithTags("Admin Inventory")
            .WithName("UpdateWarehouse")
            .WithSummary("Depo adını ve/veya aktifliğini değiştirir (kod değişmez). Yok: 404 warehouse_not_found.")
            .Accepts<UpdateWarehouseRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<WarehouseDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        admin.MapGet("/inventory/balances", GetBalances)
            .WithTags("Admin Inventory")
            .WithName("ListInventoryBalances")
            .WithSummary("Sayfalı bakiye listesi (warehouseId, variantId, sku süzgeçleri).")
            .Produces<PagedDto<InventoryBalanceDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        admin.MapGet("/inventory/movements", GetMovements)
            .WithTags("Admin Inventory")
            .WithName("ListStockMovements")
            .WithSummary("Sayfalı stok hareketleri, en yeni önce (warehouseId, variantId süzgeçleri).")
            .Produces<PagedDto<StockMovementDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        admin.MapPost("/inventory/receive", Receive)
            .WithTags("Admin Inventory")
            .WithName("ReceiveStock")
            .WithSummary("Depo girişi (quantity 1..1.000.000). Idempotency-Key başlığı zorunlu.")
            .WithDescription(
                "Idempotency-Key: ^[A-Za-z0-9_-]{16,128}$ ve 'order-' ile BAŞLAYAMAZ (sipariş düşüm/iade anahtarlarına ayrılmıştır, 400 validation_failed). İlk uygulama 201 (replayed:false); aynı anahtar + aynı içerik 200 (replayed:true, bakiye değişmez); " +
                "aynı anahtar + farklı içerik 409 idempotency_key_reused. Reddedilen istek anahtarı tüketmez ve hareket bırakmaz. " +
                "Hatalar: 404 warehouse_not_found/variant_not_found, 409 warehouse_inactive/variant_inactive/stock_limit_exceeded, 503 stock_busy.")
            .Accepts<ReceiveStockRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<StockMovementDto>(StatusCodes.Status201Created)
            .Produces<StockMovementDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapPost("/inventory/adjust", Adjust)
            .WithTags("Admin Inventory")
            .WithName("AdjustStock")
            .WithSummary("Artı/eksi stok düzeltmesi (quantityDelta ≠ 0, type adjustment ya da correction). Idempotency-Key zorunlu.")
            .WithDescription(
                "Bakiyeyi sıfırın altına düşürecek işlem 409 insufficient_stock, üst sınırı (1.000.000) aşacak işlem 409 stock_limit_exceeded döner. " +
                "Diğer kurallar receive ucuyla aynıdır.")
            .Accepts<AdjustStockRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<StockMovementDto>(StatusCodes.Status201Created)
            .Produces<StockMovementDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return admin;
    }

    private static async Task<IResult> GetWarehouses(IInventoryQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.GetWarehousesAsync(cancellationToken));

    private static async Task<IResult> CreateWarehouse(
        HttpContext http,
        ClaimsPrincipal user,
        IInventoryService inventory,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<CreateWarehouseRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = InventoryRequestValidator.ValidateCreateWarehouse(body);
        if (!validation.IsValid)
        {
            return ApiResults.ValidationFailed(validation.Errors);
        }

        var result = await inventory.CreateWarehouseAsync(user.GetUserId(), validation.Value!, cancellationToken);
        return result.Status switch
        {
            WarehouseWriteStatus.CodeTaken => ApiResults.Problem(StatusCodes.Status409Conflict, "Bu depo kodu kullanılıyor", ApiErrorCodes.WarehouseCodeTaken),
            WarehouseWriteStatus.Busy => Busy(),
            _ => Results.Json(result.Warehouse, statusCode: StatusCodes.Status201Created),
        };
    }

    private static async Task<IResult> UpdateWarehouse(
        Guid id,
        HttpContext http,
        ClaimsPrincipal user,
        IInventoryService inventory,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<UpdateWarehouseRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = InventoryRequestValidator.ValidateUpdateWarehouse(body);
        if (!validation.IsValid)
        {
            return ApiResults.ValidationFailed(validation.Errors);
        }

        var result = await inventory.UpdateWarehouseAsync(user.GetUserId(), id, validation.Value!, cancellationToken);
        return result.Status switch
        {
            WarehouseWriteStatus.NotFound => WarehouseNotFound(),
            WarehouseWriteStatus.Busy => Busy(),
            _ => Results.Ok(result.Warehouse),
        };
    }

    private static async Task<IResult> GetBalances(
        IInventoryQueries queries,
        CancellationToken cancellationToken,
        string? warehouseId = null,
        string? sku = null,
        string? page = null,
        string? pageSize = null,
        string? variantId = null)
    {
        // Sorgu parametreleri string: tip hatası (page=abc) varsayılan bağlama 400'üne değil alan bazlı validation_failed'a düşer.
        var validation = InventoryRequestValidator.ValidateBalanceQuery(new BalanceListRequest(warehouseId, sku, page, pageSize, variantId));
        return validation.IsValid
            ? Results.Ok(await queries.GetBalancesAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> GetMovements(
        IInventoryQueries queries,
        CancellationToken cancellationToken,
        string? warehouseId = null,
        string? variantId = null,
        string? page = null,
        string? pageSize = null)
    {
        var validation = InventoryRequestValidator.ValidateMovementQuery(new MovementListRequest(warehouseId, variantId, page, pageSize));
        return validation.IsValid
            ? Results.Ok(await queries.GetMovementsAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> Receive(
        HttpContext http,
        ClaimsPrincipal user,
        IInventoryService inventory,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<ReceiveStockRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = InventoryRequestValidator.ValidateReceive(body, IdempotencyKey(http.Request), user.GetUserId());
        return validation.IsValid
            ? ToResult(await inventory.ApplyStockOperationAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> Adjust(
        HttpContext http,
        ClaimsPrincipal user,
        IInventoryService inventory,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<AdjustStockRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = InventoryRequestValidator.ValidateAdjust(body, IdempotencyKey(http.Request), user.GetUserId());
        return validation.IsValid
            ? ToResult(await inventory.ApplyStockOperationAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    /// <summary>Başlık yoksa null; birden çok değer ya da bozuk biçim doğrulayıcıda reddedilir (virgülle birleşen metin desene uymaz).</summary>
    private static string? IdempotencyKey(HttpRequest request) =>
        request.Headers.TryGetValue(IdempotencyKeyHeader, out var values) && values.Count > 0 ? values.ToString() : null;

    private static IResult ToResult(StockOperationResult result) => result.Status switch
    {
        StockOperationStatus.Applied => Results.Json(result.Movement, statusCode: StatusCodes.Status201Created),
        StockOperationStatus.Replayed => Results.Ok(result.Movement),
        StockOperationStatus.KeyReused => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Bu Idempotency-Key farklı bir istekle daha önce kullanılmış", ApiErrorCodes.IdempotencyKeyReused),
        StockOperationStatus.WarehouseNotFound => WarehouseNotFound(),
        StockOperationStatus.VariantNotFound => ApiResults.Problem(
            StatusCodes.Status404NotFound, "Varyant bulunamadı", ApiErrorCodes.VariantNotFound),
        StockOperationStatus.WarehouseInactive => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Depo pasif", ApiErrorCodes.WarehouseInactive),
        StockOperationStatus.VariantInactive => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Varyant pasif", ApiErrorCodes.VariantInactive),
        StockOperationStatus.InsufficientStock => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Yetersiz stok: bakiye sıfırın altına düşemez", ApiErrorCodes.InsufficientStock),
        StockOperationStatus.StockLimitExceeded => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Stok üst sınırı aşılamaz", ApiErrorCodes.StockLimitExceeded),
        StockOperationStatus.Busy => ApiResults.Problem(
            StatusCodes.Status503ServiceUnavailable, "Stok şu an meşgul, lütfen tekrar deneyin", ApiErrorCodes.StockBusy),
        _ => throw new InvalidOperationException("Bilinmeyen stok işlem durumu."),
    };

    private static IResult Busy() =>
        ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "Sistem meşgul, lütfen tekrar deneyin", ApiErrorCodes.Busy);

    private static IResult WarehouseNotFound() =>
        ApiResults.Problem(StatusCodes.Status404NotFound, "Depo bulunamadı", ApiErrorCodes.WarehouseNotFound);
}

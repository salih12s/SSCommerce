using System.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;

namespace SSCommerce.Api.Errors;

/// <summary>Makine okunur hata kodları (RFC 9457 ProblemDetails <c>code</c> uzantısı). İstemciler iletiye değil koda bakar.</summary>
public static class ApiErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string CategoryNotFound = "category_not_found";
    public const string ProductNotFound = "product_not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string BadRequest = "bad_request";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string InternalError = "internal_error";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string CsrfInvalid = "csrf_invalid";
    public const string OriginNotAllowed = "origin_not_allowed";
    public const string InvalidCredentials = "invalid_credentials";
    public const string LogoutFailed = "logout_failed";
    public const string EmailUnavailable = "email_unavailable";
    public const string WarehouseNotFound = "warehouse_not_found";
    public const string VariantNotFound = "variant_not_found";
    public const string WarehouseCodeTaken = "warehouse_code_taken";
    public const string WarehouseInactive = "warehouse_inactive";
    public const string VariantInactive = "variant_inactive";
    public const string InsufficientStock = "insufficient_stock";
    public const string StockLimitExceeded = "stock_limit_exceeded";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string StockBusy = "stock_busy";
    public const string CartItemNotFound = "cart_item_not_found";
    public const string CartQuantityLimit = "cart_quantity_limit";
    public const string CartLineLimit = "cart_line_limit";
    public const string CartTotalTooLarge = "cart_total_too_large";
    public const string CartEmpty = "cart_empty";
    public const string AddressNotFound = "address_not_found";
    public const string AddressLimit = "address_limit";

    // Faz 7 (siparişler, docs/CONTRACT_PHASE_7.md).
    public const string OrderNotFound = "order_not_found";
    public const string OrderItemInactive = "order_item_inactive";
    public const string OrderQuoteChanged = "order_quote_changed";
    public const string OrderTransitionInvalid = "order_transition_invalid";

    // Faz 8 (yönetim, docs/CONTRACT_PHASE_8.md "Errors").
    public const string UserNotFound = "user_not_found";
    public const string VersionConflict = "version_conflict";
    public const string CategorySlugTaken = "category_slug_taken";
    public const string ProductSlugTaken = "product_slug_taken";
    public const string SkuTaken = "sku_taken";
    public const string VariantOptionsTaken = "variant_options_taken";
    public const string CategoryCycle = "category_cycle";
    public const string CategoryTooDeep = "category_too_deep";
    public const string ImageNotApproved = "image_not_approved";
    public const string OpenOrderLimit = "open_order_limit";
    public const string OrderDailyLimit = "order_daily_limit";
    public const string RoleSelfDemotion = "role_self_demotion";
    public const string LastAdmin = "last_admin";

    /// <summary>Kullanıcı başına kilit zaman aşımı (sepet/adres yazması): hiçbir şey yazılmadı, tekrar denenebilir.</summary>
    public const string Busy = "busy";

    /// <summary>Veritabanına ulaşılamıyor (bağlantı düzeyi hata). Merkezî eşleme: <see cref="DatabaseUnavailableMiddleware"/>.</summary>
    public const string ServiceUnavailable = "service_unavailable";
}

public static class ApiProblemDetails
{
    public const string CodeKey = "code";
    public const string TraceIdKey = "traceId";

    /// <summary>
    /// <c>AddProblemDetails(o =&gt; o.CustomizeProblemDetails = ...)</c> kancası. ProblemDetails'in YAZILDIĞI her yerde
    /// (işlenmeyen istisna, gövdesiz 4xx/5xx, <c>Results.Problem</c>, doğrulama hatası) çalışır:
    /// <list type="bullet">
    /// <item><c>code</c>: endpoint kendi kodunu verdiyse korunur, yoksa durum kodundan türetilir.</item>
    /// <item><c>traceId</c>: W3C trace id (yoksa <c>TraceIdentifier</c>); loglarla eşleştirmek içindir, sır değildir.</item>
    /// <item>5xx'te <c>detail</c> boşaltılır: istisna iletisi, bağlantı dizesi ya da yığın izi asla istemciye gitmez.</item>
    /// </list>
    /// </summary>
    public static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;

        if (!problem.Extensions.ContainsKey(CodeKey))
        {
            problem.Extensions[CodeKey] = CodeFor(status);
        }

        problem.Extensions[TraceIdKey] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;

        if (status >= 500)
        {
            problem.Detail = null;
        }
    }

    private static string CodeFor(int status) => status switch
    {
        400 => ApiErrorCodes.BadRequest,
        401 => ApiErrorCodes.Unauthenticated,
        403 => ApiErrorCodes.Forbidden,
        404 => ApiErrorCodes.NotFound,
        405 => ApiErrorCodes.MethodNotAllowed,
        409 => ApiErrorCodes.Conflict,
        415 => ApiErrorCodes.UnsupportedMediaType,
        429 => ApiErrorCodes.RateLimited,
        >= 500 => ApiErrorCodes.InternalError,
        _ => $"http_{status}",
    };
}

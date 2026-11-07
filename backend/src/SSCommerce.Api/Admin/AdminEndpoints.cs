using System.Security.Claims;
using SSCommerce.Api.Inventory;
using SSCommerce.Api.Orders;
using SSCommerce.Api.Security;

namespace SSCommerce.Api.Admin;

public sealed record AdminMeDto(string Id, string? Email, IReadOnlyList<string> Roles);

/// <summary>
/// Rol korumalı admin uçları. Rol bilgisi cookie'deki kimlikten gelir; her istekte security stamp doğrulaması principal'i yeniden kurduğundan (aralık 0)
/// rol değişikliği hemen yansır. Faz 8: katalog, stok/depo, pano/raporlar/denetim, personel rolleri ve teslimat görünümü bu AdminOnly grubundadır
/// (Warehouse'un girebildiği tek yer ayrı gruptaki <c>/admin/orders</c> liste/detay/ileri geçişidir).
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder api)
    {
        // Politika GRUP düzeyinde: /api/v1/admin altına eklenecek her uç, unutulsa bile AdminOnly ile korunur.
        var admin = api.MapGroup("/admin").WithTags("Admin").RequireAuthorization(AuthPolicies.AdminOnly);

        admin.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new AdminMeDto(
                user.FindFirstValue(ClaimTypes.NameIdentifier)!,
                user.FindFirstValue(ClaimTypes.Email),
                user.FindAll(ClaimTypes.Role).Select(c => c.Value).Order(StringComparer.Ordinal).ToArray())))
            .WithName("GetAdminMe")
            .WithSummary("Admin kimliği. Anonimde 401, Admin olmayanda 403.")
            .Produces<AdminMeDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Stok ve depo uçları (Faz 5). Grup AdminOnly olduğundan hepsi korumalıdır (D-045).
        admin.MapInventoryEndpoints();

        // Faz 8: katalog yönetimi, pano/raporlar/denetim, personel rolleri ve (AdminOnly) teslimat görünümü.
        admin.MapAdminCatalogEndpoints();
        admin.MapAdminReportEndpoints();
        admin.MapStaffEndpoints();
        admin.MapDeliveryViewEndpoint();

        return api;
    }
}

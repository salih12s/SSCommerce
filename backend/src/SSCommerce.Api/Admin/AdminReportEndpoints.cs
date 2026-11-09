using SSCommerce.Api.Errors;
using SSCommerce.Application.Administration;
using SSCommerce.Application.Inventory;

namespace SSCommerce.Api.Admin;

/// <summary>
/// Pano ve sınırlı raporlar (Faz 8, docs/CONTRACT_PHASE_8.md "Dashboard and reports"). SALT OKUNUR: hiçbir GET bir şey yazmaz. <c>/api/v1/admin</c> grubunda
/// (AdminOnly, <c>no-store</c>). Warehouse için 403. Para yalnız <c>unpaidSubtotalMinor</c>'dur (gelir/ciro/tahsilat alanı yoktur).
/// </summary>
public static class AdminReportEndpoints
{
    public static IEndpointRouteBuilder MapAdminReportEndpoints(this IEndpointRouteBuilder admin)
    {
        admin.MapGet("/dashboard", GetDashboard)
            .WithTags("Admin Reports")
            .WithName("AdminDashboard")
            .WithSummary("Pano: sipariş durum sayıları, açık siparişler (ödenmemiş ara toplam, yaşlı sayı), katalog ve stok sayıları, son hareketler/olaylar. lowStockThreshold 1..1000, agedHours 1..720.")
            .Produces<DashboardDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        admin.MapGet("/reports/low-stock", GetLowStock)
            .WithTags("Admin Reports")
            .WithName("AdminLowStockReport")
            .WithSummary("Düşük stoklu satılabilir varyantlar (stok ≤ threshold; en az stoklu önce). page 1..1000, pageSize ≤ 50.")
            .Produces<PagedDto<LowStockRowDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        admin.MapGet("/audit", GetAudit)
            .WithTags("Admin Reports")
            .WithName("AdminAuditLog")
            .WithSummary("Değiştirilemez yönetim denetim kaydı, en yeni önce (targetType, targetId süzgeçleri). actorEmail yalnız aktör şu an personelse dolu.")
            .Produces<PagedDto<AuditRowDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        return admin;
    }

    private static async Task<IResult> GetDashboard(
        IDashboardQueries queries,
        CancellationToken cancellationToken,
        string? lowStockThreshold = null,
        string? agedHours = null)
    {
        var validation = AdminReportValidator.ValidateDashboard(lowStockThreshold, agedHours);
        return validation.IsValid
            ? Results.Ok(await queries.GetDashboardAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> GetLowStock(
        IDashboardQueries queries,
        CancellationToken cancellationToken,
        string? threshold = null,
        string? page = null,
        string? pageSize = null)
    {
        var validation = AdminReportValidator.ValidateLowStock(threshold, page, pageSize);
        return validation.IsValid
            ? Results.Ok(await queries.GetLowStockAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> GetAudit(
        IDashboardQueries queries,
        CancellationToken cancellationToken,
        string? targetType = null,
        string? targetId = null,
        string? page = null,
        string? pageSize = null)
    {
        var validation = AdminReportValidator.ValidateAudit(targetType, targetId, page, pageSize);
        return validation.IsValid
            ? Results.Ok(await queries.GetAuditAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }
}

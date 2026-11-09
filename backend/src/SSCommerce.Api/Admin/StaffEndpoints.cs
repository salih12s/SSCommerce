using System.Security.Claims;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Security;
using SSCommerce.Application.Administration;
using SSCommerce.Application.Catalog;

namespace SSCommerce.Api.Admin;

/// <summary>
/// Personel rol yönetimi (Faz 8, D-070). <c>/api/v1/admin</c> grubunda (AdminOnly, Origin + CSRF, <c>no-store</c>). Kimlik HESAP KİMLİĞİYLE verilir; e-posta araması
/// yoktur (e-posta doğrulanmaz, D-036). <c>{role}</c> tam eşleşme <c>Admin</c> ya da <c>Warehouse</c> (büyük/küçük harf, boşluk, <c>Customer</c> 400). Kayıt hiçbir zaman
/// personel rolü vermez. Yanıtlar parola özeti, security stamp, telefon ya da adres taşımaz.
/// </summary>
public static class StaffEndpoints
{
    public static IEndpointRouteBuilder MapStaffEndpoints(this IEndpointRouteBuilder admin)
    {
        admin.MapGet("/users/{id}", GetUser)
            .WithTags("Admin Staff")
            .WithName("AdminGetUser")
            .WithSummary("Hesap özeti: id, email, emailConfirmed, createdAt, roles. Kimlik hesap kimliğidir (e-posta araması yok).")
            .Produces<StaffUserDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapGet("/staff", GetStaff)
            .WithTags("Admin Staff")
            .WithName("AdminListStaff")
            .WithSummary("Admin ya da Warehouse rolü olan hesaplar (en çok 200). Düz dizi.")
            .Produces<IReadOnlyList<StaffUserDto>>(StatusCodes.Status200OK);

        admin.MapPut("/users/{id}/roles/{role}", GrantRole)
            .WithTags("Admin Staff")
            .WithName("AdminGrantRole")
            .WithSummary("Admin ya da Warehouse rolü verir. Zaten varsa 200 changed:false (damga ve denetim yazılmaz). Hedefin oturumları kapanır.")
            .WithDescription("Hatalar: 400 geçersiz rol; 403 yapan artık Admin değil; 404 user_not_found; 409 showcase_account_protected (sabit vitrin hesabı); 503 busy.")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<RoleChangeDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapDelete("/users/{id}/roles/{role}", RevokeRole)
            .WithTags("Admin Staff")
            .WithName("AdminRevokeRole")
            .WithSummary("Admin ya da Warehouse rolünü alır. Hedefin oturumları kapanır. Admin kendi Admin rolünü alamaz.")
            .WithDescription("Hatalar: 400 geçersiz rol; 403 yapan artık Admin değil; 404 user_not_found; 409 role_self_demotion, last_admin, showcase_account_protected (sabit vitrin hesabı); 503 busy.")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<RoleChangeDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return admin;
    }

    private static async Task<IResult> GetUser(string id, IStaffQueries queries, CancellationToken cancellationToken)
    {
        var (userId, errors) = AdminCatalogValidator.ValidateId(id);
        if (userId is not { } valid)
        {
            return ApiResults.ValidationFailed(errors!);
        }

        var user = await queries.GetUserAsync(valid, cancellationToken);
        return user is null ? UserNotFound() : Results.Ok(user);
    }

    private static async Task<IResult> GetStaff(IStaffQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.ListStaffAsync(cancellationToken));

    private static Task<IResult> GrantRole(string id, string role, ClaimsPrincipal user, IStaffRoleService service, CancellationToken cancellationToken) =>
        ChangeRoleAsync(id, role, user, grant: true, service, cancellationToken);

    private static Task<IResult> RevokeRole(string id, string role, ClaimsPrincipal user, IStaffRoleService service, CancellationToken cancellationToken) =>
        ChangeRoleAsync(id, role, user, grant: false, service, cancellationToken);

    private static async Task<IResult> ChangeRoleAsync(
        string id,
        string role,
        ClaimsPrincipal user,
        bool grant,
        IStaffRoleService service,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var (userId, idErrors) = AdminCatalogValidator.ValidateId(id);
        if (userId is null)
        {
            errors["id"] = idErrors!["id"];
        }

        if (!StaffRoleRules.TryParse(role, out var parsedRole))
        {
            errors["role"] = ["role yalnızca tam olarak 'Admin' ya da 'Warehouse' olabilir."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationFailed(errors);
        }

        var actor = user.GetUserId();
        var result = grant
            ? await service.GrantAsync(actor, userId!.Value, parsedRole, cancellationToken)
            : await service.RevokeAsync(actor, userId!.Value, parsedRole, cancellationToken);
        return result.Status switch
        {
            RoleChangeStatus.Ok => Results.Ok(result.Change),
            RoleChangeStatus.UserNotFound => UserNotFound(),
            RoleChangeStatus.ActorNotAdmin => ApiResults.Problem(StatusCodes.Status403Forbidden, "Bu işlem için yetkiniz yok", ApiErrorCodes.Forbidden),
            RoleChangeStatus.ShowcaseProtected => ApiResults.Problem(
                StatusCodes.Status409Conflict, "Vitrin hesaplarının rolleri değiştirilemez", ApiErrorCodes.ShowcaseAccountProtected),
            RoleChangeStatus.SelfDemotion => ApiResults.Problem(
                StatusCodes.Status409Conflict, "Kendi Admin rolünüzü kaldıramazsınız", ApiErrorCodes.RoleSelfDemotion),
            RoleChangeStatus.LastAdmin => ApiResults.Problem(
                StatusCodes.Status409Conflict, "Son Admin kaldırılamaz: en az iki (kilitli olmayan) Admin kalmalı", ApiErrorCodes.LastAdmin),
            _ => ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "Sistem meşgul, lütfen tekrar deneyin", ApiErrorCodes.Busy),
        };
    }

    private static IResult UserNotFound() =>
        ApiResults.Problem(StatusCodes.Status404NotFound, "Hesap bulunamadı", ApiErrorCodes.UserNotFound);
}

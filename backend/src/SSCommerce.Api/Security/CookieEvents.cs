using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SSCommerce.Api.Errors;

namespace SSCommerce.Api.Security;

/// <summary>
/// Cookie şemasının yönlendirme olayları. Varsayılan davranış 302 ile giriş sayfasına yönlendirmektir; bu bir JSON API'dir:
/// kimliksiz → 401, yetkisiz → 403, ikisi de <c>application/problem+json</c> (D-033). ASP.NET Core 10 uç nokta metadata'sına
/// bakarak API uçlarında yönlendirmeyi kendisi kapatır; bu olaylar metadata OLMASA bile (ör. ileride düz bir uç eklenirse) yönlendirmeyi engeller.
/// </summary>
public static class CookieEvents
{
    public static Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Kimlik doğrulaması gerekli", ApiErrorCodes.Unauthenticated);

    public static Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Bu işlem için yetkiniz yok", ApiErrorCodes.Forbidden);

    public static async Task WriteAsync(HttpContext http, int status, string title, string code)
    {
        http.Response.StatusCode = status;
        var problems = http.RequestServices.GetService<IProblemDetailsService>();
        if (problems is null)
        {
            return;
        }

        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions[ApiProblemDetails.CodeKey] = code;
        await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }
}

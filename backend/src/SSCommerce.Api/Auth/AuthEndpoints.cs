using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Http;
using SSCommerce.Api.Security;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Auth;

/// <summary>İstek gövdesi: yalnız e-posta ve parola. Başka alanlar (<c>role</c>, <c>isAdmin</c>...) yok sayılır, hiçbir etkisi yoktur.</summary>
public sealed record CredentialsRequest(string? Email, string? Password);

public sealed record CapabilitiesDto(bool Admin, bool Warehouse);

/// <summary>Oturum açmış kullanıcının güvenli özeti. Parola hash'i, security stamp, token ya da cookie değeri YOKTUR.</summary>
public sealed record AccountDto(Guid Id, string Email, bool EmailConfirmed, IReadOnlyList<string> Roles, CapabilitiesDto Capabilities);

public sealed record CsrfTokenDto(string Token);

/// <summary>
/// Kimlik uçları (F4.2). İstek akışı: HTTP → rate limit → authentication/authorization → Origin + CSRF filtresi → bu sınıf
/// → UserManager/SignInManager (Identity) → EF Core → PostgreSQL. Oturum HttpOnly cookie'dir; JWT/localStorage yok (D-033).
/// </summary>
public static class AuthEndpoints
{
    private const int MaxEmailLength = 254;
    private const int MaxPasswordLength = 128;
    private const int MaxBodyBytes = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapGet("/csrf", GetCsrfToken)
            .WithName("GetCsrfToken")
            .WithSummary("CSRF token'ı verir. Değiştiren isteklerde X-CSRF-TOKEN başlığında gönderilir; login/logout sonrası yeniden alınmalıdır.")
            .RequireRateLimiting(RateLimitPolicies.General)
            .Produces<CsrfTokenDto>(StatusCodes.Status200OK);

        auth.MapPost("/register", Register)
            .WithName("Register")
            .WithSummary("Müşteri hesabı açar ve oturum başlatır. Rol alanı kabul edilmez: hesap her zaman Customer olur.")
            .Accepts<CredentialsRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Credentials)
            .Produces<AccountDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/login", Login)
            .WithName("Login")
            .WithSummary("E-posta ve parola ile cookie oturumu açar. Yanlış parola, olmayan hesap ve kilitli hesap aynı 401 yanıtını alır.")
            .Accepts<CredentialsRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Credentials)
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", Logout)
            .WithName("Logout")
            .WithSummary("Oturumu kapatır ve sunucu tarafında geçersiz kılar (security stamp yenilenir; kullanıcının tüm cookie'leri düşer).")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.General)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        auth.MapGet("/me", GetMe)
            .WithName("GetMe")
            .WithSummary("Oturumdaki kullanıcı. Anonimde 401.")
            .RequireAuthorization()
            .Produces<AccountDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return api;
    }

    private static IResult GetCsrfToken(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new CsrfTokenDto(tokens.RequestToken!));
    }

    private static async Task<IResult> Register(
        HttpContext http,
        UserRegistration registration,
        SignInManager<AppUser> signIn,
        UserManager<AppUser> users,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await ReadBodyAsync(http.Request, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var errors = ValidateForRegistration(body);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationFailed(errors);
        }

        var result = await registration.RegisterAsync(body.Email!.Trim(), body.Password!, cancellationToken);
        switch (result.Status)
        {
            case RegistrationStatus.Invalid:
                return ApiResults.ValidationFailed(result.Errors);
            case RegistrationStatus.EmailTaken:
                // D-035: e-posta doğrulama altyapısı olmadan bu bilgi gizlenemez; rate limit ile sınırlıdır.
                return ApiResults.Problem(StatusCodes.Status409Conflict, "Bu e-posta adresi kullanılamıyor", ApiErrorCodes.EmailUnavailable);
        }

        await signIn.SignInAsync(result.User!, isPersistent: false);
        return Results.Json(await ToAccountAsync(users, result.User!), JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Login(
        HttpContext http,
        CredentialVerifier credentials,
        SignInManager<AppUser> signIn,
        UserManager<AppUser> users,
        ILogger<AuthLog> logger,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await ReadBodyAsync(http.Request, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(body.Email))
        {
            errors["email"] = ["E-posta zorunludur."];
        }

        if (string.IsNullOrEmpty(body.Password))
        {
            errors["password"] = ["Parola zorunludur."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationFailed(errors);
        }

        // Aşırı uzun girdi hash'lenmez/aranmaz; yine de aynı genel yanıt.
        if (body.Email!.Length > MaxEmailLength || body.Password!.Length > MaxPasswordLength)
        {
            return InvalidCredentials();
        }

        // Doğrulama + atomik kilit sayacı + zamanlama eşitleme: bkz. CredentialVerifier. null = yanlış parola / olmayan / kilitli hesap.
        var user = await credentials.VerifyAsync(body.Email.Trim(), body.Password, cancellationToken);
        if (user is null)
        {
            return InvalidCredentials();
        }

        await signIn.SignInAsync(user, isPersistent: false);
        logger.LogInformation("Giriş yapıldı: {UserId}", user.Id);
        return Results.Ok(await ToAccountAsync(users, user));
    }

    private static async Task<IResult> Logout(
        HttpContext http,
        SignInManager<AppUser> signIn,
        UserManager<AppUser> users,
        ISessionRevoker revoker,
        ILogger<AuthLog> logger,
        CancellationToken cancellationToken)
    {
        // Sunucu tarafı iptal (D-033): stamp atomik yenilenir, kullanıcının daha önce verilmiş TÜM cookie'leri geçersiz olur.
        // İptal başarısızsa BAŞARI DÖNÜLMEZ (çalınmış cookie geçerli kalmış olur): 503 logout_failed. Tarayıcı cookie'si yine silinir.
        var revoked = true;
        var userId = Guid.TryParse(users.GetUserId(http.User), out var parsed) ? parsed : (Guid?)null;
        if (userId is { } id)
        {
            try
            {
                await revoker.RevokeAsync(id, cancellationToken);
                logger.LogInformation("Çıkış yapıldı: {UserId}", id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                revoked = false;
                logger.LogError(ex, "Oturum sunucu tarafında iptal edilemedi: {UserId}", id);
            }
        }

        await signIn.SignOutAsync();
        return revoked
            ? Results.NoContent()
            : ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Çıkış tamamlanamadı, lütfen tekrar deneyin",
                ApiErrorCodes.LogoutFailed);
    }

    private static async Task<IResult> GetMe(HttpContext http, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(http.User);
        return user is null
            ? ApiResults.Problem(StatusCodes.Status401Unauthorized, "Kimlik doğrulaması gerekli", ApiErrorCodes.Unauthenticated)
            : Results.Ok(await ToAccountAsync(users, user));
    }

    private static IResult InvalidCredentials() =>
        ApiResults.Problem(StatusCodes.Status401Unauthorized, "E-posta veya parola hatalı", ApiErrorCodes.InvalidCredentials);

    private static async Task<AccountDto> ToAccountAsync(UserManager<AppUser> users, AppUser user)
    {
        var roles = (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal).ToArray();
        var isAdmin = roles.Contains(AppRoles.Admin);
        // warehouse yeteneği = depo alanını kullanabilir (WarehouseOrAdmin politikasıyla aynı kural).
        var warehouse = isAdmin || roles.Contains(AppRoles.Warehouse);
        return new AccountDto(user.Id, user.Email!, user.EmailConfirmed, roles, new CapabilitiesDto(isAdmin, warehouse));
    }

    private static Dictionary<string, string[]> ValidateForRegistration(CredentialsRequest body)
    {
        var errors = new Dictionary<string, string[]>();
        var email = body.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            errors["email"] = ["E-posta zorunludur."];
        }
        else if (email.Length > MaxEmailLength || email.Any(char.IsWhiteSpace) || email.Any(char.IsControl) || !new EmailAddressAttribute().IsValid(email))
        {
            errors["email"] = ["E-posta adresi geçersiz."];
        }

        if (string.IsNullOrEmpty(body.Password))
        {
            errors["password"] = ["Parola zorunludur."];
        }
        else if (body.Password.Length > MaxPasswordLength)
        {
            errors["password"] = [$"Parola en çok {MaxPasswordLength} karakter olabilir."];
        }

        return errors;
    }

    // Gövde okuma ortak yardımcıdadır (Http/JsonRequestBody): bozuk/yanlış tipli JSON her ortamda alan bazlı 400 validation_failed olur.
    private static Task<(CredentialsRequest? Body, Dictionary<string, string[]>? Error)> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken) =>
        JsonRequestBody.ReadAsync<CredentialsRequest>(request, MaxBodyBytes, cancellationToken);

    /// <summary>Günlük kategorisi için işaretçi tip.</summary>
    private sealed class AuthLog;
}

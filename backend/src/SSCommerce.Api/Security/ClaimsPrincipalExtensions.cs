using System.Security.Claims;

namespace SSCommerce.Api.Security;

/// <summary>
/// Geçerli kullanıcının kimliği YALNIZ kimlik doğrulanmış principal'den (cookie → ClaimTypes.NameIdentifier) okunur.
/// İstek gövdesi, rota ya da başlıktan kullanıcı kimliği alınmaz (docs/CONTRACT_PHASES_5_6.md "Ortak kurallar").
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>Kimlik doğrulanmamışsa ya da talep geçerli bir (boş olmayan) Guid değilse false.</summary>
    public static bool TryGetUserId(this ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        return user.Identity?.IsAuthenticated == true &&
               Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out userId) &&
               userId != Guid.Empty;
    }

    /// <summary>
    /// Yetkili uçlarda kullanılır: <c>RequireAuthorization</c> kimliği zaten garanti eder. Garanti bozulursa (yapılandırma hatası)
    /// sessizce başka bir kimliğe düşmek yerine istisna fırlatılır ve ortak hata işleyicisi güvenli 500 döner.
    /// </summary>
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        user.TryGetUserId(out var userId)
            ? userId
            : throw new InvalidOperationException("Kimlik doğrulanmış bir kullanıcı talebi bekleniyordu.");
}

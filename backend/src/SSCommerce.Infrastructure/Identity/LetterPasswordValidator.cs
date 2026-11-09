using Microsoft.AspNetCore.Identity;

namespace SSCommerce.Infrastructure.Identity;

/// <summary>
/// Parola politikasına "en az bir HARF" kuralı (D-035). Identity'nin hazır seçenekleri yalnız büyük/küçük harf ayrımı ister,
/// "herhangi bir harf" için seçenek yoktur. Rakam ve uzunluk Identity seçenekleriyle (<c>RequireDigit</c>, <c>RequiredLength</c>) sağlanır.
/// Hata kodu <see cref="ErrorCode"/>; parola metni hiçbir hata iletisinde yer almaz.
/// </summary>
public sealed class LetterPasswordValidator : IPasswordValidator<AppUser>
{
    public const string ErrorCode = "PasswordRequiresLetter";

    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        if (!string.IsNullOrEmpty(password) && password.Any(char.IsLetter))
        {
            return Task.FromResult(IdentityResult.Success);
        }

        return Task.FromResult(IdentityResult.Failed(new IdentityError
        {
            Code = ErrorCode,
            Description = "Parola en az bir harf içermelidir.",
        }));
    }
}

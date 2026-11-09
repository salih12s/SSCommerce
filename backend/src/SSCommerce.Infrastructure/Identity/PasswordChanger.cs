using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Identity;

public enum PasswordChangeStatus
{
    Changed,

    /// <summary>Mevcut parola yanlış (ya da hesap kilitli). Yanlışlığın nedeni ayrıştırılmaz.</summary>
    CurrentPasswordWrong,

    /// <summary>Yeni parola politikaya uymuyor; <see cref="PasswordChangeResult.NewPasswordErrors"/> dolu.</summary>
    NewPasswordInvalid,

    /// <summary>Hesap satırı aynı anda başka bir yazımla değişti; hiçbir şey yazılmadı, tekrar denenebilir.</summary>
    Conflict,
}

public sealed record PasswordChangeResult(PasswordChangeStatus Status, string[] NewPasswordErrors)
{
    public static PasswordChangeResult Of(PasswordChangeStatus status) => new(status, []);
}

/// <summary>
/// Parola değiştirme kullanım senaryosu (MD-2, D-090). Sıra, güvenlik ve hız nedeniyle bilerek böyledir:
/// <list type="number">
/// <item>Yeni parola politikası (kayıtla AYNI doğrulayıcılar: <c>UserManager.PasswordValidators</c>) ÖNCE denetlenir. Mevcut parola tahmini
/// yapılmadığı için kilit sayacına dokunmaz ve yeni parolanın zayıflığı mevcut parolanın doğruluğunu sızdırmaz.</item>
/// <item>Mevcut parola <see cref="CredentialVerifier"/> ile doğrulanır: giriş ile AYNI atomik kilit sayacı (D-035). Böylece çalınmış bir
/// oturum, parola değiştirme ucunu "mevcut parola" tahmin oracle'ı olarak kullanıp brute force yapamaz; 5 yanlıştan sonra hesap 15 dk kilitlenir.
/// Başarıda sayaç sıfırlanır.</item>
/// <item><c>UserManager.ChangePasswordAsync</c> parola hash'ini yazar ve security stamp'i yeniler (diğer oturumlar kapanır). Doğrulayıcının
/// <c>ExecuteUpdate</c> ile yazdığı <c>ConcurrencyStamp</c> bellekteki kullanıcıyı bayatlatır; bu yüzden yazmadan önce satır yeniden yüklenir,
/// aksi halde başarılı doğrulama sahte bir <c>ConcurrencyFailure</c> üretirdi.</item>
/// </list>
/// Parola metni hiçbir ileti, günlük ya da dönüş değerinde yer almaz.
/// </summary>
public sealed class PasswordChanger(
    UserManager<AppUser> users,
    CredentialVerifier verifier,
    SSCommerceDbContext db,
    ILogger<PasswordChanger> logger)
{
    public async Task<PasswordChangeResult> ChangeAsync(AppUser account, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var policyErrors = await ValidateNewPasswordAsync(account, newPassword);
        if (policyErrors.Length > 0)
        {
            return new PasswordChangeResult(PasswordChangeStatus.NewPasswordInvalid, policyErrors);
        }

        var user = await verifier.VerifyAsync(account.Email!, currentPassword, cancellationToken, forPasswordChange: true);
        if (user is null || user.Id != account.Id)
        {
            return PasswordChangeResult.Of(PasswordChangeStatus.CurrentPasswordWrong);
        }

        await db.Entry(user).ReloadAsync(cancellationToken);

        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            logger.LogInformation("Parola değiştirildi, diğer oturumlar kapatıldı: {UserId}", user.Id);
            return PasswordChangeResult.Of(PasswordChangeStatus.Changed);
        }

        var codes = result.Errors.Select(e => e.Code).ToArray();
        if (codes.Contains("PasswordMismatch"))
        {
            // Doğrulama ile yazma arasında parola başka bir istekle değişti.
            return PasswordChangeResult.Of(PasswordChangeStatus.CurrentPasswordWrong);
        }

        if (codes.Any(PasswordPolicyMessages.IsPolicyViolation))
        {
            return new PasswordChangeResult(
                PasswordChangeStatus.NewPasswordInvalid,
                codes.Where(PasswordPolicyMessages.IsPolicyViolation).Select(PasswordPolicyMessages.For).Distinct().ToArray());
        }

        logger.LogWarning("Parola değiştirilemedi ({Codes}): {UserId}", string.Join(',', codes), user.Id);
        return PasswordChangeResult.Of(PasswordChangeStatus.Conflict);
    }

    private async Task<string[]> ValidateNewPasswordAsync(AppUser user, string newPassword)
    {
        var messages = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, newPassword);
            if (!result.Succeeded)
            {
                messages.AddRange(result.Errors.Select(e => PasswordPolicyMessages.For(e.Code)));
            }
        }

        return messages.Distinct().ToArray();
    }
}

namespace SSCommerce.Infrastructure.Identity;

/// <summary>
/// Parola politikası hata kodlarının sabit Türkçe iletileri. Kayıt (<see cref="UserRegistration"/>) ve parola değiştirme
/// (<see cref="PasswordChanger"/>) AYNI iletileri kullanır: politika tek yerde tanımlı olduğu için iki uç ayrışamaz.
/// Identity iletileri İngilizcedir ve değişebilir; parola metni hiçbir iletide yer almaz.
/// </summary>
public static class PasswordPolicyMessages
{
    public static string For(string code) => code switch
    {
        "PasswordTooShort" => $"Parola en az {IdentityServiceCollectionExtensions.MinPasswordLength} karakter olmalıdır.",
        "PasswordRequiresDigit" => "Parola en az bir rakam içermelidir.",
        LetterPasswordValidator.ErrorCode => "Parola en az bir harf içermelidir.",
        _ => "Parola kurallara uymuyor.",
    };

    /// <summary>Politika ihlali kodu mu? (<c>PasswordMismatch</c> politika değil, yanlış mevcut parolayı anlatır.)</summary>
    public static bool IsPolicyViolation(string code) =>
        code.StartsWith("Password", StringComparison.Ordinal) && code != "PasswordMismatch";
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Identity;

public enum RegistrationStatus
{
    Created,

    /// <summary>E-posta (normalize edilmiş haliyle) zaten kayıtlı. D-035: kayıtta bu bilgi 409 ile verilir.</summary>
    EmailTaken,

    /// <summary>Parola/e-posta kuralı ihlali; <see cref="RegistrationResult.Errors"/> alan → ileti.</summary>
    Invalid,
}

public sealed record RegistrationResult(
    RegistrationStatus Status,
    AppUser? User,
    IReadOnlyDictionary<string, string[]> Errors);

/// <summary>
/// Kayıt kullanım senaryosu: kullanıcıyı oluştur + <c>Customer</c> rolünü ver, ikisi TEK transaction'da (rol verilemezse kullanıcı da
/// kalmaz). Rol sabittir; çağıran hiçbir rol bilgisi veremez (D-032). <c>EmailConfirmed</c> false kalır (D-036).
/// Eşzamanlı iki kayıt aynı e-postayla gelirse veritabanındaki UNIQUE indeks kazananı belirler, kaybeden
/// <see cref="RegistrationStatus.EmailTaken"/> alır (500 değil).
/// </summary>
public sealed class UserRegistration(
    UserManager<AppUser> users,
    SSCommerceDbContext db,
    ILogger<UserRegistration> logger)
{
    public async Task<RegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = new AppUser { UserName = email, Email = email };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var create = await users.CreateAsync(user, password);
            if (!create.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failure(create);
            }

            var role = await users.AddToRoleAsync(user, AppRoles.Customer);
            if (!role.Succeeded)
            {
                // Beklenmeyen durum (rol tablosu eksik): yarım hesap bırakma.
                await transaction.RollbackAsync(cancellationToken);
                logger.LogError("Customer rolü atanamadı; kayıt geri alındı. Hata kodları: {Codes}", string.Join(',', role.Errors.Select(e => e.Code)));
                throw new InvalidOperationException("Customer rolü atanamadı.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Yarış: iki istek de "e-posta yok" gördü, ikincisi UNIQUE indekse çarptı.
            return new RegistrationResult(RegistrationStatus.EmailTaken, null, new Dictionary<string, string[]>());
        }

        logger.LogInformation("Kullanıcı kaydedildi: {UserId}", user.Id);
        return new RegistrationResult(RegistrationStatus.Created, user, new Dictionary<string, string[]>());
    }

    private static RegistrationResult Failure(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName"))
        {
            return new RegistrationResult(RegistrationStatus.EmailTaken, null, new Dictionary<string, string[]>());
        }

        var errors = new Dictionary<string, List<string>>();
        foreach (var error in result.Errors)
        {
            var (field, message) = Map(error);
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        return new RegistrationResult(
            RegistrationStatus.Invalid,
            null,
            errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    // Identity iletileri İngilizcedir ve ileride değişebilir; kodlara göre sabit Türkçe iletiler verilir. Parola/e-posta metni asla yankılanmaz.
    private static (string Field, string Message) Map(IdentityError error) => error.Code switch
    {
        "PasswordTooShort" => ("password", $"Parola en az {IdentityServiceCollectionExtensions.MinPasswordLength} karakter olmalıdır."),
        "PasswordRequiresDigit" => ("password", "Parola en az bir rakam içermelidir."),
        LetterPasswordValidator.ErrorCode => ("password", "Parola en az bir harf içermelidir."),
        "InvalidEmail" or "InvalidUserName" => ("email", "E-posta adresi geçersiz."),
        _ => error.Code.StartsWith("Password", StringComparison.Ordinal)
            ? ("password", "Parola kurallara uymuyor.")
            : ("email", "Kayıt bilgileri geçersiz."),
    };
}

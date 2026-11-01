using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Identity;

public static class IdentityServiceCollectionExtensions
{
    public const int MinPasswordLength = 10;
    public const int MaxFailedAccessAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Identity çekirdeği (D-032, D-035, D-036): kullanıcı/rol yönetimi, EF Core depoları, SignInManager. Cookie şeması ve
    /// HTTP tarafı (<c>AddIdentityCookies</c>, antiforgery, politikalar) Api katmanındadır. Parola hash'ini Identity yapar
    /// (PBKDF2, V3 biçimi); kendi şifre saklama kodu yoktur.
    /// </summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = MinPasswordLength;
                options.Password.RequireDigit = true;          // en az 1 rakam
                options.Password.RequireLowercase = false;     // "en az 1 harf" LetterPasswordValidator ile
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;

                options.User.RequireUniqueEmail = true;
                // Kullanıcı adı = e-posta. Varsayılan izinli karakter listesi geçerli e-postaları (ör. apostrof içerenleri) reddederdi;
                // biçim denetimi e-posta doğrulamasında yapılır.
                options.User.AllowedUserNameCharacters = string.Empty;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
                options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;

                // E-posta sağlayıcısı yok (D-036): doğrulama şartı koşulamaz ve EmailConfirmed hiçbir yerde true yapılmaz.
                options.SignIn.RequireConfirmedEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<SSCommerceDbContext>()
            .AddSignInManager()
            .AddPasswordValidator<LetterPasswordValidator>();

        services.AddScoped<UserRegistration>();
        services.AddScoped<CredentialVerifier>();
        services.AddScoped<ISessionRevoker, SessionRevoker>();
        return services;
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Identity;

/// <summary>
/// E-posta + parola doğrulaması ve KİLİTLEME sayacı (D-035). Dönen <c>null</c> = yanlış parola, olmayan hesap ya da kilitli hesap
/// (çağıran üçü için de aynı 401'i verir).
///
/// Neden <c>SignInManager.CheckPasswordSignInAsync(lockoutOnFailure)</c> değil: o, sayaç artışını yüklenmiş kullanıcıyı
/// <c>ConcurrencyStamp</c> denetimiyle yazarak yapar. Eşzamanlı yanlış parolalarda çatışma sonucu sessizce yutulur ve artışlar
/// kaybolur (saldırgan paralel istekle kilitlemeyi geciktirir). Burada sayaç <b>tek atomik UPDATE</b>'tir
/// (<c>ExecuteUpdateAsync</c>; PostgreSQL satır kilidi serileştirir, READ COMMITTED'da bekleyen UPDATE güncel satırla yeniden değerlendirilir).
/// Satır kilidi + okuma-yazma yerine tek ifade seçildi: daha az gidiş-dönüş, kilit tutma süresi yok, ölü kilit riski yok.
///
/// Zamanlama: her sonuç tam BİR parola hash işlemi yapar (olmayan ve kilitli hesapta sahte hash). Böylece yanıt süresi hesap durumunu sızdırmaz.
/// Kabul edilen risk: bilinen bir e-postaya kasıtlı yanlış denemeyle o hesabı 15 dk kilitlemek mümkündür (hedefli kilitleme DoS'u);
/// rate limit ve kilit süresiyle sınırlıdır, brute force'u engellemek için bilinçli takas.
/// </summary>
public sealed class CredentialVerifier(
    UserManager<AppUser> users,
    SSCommerceDbContext db,
    TimeProvider timeProvider,
    ILogger<CredentialVerifier> logger)
{
    // Hash maliyetini eşitlemek için kullanılan sahte kullanıcı (parola hash'i hesaplanır, hiçbir yere yazılmaz).
    private static readonly AppUser TimingDummyUser = new() { UserName = "timing-dummy" };

    public async Task<AppUser?> VerifyAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            users.PasswordHasher.HashPassword(TimingDummyUser, password);
            logger.LogInformation("Başarısız giriş denemesi (bilinmeyen hesap).");
            return null;
        }

        if (await users.IsLockedOutAsync(user))
        {
            // Kilitliyken parola doğrulanmaz (doğru parola da reddedilir) ama süre farkı olmasın diye hash yine hesaplanır.
            users.PasswordHasher.HashPassword(TimingDummyUser, password);
            logger.LogInformation("Giriş reddedildi, hesap kilitli: {UserId}", user.Id);
            return null;
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await RegisterFailureAsync(user.Id, cancellationToken);
            logger.LogInformation("Başarısız giriş denemesi: {UserId}", user.Id);
            return null;
        }

        await ResetFailuresAsync(user.Id, cancellationToken);
        return user;
    }

    private async Task RegisterFailureAsync(Guid userId, CancellationToken cancellationToken)
    {
        var max = IdentityServiceCollectionExtensions.MaxFailedAccessAttempts;
        var now = timeProvider.GetUtcNow();
        DateTimeOffset? lockUntil = now + IdentityServiceCollectionExtensions.LockoutDuration;
        var concurrencyStamp = Guid.NewGuid().ToString();

        // Tek UPDATE: yeni sayaç = eski + 1; eşiğe ulaşınca sayaç sıfırlanır ve LockoutEnd yazılır (Identity ile aynı kural).
        // Zaten kilitli satır sayılmaz/uzatılmaz (paralel denemeler kilidi uzatamaz). SET ifadeleri ESKİ satır değerlerini okur.
        await db.Users
            .Where(u => u.Id == userId && u.LockoutEnabled && (u.LockoutEnd == null || u.LockoutEnd <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1 >= max ? 0 : u.AccessFailedCount + 1)
                    .SetProperty(u => u.LockoutEnd, u => u.AccessFailedCount + 1 >= max ? lockUntil : u.LockoutEnd)
                    .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp),
                cancellationToken);
    }

    private Task ResetFailuresAsync(Guid userId, CancellationToken cancellationToken)
    {
        var concurrencyStamp = Guid.NewGuid().ToString();
        return db.Users
            .Where(u => u.Id == userId && u.AccessFailedCount > 0)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.AccessFailedCount, 0)
                    .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp),
                cancellationToken);
    }
}

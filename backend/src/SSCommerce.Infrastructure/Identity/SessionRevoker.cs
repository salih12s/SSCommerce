using Microsoft.EntityFrameworkCore;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Identity;

/// <summary>Kullanıcının daha önce verilmiş TÜM oturum cookie'lerini sunucu tarafında geçersiz kılar (D-033).</summary>
public interface ISessionRevoker
{
    /// <summary>Başarılı dönerse bu kullanıcının eski cookie'leri bir sonraki istekte reddedilir. Başarısızlıkta İSTİSNA fırlatır (sessizce yutmaz).</summary>
    Task RevokeAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Security stamp'i TEK atomik <c>UPDATE</c> ile yeniler (<c>ExecuteUpdateAsync</c>). Neden <c>UserManager.UpdateSecurityStampAsync</c> değil:
/// o, yüklenmiş kullanıcıyı <c>ConcurrencyStamp</c> denetimiyle yazar; aynı satıra eşzamanlı bir yazım (ör. başka cihazdan yanlış parola
/// sayacı) <c>ConcurrencyFailure</c> döndürür ve çıkış "başarılı" görünürken eski cookie 8 saat geçerli kalırdı.
/// Tek UPDATE ifadesi satır kilidiyle serileşir, çatışmaz; ConcurrencyStamp da yenilenir, böylece bayat bir yazar sonradan stamp'i EZEMEZ.
/// </summary>
internal sealed class SessionRevoker(SSCommerceDbContext db) : ISessionRevoker
{
    public async Task RevokeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var securityStamp = Guid.NewGuid().ToString();
        var concurrencyStamp = Guid.NewGuid().ToString();

        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.SecurityStamp, securityStamp)
                    .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp),
                cancellationToken);
        // 0 satır etkilenmesi (kullanıcı silinmiş) hata değildir: iptal edilecek oturum kalmamıştır.
    }
}

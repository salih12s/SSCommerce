using Microsoft.AspNetCore.Identity;

namespace SSCommerce.Infrastructure.Identity;

/// <summary>
/// Uygulama kullanıcısı. Parola hash'i, e-posta, kilitleme ve security stamp alanları <see cref="IdentityUser{TKey}"/>'dan gelir
/// (D-032). Gereksiz kişisel veri tutulmaz: ad/telefon alanı bilinçli olarak eklenmedi.
/// <see cref="CreatedAt"/> UTC'dir; DbContext.SaveChanges eklenen kullanıcıda boşsa <see cref="TimeProvider"/> ile damgalar.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }
}

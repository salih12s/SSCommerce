using Microsoft.EntityFrameworkCore;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Administration;

/// <summary>
/// Personel (Admin/Warehouse rolü) e-posta çözümleyicisi. Kişisel veriyi asıl hesap tablosundan okur ve yalnız ŞU AN personel rolü taşıyan
/// kullanıcılar için döndürür: müşteriler (sipariş düşümü/iadesi aktörleri) ve rolü sonradan kalkanlar anonim kalır (null). Sayfa başına TEK sorgu.
/// </summary>
internal static class StaffDirectory
{
    public static async Task<Dictionary<Guid, string>> ResolveEmailsAsync(
        SSCommerceDbContext db,
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var staffRoleIds = db.Roles.Where(r => r.Name == AppRoles.Admin || r.Name == AppRoles.Warehouse).Select(r => r.Id);
        var rows = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.Email != null && db.UserRoles.Any(ur => ur.UserId == u.Id && staffRoleIds.Contains(ur.RoleId)))
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => r.Email!);
    }
}

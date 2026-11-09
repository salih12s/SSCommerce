using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Administration;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Administration;

/// <summary>
/// Hesap ve personel okuma (Faz 8). SALT OKUNUR: hiçbir GET bir şey yazmaz. Yanıtlar parola özeti, security stamp, telefon ya da adres taşımaz.
/// Kimlik e-postayla aranmaz (D-070): yalnız hesap kimliğiyle okunur.
/// </summary>
internal sealed class StaffQueries(SSCommerceDbContext db) : IStaffQueries
{
    public async Task<StaffUserDto?> GetUserAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new { u.Id, u.Email, u.EmailConfirmed, u.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return null;
        }

        var roles = await RolesByUserAsync([id], cancellationToken);
        return new StaffUserDto(user.Id, user.Email ?? string.Empty, user.EmailConfirmed, user.CreatedAt, roles.GetValueOrDefault(id) ?? []);
    }

    public async Task<IReadOnlyList<StaffUserDto>> ListStaffAsync(CancellationToken cancellationToken)
    {
        var staffRoleIds = new[] { AppRoles.AdminId, AppRoles.WarehouseId };
        var users = await db.Users.AsNoTracking()
            .Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && staffRoleIds.Contains(ur.RoleId)))
            .OrderBy(u => u.Email).ThenBy(u => u.Id)
            .Take(StaffRoleRules.StaffListCap)
            .Select(u => new { u.Id, u.Email, u.EmailConfirmed, u.CreatedAt })
            .ToListAsync(cancellationToken);

        var roles = await RolesByUserAsync(users.Select(u => u.Id).ToArray(), cancellationToken);
        return users
            .Select(u => new StaffUserDto(u.Id, u.Email ?? string.Empty, u.EmailConfirmed, u.CreatedAt, roles.GetValueOrDefault(u.Id) ?? []))
            .ToList();
    }

    /// <summary>Verilen kullanıcıların rolleri TEK sorguyla (N+1 yok), ordinal sıralı.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<string>>> RolesByUserAsync(Guid[] userIds, CancellationToken cancellationToken)
    {
        var rows = await (
            from userRole in db.UserRoles.AsNoTracking()
            join role in db.Roles on userRole.RoleId equals role.Id
            where userIds.Contains(userRole.UserId)
            select new { userRole.UserId, role.Name }).ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Name ?? string.Empty).Order(StringComparer.Ordinal).ToList());
    }
}

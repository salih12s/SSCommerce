using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Administration;
using SSCommerce.Domain.Administration;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Administration;

/// <summary>
/// Personel rol yönetimi (Faz 8, D-070). TEK READ COMMITTED transaction ve sıra sözleşmededir:
/// <list type="number">
/// <item><c>SET LOCAL lock_timeout = 5s</c> (55P03 ve 40P01 =&gt; <see cref="RoleChangeStatus.Busy"/>).</item>
/// <item><c>pg_advisory_xact_lock(hashtextextended('identity:roles', 0))</c>: TÜM rol değişiklikleri (ve ilk-Admin komutu) sıraya girer. Bu kilit
/// "iki Admin birbirini aynı anda düşürür" yarışını kapatır: ikincisi kilidi aldığında birincinin commit'ini görür.</item>
/// <item>Yapanın hâlâ Admin olduğu TAZE SQL ile doğrulanır (cookie'deki rol eski olabilir): değilse 403.</item>
/// <item>Hedef var mı (404); kendini düşürme (409); son Admin: kilitli OLMAYAN Admin sayısı (taze SQL) 2'den azsa Admin alınamaz (409).</item>
/// <item>Uygula; ZATEN istenen durumdaysa <c>changed:false</c> ve ne damga ne denetim yazılır.</item>
/// <item>Hedefin oturumları iptal edilir (<see cref="ISessionRevoker"/>, AYNI DbContext/transaction: rol değişti ama eski cookie yaşıyor durumu olmaz).</item>
/// <item>Denetim <c>role.grant</c>/<c>role.revoke</c>: yalnız <c>{role}</c> (e-posta, hedef kimliği satırın <c>target_id</c>'sindedir). Commit.</item>
/// </list>
/// Rol adları beyaz listeden gelir (<see cref="StaffRoleRules"/>); rol kimlikleri sabittir (<see cref="AppRoles"/>).
/// </summary>
internal sealed class StaffRoleService(SSCommerceDbContext db, ISessionRevoker sessions, TimeProvider clock) : IStaffRoleService
{
    private const string RolesLockSql = "SELECT pg_advisory_xact_lock(hashtextextended('identity:roles', 0))";

    public Task<RoleChangeResult> GrantAsync(Guid actorUserId, Guid targetUserId, string role, CancellationToken cancellationToken) =>
        ChangeAsync(actorUserId, targetUserId, role, grant: true, cancellationToken);

    public Task<RoleChangeResult> RevokeAsync(Guid actorUserId, Guid targetUserId, string role, CancellationToken cancellationToken) =>
        ChangeAsync(actorUserId, targetUserId, role, grant: false, cancellationToken);

    private async Task<RoleChangeResult> ChangeAsync(Guid actorUserId, Guid targetUserId, string role, bool grant, CancellationToken cancellationToken)
    {
        var roleId = RoleIdOf(role);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
            await TransactionalSql.ExecuteAsync(db, transaction, RolesLockSql, cancellationToken);

            // Taze SQL: yapan hâlâ Admin mi? (kilidi bekleyen istek rakibin commit'ini görür)
            if (!await db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == actorUserId && ur.RoleId == AppRoles.AdminId, cancellationToken))
            {
                return new RoleChangeResult(RoleChangeStatus.ActorNotAdmin);
            }

            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == targetUserId, cancellationToken))
            {
                return new RoleChangeResult(RoleChangeStatus.UserNotFound);
            }

            if (!grant && role == StaffRoleRules.Admin && actorUserId == targetUserId)
            {
                return new RoleChangeResult(RoleChangeStatus.SelfDemotion);
            }

            var has = await db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == targetUserId && ur.RoleId == roleId, cancellationToken);
            if (has == grant)
            {
                // Zaten istenen durumda: değişiklik yok, damga ve denetim yok.
                return new RoleChangeResult(RoleChangeStatus.Ok, new RoleChangeDto(false, await RolesOfAsync(targetUserId, cancellationToken)));
            }

            if (!grant && role == StaffRoleRules.Admin && await EligibleAdminCountAsync(cancellationToken) < 2)
            {
                return new RoleChangeResult(RoleChangeStatus.LastAdmin);
            }

            if (grant)
            {
                db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = targetUserId, RoleId = roleId });
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await db.UserRoles.Where(ur => ur.UserId == targetUserId && ur.RoleId == roleId).ExecuteDeleteAsync(cancellationToken);
            }

            await sessions.RevokeAsync(targetUserId, cancellationToken);
            AdminAudit.Add(
                db,
                clock,
                actorUserId,
                grant ? "role.grant" : "role.revoke",
                AdminAuditRules.TargetUser,
                targetUserId,
                new { role });
            await db.SaveChangesAsync(cancellationToken);
            var roles = await RolesOfAsync(targetUserId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RoleChangeResult(RoleChangeStatus.Ok, new RoleChangeDto(true, roles));
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return new RoleChangeResult(RoleChangeStatus.Busy);
        }
    }

    /// <summary>Şu an kilitli OLMAYAN Admin sayısı (taze SQL). Kilitli Admin giriş yapamaz; yalnız onlar kalırsa sistem yönetilemez.</summary>
    private async Task<int> EligibleAdminCountAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await (
            from userRole in db.UserRoles.AsNoTracking()
            join user in db.Users on userRole.UserId equals user.Id
            where userRole.RoleId == AppRoles.AdminId && (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= now)
            select userRole.UserId).CountAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> RolesOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        var names = await (
            from userRole in db.UserRoles.AsNoTracking()
            join r in db.Roles on userRole.RoleId equals r.Id
            where userRole.UserId == userId
            select r.Name).ToListAsync(cancellationToken);
        return names.Where(n => n is not null).Select(n => n!).Order(StringComparer.Ordinal).ToList();
    }

    private static Guid RoleIdOf(string role) => role switch
    {
        StaffRoleRules.Admin => AppRoles.AdminId,
        StaffRoleRules.Warehouse => AppRoles.WarehouseId,
        _ => throw new ArgumentOutOfRangeException(nameof(role), "Yalnız Admin ve Warehouse verilebilir."),
    };
}

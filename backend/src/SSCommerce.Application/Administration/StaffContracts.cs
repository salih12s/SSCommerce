namespace SSCommerce.Application.Administration;

// Personel rol yönetimi (Faz 8, D-070, docs/CONTRACT_PHASE_8.md "Staff role management"). Kimlik HESAP KİMLİĞİYLE verilir, e-posta ile değil:
// e-posta doğrulanmaz (D-036) ve kayıt açıktır; bir meslektaşın adresini önceden kaydeden biri rol alabilirdi. E-posta arama yoktur.

/// <summary>Hesap özeti. Parola özeti, security stamp, telefon, adres YOKTUR. <c>Roles</c> ordinal sıralı.</summary>
public sealed record StaffUserDto(Guid Id, string Email, bool EmailConfirmed, DateTimeOffset CreatedAt, IReadOnlyList<string> Roles);

public sealed record RoleChangeDto(bool Changed, IReadOnlyList<string> Roles);

public enum RoleChangeStatus
{
    /// <summary>Uygulandı ya da zaten istenen durumdaydı (<see cref="RoleChangeDto.Changed"/> ayırır): 200.</summary>
    Ok,

    /// <summary>Hedef hesap yok (404 user_not_found).</summary>
    UserNotFound,

    /// <summary>Yapan kişi (taze SQL ile) artık Admin değil (403 forbidden).</summary>
    ActorNotAdmin,

    /// <summary>Admin kendi Admin rolünü alamaz (409 role_self_demotion).</summary>
    SelfDemotion,

    /// <summary>Kilitli olmayan Admin sayısı 2'den az: Admin rolü alınamaz (409 last_admin).</summary>
    LastAdmin,

    /// <summary>Kilit zaman aşımı ya da deadlock: hiçbir şey yazılmadı (503 busy).</summary>
    Busy,
}

public sealed record RoleChangeResult(RoleChangeStatus Status, RoleChangeDto? Change = null);

/// <summary>
/// Rol verme/alma: TEK transaction. Sıra: lock_timeout → <c>pg_advisory_xact_lock('identity:roles')</c> → yapanın Admin olduğu TAZE SQL ile yeniden doğrulanır →
/// hedef var mı → kendini düşürme → son Admin → uygula → hedefin oturumları iptal (aynı DbContext/transaction) → denetim <c>role.grant|revoke</c> (yalnız
/// <c>{role}</c>) → commit. İstenen durumda zaten ise 200 <c>changed:false</c>, damga ve denetim yazılmaz.
/// </summary>
public interface IStaffRoleService
{
    Task<RoleChangeResult> GrantAsync(Guid actorUserId, Guid targetUserId, string role, CancellationToken cancellationToken);

    Task<RoleChangeResult> RevokeAsync(Guid actorUserId, Guid targetUserId, string role, CancellationToken cancellationToken);
}

public interface IStaffQueries
{
    Task<StaffUserDto?> GetUserAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Admin ya da Warehouse rolü olan hesaplar (en çok 200), e-posta sırasıyla.</summary>
    Task<IReadOnlyList<StaffUserDto>> ListStaffAsync(CancellationToken cancellationToken);
}

/// <summary>Rota <c>{role}</c> değeri için SAF beyaz liste: yalnız tam eşleşme <c>Admin</c> ya da <c>Warehouse</c>.</summary>
public static class StaffRoleRules
{
    public const string Admin = "Admin";
    public const string Warehouse = "Warehouse";
    public const int StaffListCap = 200;

    /// <summary>
    /// Tam (ordinal) eşleşme: <c>ADMIN</c>, <c>admin</c>, boşluklu, kodlanmış (<c>%41dmin</c> yönlendirmede zaten çözülür; çözülmüş hali eşleşmeyen her şey) ve
    /// <c>Customer</c> reddedilir. Müşteri rolü bu uçlardan verilemez ve alınamaz.
    /// </summary>
    public static bool TryParse(string? raw, out string role)
    {
        switch (raw)
        {
            case Admin:
                role = Admin;
                return true;
            case Warehouse:
                role = Warehouse;
                return true;
            default:
                role = string.Empty;
                return false;
        }
    }
}

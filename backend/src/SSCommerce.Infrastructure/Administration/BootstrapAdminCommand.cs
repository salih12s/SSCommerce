using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SSCommerce.Domain.Administration;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Administration;

/// <summary>
/// <c>dotnet run --project backend/src/SSCommerce.Api -- --bootstrap-admin</c> komutunun gövdesi (Faz 8, D-070): HTTP ucu DEĞİLDİR, Kestrel başlamaz.
/// <b>İLK</b> Admin'i atar; sonraki Admin'ler rol API'siyle atanır. Kurallar:
/// <list type="bullet">
/// <item>YALNIZ Development ortamı; aksi halde çıkış 1 ve hiçbir şey yazılmaz.</item>
/// <item>Bağlantı dizesinin sunucusu (Host) loopback olmalıdır (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>); uzak bir veritabanına bakan Development profili reddedilir (çıkış 1).</item>
/// <item>Bekleyen migration varsa çıkış 1. Zaten bir Admin varsa çıkış 1.</item>
/// <item>E-posta, KAYITLI bir hesabınkidir: stdin'den iki kez okunur (onay), ASLA yankılanmaz, loglanmaz, denetime yazılmaz. Parola okunmaz/saklanmaz/üretilmez.
/// Bilinmeyen hesap ya da iki giriş uyuşmazsa çıkış 1.</item>
/// <item>TEK transaction: <c>lock_timeout</c> → advisory <c>identity:roles</c> (rol API'siyle AYNI kilit) → "Admin yok" yeniden doğrulanır → rol eklenir →
/// oturumlar iptal → denetim <c>role.bootstrap</c> (aktör = hedef, <c>{role}</c> yalnız) → commit. Eşzamanlı iki çalıştırma TEK Admin ve TEK denetim satırı bırakır.</item>
/// <item>Çıkış kodları: 0 başarılı, 1 ön koşul/giriş reddi (hiçbir şey yazılmadı), 2 beklenmeyen/veritabanı hatası. Hata iletisinde yalnız istisna TÜR adı yazılır.</item>
/// </list>
/// </summary>
public static class BootstrapAdminCommand
{
    public const string Flag = "--bootstrap-admin";

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "::1", "localhost"];

    public static async Task<int> RunAsync(
        IServiceProvider services,
        IHostEnvironment environment,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            await error.WriteLineAsync(
                $"HATA: {Flag} yalnızca Development ortamında çalışır (şu an: {environment.EnvironmentName}). Hiçbir şey yazılmadı.");
            return 1;
        }

        try
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider;

            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(DependencyInjection.ConnectionStringName);
            if (!IsLoopbackConnection(connectionString))
            {
                await error.WriteLineAsync(
                    "HATA: bağlantı dizesinin sunucusu loopback (127.0.0.1, ::1, localhost) değil ya da tanımsız. Hiçbir şey yazılmadı.");
                return 1;
            }

            var db = provider.GetRequiredService<SSCommerceDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            if (pending.Length > 0)
            {
                await error.WriteLineAsync(
                    $"HATA: {pending.Length} bekleyen migration var. Önce şemayı güncelleyin: dotnet ef database update. Hiçbir şey yazılmadı.");
                return 1;
            }

            if (await AdminExistsAsync(db, cancellationToken))
            {
                await error.WriteLineAsync("HATA: zaten bir Admin var. Bu komut yalnız İLK Admin içindir; sonrası için rol API'sini kullanın. Hiçbir şey yazılmadı.");
                return 1;
            }

            await output.WriteLineAsync("Admin yapılacak KAYITLI hesabın e-postasını girin (ekrana yazılmaz, kaydedilmez).");
            await output.WriteAsync("E-posta: ");
            var first = (await input.ReadLineAsync(cancellationToken))?.Trim();
            await output.WriteAsync("E-posta (onay): ");
            var second = (await input.ReadLineAsync(cancellationToken))?.Trim();
            if (string.IsNullOrEmpty(first) || !string.Equals(first, second, StringComparison.Ordinal))
            {
                await error.WriteLineAsync("HATA: iki giriş boş ya da birbirinden farklı. Hiçbir şey yazılmadı.");
                return 1;
            }

            var normalizer = provider.GetRequiredService<ILookupNormalizer>();
            var normalized = normalizer.NormalizeEmail(first);
            var targetId = await db.Users.AsNoTracking()
                .Where(u => u.NormalizedEmail == normalized)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (targetId is null)
            {
                await error.WriteLineAsync("HATA: bu e-postayla kayıtlı hesap yok. Önce siteden normal kayıt olun. Hiçbir şey yazılmadı.");
                return 1;
            }

            var assigned = await AssignAsync(db, provider.GetRequiredService<ISessionRevoker>(), provider.GetRequiredService<TimeProvider>(), targetId.Value, cancellationToken);
            if (!assigned)
            {
                await error.WriteLineAsync("HATA: bu sırada başka bir Admin atandı. Hiçbir şey yazılmadı.");
                return 1;
            }

            await output.WriteLineAsync("Tamam: ilk Admin atandı. Hesabın açık oturumları kapatıldı; yeniden giriş yapılmalı.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // İleti yerine yalnızca tür adı: Npgsql istisna iletileri sunucu/kullanıcı bilgisi taşıyabilir.
            await error.WriteLineAsync($"HATA: ilk Admin atanamadı ({ex.GetType().Name}). Veritabanı çalışıyor mu ve ConnectionStrings__Postgres tanımlı mı?");
            return 2;
        }
    }

    /// <summary>
    /// Bağlantı dizesinin TÜM sunucuları loopback ise true. Ayrıştırılamayan, boş ya da sunucu içermeyen dize false'tur (yalnızca "güvenli" olduğu kanıtlananlar geçer).
    /// Dize hiçbir yerde yazdırılmaz.
    /// </summary>
    public static bool IsLoopbackConnection(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var hosts = (builder.Host ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return hosts.Length > 0 && hosts.All(h => LoopbackHosts.Contains(h, StringComparer.OrdinalIgnoreCase));
    }

    private static Task<bool> AdminExistsAsync(SSCommerceDbContext db, CancellationToken cancellationToken) =>
        db.UserRoles.AsNoTracking().AnyAsync(ur => ur.RoleId == AppRoles.AdminId, cancellationToken);

    /// <summary>Tek transaction: kilit → "Admin yok" yeniden doğrulama → rol → oturum iptali → denetim. Başka bir Admin çıktıysa false (yazma yok).</summary>
    private static async Task<bool> AssignAsync(
        SSCommerceDbContext db,
        ISessionRevoker sessions,
        TimeProvider clock,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
        await TransactionalSql.ExecuteAsync(db, transaction, "SELECT pg_advisory_xact_lock(hashtextextended('identity:roles', 0))", cancellationToken);

        // Kilidi aldıktan SONRA taze okuma: bekleyen eşzamanlı çalıştırma rakibin commit'ini görür.
        if (await AdminExistsAsync(db, cancellationToken))
        {
            return false;
        }

        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = targetUserId, RoleId = AppRoles.AdminId });
        await db.SaveChangesAsync(cancellationToken);
        await sessions.RevokeAsync(targetUserId, cancellationToken);
        AdminAudit.Add(db, clock, targetUserId, "role.bootstrap", AdminAuditRules.TargetUser, targetUserId, new { role = AppRoles.Admin });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

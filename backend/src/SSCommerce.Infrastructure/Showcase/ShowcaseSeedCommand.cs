using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Showcase;

/// <summary>
/// <c>dotnet run --project backend/src/SSCommerce.Api -- --seed-showcase</c> komutunun gövdesi (VT-2, D-092). HTTP ucu DEĞİLDİR, Kestrel başlamaz.
/// Kurallar (<see cref="DemoCatalogSeedCommand"/> ve <see cref="BootstrapAdminCommand"/> ile aynı korumalar):
/// <list type="bullet">
/// <item>YALNIZ Development ortamı VE loopback veritabanı sunucusu; aksi halde çıkış 1 ve hiçbir şey yazılmaz.</item>
/// <item>Bekleyen migration varsa çıkış 1. Migration UYGULANMAZ.</item>
/// <item>Çıktıda parola, e-posta, kimlik ya da bağlantı dizesi YOKTUR; yalnız sayılar. Hata iletilerinde istisna TÜR adı yazılır.</item>
/// <item>İdempotent: ikinci koşu hiçbir şey çoğaltmaz; yarım kalmış durumda eksikleri tamamlar.</item>
/// <item>Çıkış kodları: 0 başarılı, 1 ön koşul reddi / beklenen kural hatası, 2 veritabanı ya da beklenmeyen hata.</item>
/// </list>
/// </summary>
public static class ShowcaseSeedCommand
{
    public const string Flag = "--seed-showcase";

    public static async Task<int> RunAsync(
        IServiceProvider services,
        IHostEnvironment environment,
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

        var clock = services.GetRequiredService<TimeProvider>();
        try
        {
            var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(DependencyInjection.ConnectionStringName);
            if (!BootstrapAdminCommand.IsLoopbackConnection(connectionString))
            {
                await error.WriteLineAsync(
                    "HATA: bağlantı dizesinin sunucusu loopback (127.0.0.1, ::1, localhost) değil ya da tanımsız. Hiçbir şey yazılmadı.");
                return 1;
            }

            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
                if (pending.Length > 0)
                {
                    await error.WriteLineAsync(
                        $"HATA: {pending.Length} bekleyen migration var. Seed migration uygulamaz; önce şemayı güncelleyin: dotnet ef database update. Hiçbir şey yazılmadı.");
                    return 1;
                }
            }

            var result = await new ShowcaseSeeder(services, clock).RunAsync(cancellationToken);
            await output.WriteLineAsync(result.Describe());
            return 0;
        }
        catch (ShowcaseSeedException ex)
        {
            await error.WriteLineAsync($"HATA: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // İleti yerine yalnızca tür adı: Npgsql istisna iletileri sunucu/kullanıcı bilgisi taşıyabilir.
            await error.WriteLineAsync(
                $"HATA: vitrin verisi yazılamadı ({ex.GetType().Name}). Veritabanı çalışıyor mu ve ConnectionStrings__Postgres tanımlı mı?");
            return 2;
        }
        finally
        {
            (clock as ShowcaseClock)?.Release();
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SSCommerce.Infrastructure.Persistence.Seed;

/// <summary>
/// <c>dotnet run --project backend/src/SSCommerce.Api -- --seed-demo-catalog</c> komutunun gövdesi.
/// Çıkış kodları: 0 başarılı, 1 yanlış ortam ya da bekleyen migration, 2 veritabanı/beklenmeyen hata.
/// Migration UYGULAMAZ (ayrı adım: <c>dotnet ef database update</c>). Hata iletileri bağlantı dizesi içermez.
/// </summary>
public static class DemoCatalogSeedCommand
{
    public const string Flag = "--seed-demo-catalog";

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

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            if (pending.Length > 0)
            {
                await error.WriteLineAsync(
                    $"HATA: {pending.Length} bekleyen migration var ({string.Join(", ", pending)}). Seed migration uygulamaz; " +
                    "önce şemayı güncelleyin: dotnet ef database update. Hiçbir şey yazılmadı.");
                return 1;
            }

            var result = await new DemoCatalogSeeder(db).SeedAsync(cancellationToken);
            await output.WriteLineAsync(
                $"Demo katalog tamam. Yeni eklenen: {result.Categories} kategori, {result.Products} ürün, " +
                $"{result.Variants} varyant, {result.Images} görsel" +
                (result.Total == 0 ? " (zaten güncel; değişiklik yok)." : "."));
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // İleti yerine yalnızca tür adı: Npgsql istisna iletileri sunucu/kullanıcı bilgisi taşıyabilir.
            await error.WriteLineAsync(
                $"HATA: demo katalog yazılamadı ({ex.GetType().Name}). Veritabanı çalışıyor mu ve ConnectionStrings__Postgres tanımlı mı?");
            return 2;
        }
    }
}

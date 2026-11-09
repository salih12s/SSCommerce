using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SSCommerce.Infrastructure.Persistence;

/// <summary>
/// Yalnızca <c>dotnet ef</c> araçları içindir (çalışma anında kullanılmaz). Bağlantı dizesi ortam değişkeninden
/// (<c>ConnectionStrings__Postgres</c>) gelir. Tanımlı değilse SAHTE bir yer tutucu kullanılır: <c>migrations add/script</c> ve
/// <c>has-pending-model-changes</c> veritabanına bağlanmadan çalışır; <c>database update</c> ise bu yer tutucuyla bağlanamaz
/// (bilinçli: yanlışlıkla bir veritabanına migration uygulanmaz).
/// </summary>
internal sealed class SSCommerceDbContextFactory : IDesignTimeDbContextFactory<SSCommerceDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=127.0.0.1;Port=1;Database=sscommerce_design_time;Username=design_time;Password=design_time_placeholder";

    public SSCommerceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{DependencyInjection.ConnectionStringName}");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = PlaceholderConnectionString;
        }

        var options = new DbContextOptionsBuilder<SSCommerceDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new SSCommerceDbContext(options, TimeProvider.System);
    }
}

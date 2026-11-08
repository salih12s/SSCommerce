using SSCommerce.Api.Addresses;
using SSCommerce.Api.Admin;
using SSCommerce.Api.Auth;
using SSCommerce.Api.Carts;
using SSCommerce.Api.Catalog;
using SSCommerce.Api.Checkout;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Favorites;
using SSCommerce.Api.Health;
using SSCommerce.Api.Observability;
using SSCommerce.Api.OpenApi;
using SSCommerce.Api.Orders;
using SSCommerce.Api.Security;
using SSCommerce.Infrastructure;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Persistence.Seed;
using SSCommerce.Infrastructure.Showcase;

var builder = WebApplication.CreateBuilder(args);

// Seed ve ilk-Admin komutlarında EF SQL günlüğü gürültüsü olmasın (ve komut girdisi/sonucu günlüğe karışmasın).
if (args.Contains(DemoCatalogSeedCommand.Flag) || args.Contains(BootstrapAdminCommand.Flag) || args.Contains(ShowcaseSeedCommand.Flag))
{
    // Taze veritabanında "__EFMigrationsHistory yok" sorgu hatası da beklenen durumdur (bekleyen migration denetimi).
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
}

// Vitrin komutu (VT-2): tarihleri geçmişe yayabilmek için komut çalışırken saat ShowcaseClock'tur (varsayılanı gerçek saat). AddInfrastructure'ın
// TryAddSingleton(TimeProvider.System) kaydı bunu ezmez; sunucu modunda bu kayıt hiç yapılmaz.
if (args.Contains(ShowcaseSeedCommand.Flag))
{
    builder.Services.AddSingleton<TimeProvider, ShowcaseClock>();
}

// RFC 9457 hata modeli: her ProblemDetails'e `code` ve `traceId` eklenir (bkz. ApiProblemDetails).
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblemDetails.Customize);
builder.Services.AddOpenApi(options => options.AddCatalogQueryParameterTypes());
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity();
builder.Services.AddObservability();

// Faz 9: Server başlığı kapalı; Metrics:Port > 0 ise iç metrik dinleyicisi ana adreslere eklenir.
builder.ConfigureHosting();

// Faz 9 (B3): Development dışında konsol günlüğü JSON (tek satır/olay, RequestId kapsamı dahil). Logging:Console:FormatterName açıkça verildiyse ona dokunulmaz.
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["Logging:Console:FormatterName"]))
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    });
}

var app = builder.Build();

// Faz 9: en dış katman (güvenlik başlıkları, /metrics iç dinleyicide, X-Request-Id + telemetri + istek günlüğü).
app.UseObservability();

// Merkezi hata modeli: işlenmeyen istisna => 500, gövdesiz 4xx/5xx => RFC 9457 application/problem+json.
app.UseApiSecurityBeforeErrors();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Veritabanı bağlantı hataları => 503 service_unavailable (tüm yollar, kimlik doğrulama dahil). Hata işleyicisinin içinde, güvenlik hattının dışında.
app.UseMiddleware<DatabaseUnavailableMiddleware>();
app.UseApiSecurity();

// HTTPS yönlendirmesi bilinçli olarak yok: API, Next.js proxy'sinin (D-006) arkasında düz HTTP dinler.

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();
// /api/v1 grubu: Origin + CSRF filtreleri grup düzeyinde, yani bu gruba eklenen her yazma ucu otomatik korunur.
var api = app.MapApiV1();
api.MapCatalogEndpoints();
api.MapAuthEndpoints();
api.MapAdminEndpoints();
api.MapCartEndpoints();
api.MapAddressEndpoints();
api.MapFavoriteEndpoints();
api.MapCheckoutEndpoints();
api.MapOrderEndpoints();

// Geliştirme komutu: sunucuyu başlatmadan demo kataloğu yazar ve çıkar. HTTP ucu değildir (D-030).
if (args.Contains(DemoCatalogSeedCommand.Flag))
{
    return await DemoCatalogSeedCommand.RunAsync(app.Services, app.Environment, Console.Out, Console.Error);
}

// Geliştirme komutu (Faz 8, D-070): İLK Admin'i atar; sunucuyu başlatmaz. E-posta stdin'den iki kez okunur, yankılanmaz, loglanmaz. HTTP ucu DEĞİLDİR.
if (args.Contains(BootstrapAdminCommand.Flag))
{
    return await BootstrapAdminCommand.RunAsync(app.Services, app.Environment, Console.In, Console.Out, Console.Error);
}

// Geliştirme komutu (VT-2, D-092): sabit vitrin hesapları ve örnek veri; sunucuyu başlatmaz. Yalnız Development + loopback veritabanı. HTTP ucu DEĞİLDİR.
if (args.Contains(ShowcaseSeedCommand.Flag))
{
    return await ShowcaseSeedCommand.RunAsync(app.Services, app.Environment, Console.Out, Console.Error);
}

app.Run();
return 0;

// WebApplicationFactory<Program> için.
public partial class Program;

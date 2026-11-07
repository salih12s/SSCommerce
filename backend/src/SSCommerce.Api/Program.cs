using SSCommerce.Api.Addresses;
using SSCommerce.Api.Admin;
using SSCommerce.Api.Auth;
using SSCommerce.Api.Carts;
using SSCommerce.Api.Catalog;
using SSCommerce.Api.Checkout;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Health;
using SSCommerce.Api.OpenApi;
using SSCommerce.Api.Orders;
using SSCommerce.Api.Security;
using SSCommerce.Infrastructure;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Persistence.Seed;

var builder = WebApplication.CreateBuilder(args);

// Seed ve ilk-Admin komutlarında EF SQL günlüğü gürültüsü olmasın (ve komut girdisi/sonucu günlüğe karışmasın).
if (args.Contains(DemoCatalogSeedCommand.Flag) || args.Contains(BootstrapAdminCommand.Flag))
{
    // Taze veritabanında "__EFMigrationsHistory yok" sorgu hatası da beklenen durumdur (bekleyen migration denetimi).
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
}

// RFC 9457 hata modeli: her ProblemDetails'e `code` ve `traceId` eklenir (bkz. ApiProblemDetails).
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblemDetails.Customize);
builder.Services.AddOpenApi(options => options.AddCatalogQueryParameterTypes());
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity();

var app = builder.Build();

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

app.Run();
return 0;

// WebApplicationFactory<Program> için.
public partial class Program;

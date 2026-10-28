using SSCommerce.Api.Health;
using SSCommerce.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Merkezi hata modeli: işlenmeyen istisna => 500, gövdesiz 4xx/5xx => RFC 9457 application/problem+json.
app.UseExceptionHandler();
app.UseStatusCodePages();

// HTTPS yönlendirmesi bilinçli olarak yok: API, Next.js proxy'sinin (D-006) arkasında düz HTTP dinler.

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();

app.Run();

// WebApplicationFactory<Program> için.
public partial class Program;

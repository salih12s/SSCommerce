using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SSCommerce.Api.Health;

public static class HealthEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Canlılık: süreç ayakta mı? Hiçbir bağımlılığa bakmaz.
        app.MapGet("/health/live", () => Results.Json(new { status = "Healthy" }))
            .WithName("HealthLive")
            .ExcludeFromDescription();

        // Hazırlık: "ready" etiketli kontroller (PostgreSQL SELECT 1). Unhealthy => 503.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = WriteReport,
        }).WithName("HealthReady");

        return app;
    }

    private static Task WriteReport(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = (long)e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}

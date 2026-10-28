using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SSCommerce.Api.Tests;

/// <summary>Regression: işlenmeyen istisna Production'da ayrıntı sızdırmadan 500 problem+json döner.</summary>
public class UnhandledExceptionTests
{
    private const string SecretMessage = "gizli-ayrinti Password=hunter2 Host=db.internal";

    [Fact]
    public async Task Unhandled_exception_in_Production_returns_500_problem_json_without_details()
    {
        // Test-only: HealthCheckService istisna fırlatır; /health/ready bunu yakalamadan endpoint'e taşır.
        await using var factory = ApiFactory.Create(
            "Production",
            postgresConnectionString: null,
            services => services.AddSingleton<HealthCheckService, ThrowingHealthCheckService>());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());

        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", body);
        Assert.DoesNotContain("gizli-ayrinti", body);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
    }

    private sealed class ThrowingHealthCheckService : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(SecretMessage);
    }
}

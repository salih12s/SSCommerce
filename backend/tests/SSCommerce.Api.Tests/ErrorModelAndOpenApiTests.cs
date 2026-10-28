using System.Net;
using System.Text.Json;

namespace SSCommerce.Api.Tests;

public class ErrorModelAndOpenApiTests
{
    [Fact]
    public async Task Unknown_route_returns_404_problem_json()
    {
        await using var factory = ApiFactory.Create("Production", postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/bu-route-yok", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task OpenApi_document_is_served_in_Development()
    {
        await using var factory = ApiFactory.Create("Development", postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_not_served_in_Production()
    {
        await using var factory = ApiFactory.Create("Production", postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

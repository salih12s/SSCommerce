using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Security;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>Faz 5 güvenlik/operasyon altyapısı: no-store önekleri, kullanıcı kimliği yardımcısı, bağlantı havuzu ve komut zaman aşımı.</summary>
public class SecurityPlumbingTests
{
    // ---------- no-store önekleri (Faz 6 uçları henüz yok, yanıtlar 404; başlık yine de konmalı) ----------

    [Theory]
    [InlineData("/api/v1/cart")]
    [InlineData("/api/v1/cart/items/5")]
    [InlineData("/api/v1/addresses")]
    [InlineData("/api/v1/addresses/abc")]
    [InlineData("/api/v1/checkout/preview")]
    [InlineData("/api/v1/orders")]
    [InlineData("/api/v1/orders/5/cancel")]
    [InlineData("/api/v1/admin/orders")]
    [InlineData("/api/v1/auth/csrf")]
    [InlineData("/api/v1/admin/me")]
    public async Task Per_user_prefixes_are_served_with_Cache_Control_no_store_even_when_the_endpoint_does_not_exist_yet(string path)
    {
        await using var factory = ApiFactory.Create("Development", postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/api/v1/cartoon")]       // segment sınırı: /cart ile başlayan ama farklı kaynak
    [InlineData("/api/v1/addressbook")]
    [InlineData("/api/v1/checkouts")]
    [InlineData("/api/v1/ordersx")]
    public async Task Look_alike_paths_are_not_swept_into_no_store(string path)
    {
        await using var factory = ApiFactory.Create("Development", postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("no-store", response.Headers.CacheControl?.ToString());
    }

    // ---------- Kullanıcı kimliği yardımcısı ----------

    private static ClaimsPrincipal Principal(string? authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType));

    [Fact]
    public void The_user_id_is_read_from_the_authenticated_NameIdentifier_claim()
    {
        var id = Guid.NewGuid();
        var principal = Principal("Identity.Application", new Claim(ClaimTypes.NameIdentifier, id.ToString()));

        Assert.True(principal.TryGetUserId(out var read));
        Assert.Equal(id, read);
        Assert.Equal(id, principal.GetUserId());
    }

    [Fact]
    public void An_unauthenticated_principal_has_no_user_id_even_if_it_carries_a_claim()
    {
        // authenticationType null => IsAuthenticated false: sahte/çıplak bir kimlik talebi güvenilmez.
        var principal = Principal(authenticationType: null, new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        Assert.False(principal.TryGetUserId(out var id));
        Assert.Equal(Guid.Empty, id);
        Assert.Throws<InvalidOperationException>(() => principal.GetUserId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void A_missing_or_malformed_claim_has_no_user_id(string? value)
    {
        var claims = value is null ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, value) };
        var principal = Principal("Identity.Application", claims);

        Assert.False(principal.TryGetUserId(out _));
        Assert.Throws<InvalidOperationException>(() => principal.GetUserId());
    }

    [Fact]
    public void Other_claims_cannot_stand_in_for_the_user_id()
    {
        var principal = Principal("Identity.Application", new Claim("sub", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, Guid.NewGuid().ToString()));

        Assert.False(principal.TryGetUserId(out _));
    }

    // ---------- Bağlantı havuzu ve zaman aşımı ----------

    private const string FakeConnection = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x";

    private static (string ConnectionString, int? CommandTimeout) Inspect(string connectionString)
    {
        using var factory = ApiFactory.Create("Production", connectionString);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
        var options = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IDbContextOptions>();
        return (db.Database.GetConnectionString()!, options.Extensions.OfType<RelationalOptionsExtension>().Single().CommandTimeout);
    }

    [Fact]
    public void A_connection_string_without_a_pool_size_gets_the_default_cap_of_30_and_the_15_second_command_timeout()
    {
        var (connectionString, commandTimeout) = Inspect(FakeConnection);

        Assert.Contains("Maximum Pool Size=30", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(15, commandTimeout);
    }

    [Theory]
    [InlineData("Maximum Pool Size=7")]
    [InlineData("MaxPoolSize=7")]
    [InlineData("Max Pool Size=7")]
    public void An_explicit_pool_size_in_the_connection_string_is_respected(string setting)
    {
        var (connectionString, _) = Inspect($"{FakeConnection};{setting}");

        Assert.Contains("=7", connectionString);
        Assert.DoesNotContain("Maximum Pool Size=30", connectionString, StringComparison.OrdinalIgnoreCase);
    }
}

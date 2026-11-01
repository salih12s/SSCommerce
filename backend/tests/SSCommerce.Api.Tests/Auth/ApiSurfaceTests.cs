using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Security;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>
/// Mimari kural testi: API yüzeyindeki HER uç, kayıtlı <see cref="EndpointDataSource"/>'tan okunur ve güvenlik kuralları denetlenir.
/// Yeni bir uç eklenip korumasız bırakılırsa (ör. <c>/api/v1</c> grubunun DIŞINA düşen bir <c>MapPost</c>) bu test kırmızı olur.
///   1. POST/PUT/PATCH/DELETE uçları Origin + CSRF filtresi grubunda olmalı (<see cref="RequestProtectionMetadata"/>).
///   2. /api/v1/admin altındaki her uç AdminOnly politikasına bağlı olmalı.
///   3. /api/ altındaki her uç yetki ister, AÇIKÇA anonim bırakılanlar (aşağıdaki liste) hariç. Listeye eklemek bilinçli bir karardır.
///   4. /api/v1/admin altındaki uçlar ile KULLANICIYA ÖZEL uçlar (cart, addresses, checkout, auth/me, auth/logout) hiçbir koşulda
///      AllowAnonymous taşıyamaz; anonim izin listesi bile bunları kurtarmaz.
/// </summary>
[Collection(AuthApiCollection.Name)]
public class ApiSurfaceTests(AuthApiFixture fixture)
{
    private static readonly string[] UnsafeMethods = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>Bilerek anonim uçlar: "YÖNTEM /rota". Public katalog (okuma) + oturum açmadan önce gereken auth uçları.</summary>
    private static readonly HashSet<string> AnonymousAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /api/v1/categories",
        "GET /api/v1/products",
        "GET /api/v1/products/{slug}",
        "GET /api/v1/auth/csrf",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/login",
    };

    /// <summary>Kullanıcıya özel rota önekleri (segment sınırlı): bu uçlar anonim olamaz. Faz 6 uçları şimdiden listede.</summary>
    private static readonly string[] PerUserPrefixes =
    [
        "/api/v1/admin",
        "/api/v1/cart",
        "/api/v1/addresses",
        "/api/v1/checkout",
        "/api/v1/auth/me",
        "/api/v1/auth/logout",
    ];

    private static bool IsPerUser(string route) =>
        PerUserPrefixes.Any(prefix =>
            route.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            route.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> Violations(IEnumerable<Endpoint> endpoints)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            var label = $"{string.Join('|', methods)} {route}";
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            if (methods.Any(m => UnsafeMethods.Contains(m, StringComparer.OrdinalIgnoreCase)) &&
                endpoint.Metadata.GetMetadata<RequestProtectionMetadata>() is null)
            {
                violations.Add($"{label}: yazma ucu Origin+CSRF korumasız (MapApiV1 grubunda değil)");
            }

            if (route.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase) &&
                !authorize.Any(a => a.Policy == AuthPolicies.AdminOnly))
            {
                violations.Add($"{label}: admin ucu AdminOnly politikasına bağlı değil");
            }

            if (IsPerUser(route) && (anonymous || methods.Any(m => AnonymousAllowList.Contains($"{m} {route}"))))
            {
                violations.Add($"{label}: admin/kullanıcıya özel uç AllowAnonymous taşıyamaz (ya da anonim izin listesinde)");
            }

            if (route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && authorize.Count == 0 && !anonymous &&
                !methods.All(m => AnonymousAllowList.Contains($"{m} {route}")))
            {
                violations.Add($"{label}: yetki istemiyor ve anonim izin listesinde de değil");
            }
        }

        return violations;
    }

    [Fact]
    public void Every_write_endpoint_is_csrf_and_origin_protected_every_admin_endpoint_requires_AdminOnly_and_nothing_is_accidentally_anonymous()
    {
        var endpoints = fixture.DevelopmentFactory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.Empty(Violations(endpoints));
        // Anlamlı olması için gerçek uçlar gerçekten görülmeli.
        Assert.Contains(endpoints.OfType<RouteEndpoint>(), e => e.RoutePattern.RawText == "/api/v1/auth/login");
        Assert.Contains(endpoints.OfType<RouteEndpoint>(), e => e.RoutePattern.RawText == "/api/v1/admin/me");
    }

    [Fact]
    public void The_rule_itself_is_not_vacuous_it_flags_an_unprotected_write_endpoint_an_unguarded_admin_endpoint_and_an_accidentally_open_endpoint()
    {
        var unprotectedPost = Build("/api/v1/orders", "POST");
        var unguardedAdmin = Build("/api/v1/admin/users", "GET", new AuthorizeAttribute()); // yetki var ama AdminOnly değil
        var accidentallyOpen = Build("/api/v1/profile", "GET");
        var protectedAdminPost = Build("/api/v1/admin/products", "POST", new AuthorizeAttribute(AuthPolicies.AdminOnly), RequestProtectionMetadata.Instance);

        var violations = Violations([unprotectedPost, unguardedAdmin, accidentallyOpen, protectedAdminPost]);

        Assert.Equal(4, violations.Count); // post: 2 (korumasız + yetkisiz), admin: 1, açık uç: 1; korumalı admin POST: 0
        Assert.Contains(violations, v => v.Contains("/api/v1/orders") && v.Contains("CSRF"));
        Assert.Contains(violations, v => v.Contains("/api/v1/admin/users") && v.Contains("AdminOnly"));
        Assert.Contains(violations, v => v.Contains("/api/v1/profile") && v.Contains("anonim"));
        Assert.DoesNotContain(violations, v => v.Contains("/api/v1/admin/products"));
    }

    [Theory]
    [InlineData("/api/v1/admin/inventory/receive", "POST")]   // AdminOnly olsa bile AllowAnonymous ekli
    [InlineData("/api/v1/admin/warehouses", "GET")]
    [InlineData("/api/v1/cart", "GET")]
    [InlineData("/api/v1/cart/items/5", "PATCH")]
    [InlineData("/api/v1/addresses", "POST")]
    [InlineData("/api/v1/checkout/preview", "POST")]
    [InlineData("/api/v1/auth/me", "GET")]
    [InlineData("/api/v1/auth/logout", "POST")]
    public void An_admin_or_per_user_endpoint_with_AllowAnonymous_turns_the_rule_red(string route, string method)
    {
        var leaky = Build(route, method, new AuthorizeAttribute(AuthPolicies.AdminOnly), new AllowAnonymousAttribute(), RequestProtectionMetadata.Instance);

        var violations = Violations([leaky]);

        Assert.Contains(violations, v => v.Contains(route) && v.Contains("AllowAnonymous"));
    }

    [Fact]
    public void Public_endpoints_and_prefix_look_alikes_are_not_mistaken_for_per_user_endpoints()
    {
        // /cartoon, /administrator gibi segment sınırını aşan rotalar kullanıcıya özel sayılmaz; açık anonim izin listesi hâlâ çalışır.
        var lookAlike = Build("/api/v1/cartoon", "GET", new AllowAnonymousAttribute());
        var publicCatalog = Build("/api/v1/products", "GET", new AllowAnonymousAttribute());
        var login = Build("/api/v1/auth/login", "POST", new AllowAnonymousAttribute(), RequestProtectionMetadata.Instance);

        Assert.DoesNotContain(Violations([publicCatalog, login]), v => v.Contains("AllowAnonymous"));
        Assert.DoesNotContain(Violations([lookAlike]), v => v.Contains("AllowAnonymous"));
    }

    private static RouteEndpoint Build(string pattern, string method, params object[] metadata)
    {
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0);
        builder.Metadata.Add(new HttpMethodMetadata([method]));
        foreach (var item in metadata)
        {
            builder.Metadata.Add(item);
        }

        return (RouteEndpoint)builder.Build();
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Security;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Orders;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// API / güvenlik testi: YÜZEY ENVANTERİ. Rotalar kayıtlı <see cref="EndpointDataSource"/>'tan okunur, yani yarın eklenen her yönetim ucu otomatik olarak bu
/// matrise girer ve korumasızsa test kırılır (elle yazılmış bir liste unutulabilir). Her AdminOnly rota için: anonim 401, Customer 403, Warehouse 403.
/// Her yazma rotası için: CSRF yoksa 400 csrf_invalid, yabancı Origin 403 origin_not_allowed. Yetki denetimi gövde okunmadan ÖNCE çalışır, bu yüzden gövde "{}"dır.
/// </summary>
[Collection(AdminApiCollection.Name)]
public partial class AdminSurfaceTests(InventoryApiFixture fixture)
{
    private static readonly string[] WriteMethods = ["POST", "PUT", "PATCH", "DELETE"];

    [GeneratedRegex(@"\{(?<name>\w+)(:\w+)?\}")]
    private static partial Regex ParameterRegex();

    private sealed record Route(string Method, string Template, string Url, bool IsStaff);

    private IReadOnlyList<Route> Routes()
    {
        var routes = new List<Route>();
        var endpoints = fixture.DevelopmentFactory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var template = (endpoint.RoutePattern.RawText ?? string.Empty).TrimEnd('/');
            if (!template.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToArray();
            var staff = policies.Contains(AuthPolicies.WarehouseOrAdmin);
            var url = ParameterRegex().Replace(template, m => m.Groups["name"].Value == "role" ? "Admin" : Guid.NewGuid().ToString());
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            {
                routes.Add(new Route(method, template, url, staff));
            }
        }

        return routes;
    }

    [Fact]
    public void The_route_inventory_actually_contains_the_phase_8_surface_so_the_matrix_is_not_vacuous()
    {
        var all = Routes().Select(r => $"{r.Method} {r.Template}").ToHashSet();

        foreach (var expected in new[]
                 {
                     "GET /api/v1/admin/categories", "POST /api/v1/admin/categories", "PATCH /api/v1/admin/categories/{id}",
                     "GET /api/v1/admin/products", "GET /api/v1/admin/products/{id}", "POST /api/v1/admin/products", "PATCH /api/v1/admin/products/{id}",
                     "PUT /api/v1/admin/products/{id}/images", "GET /api/v1/admin/catalog/image-assets",
                     "POST /api/v1/admin/products/{id}/variants", "PATCH /api/v1/admin/variants/{id}", "GET /api/v1/admin/variants",
                     "GET /api/v1/admin/dashboard", "GET /api/v1/admin/reports/low-stock", "GET /api/v1/admin/audit",
                     "GET /api/v1/admin/users/{id}", "GET /api/v1/admin/staff",
                     "PUT /api/v1/admin/users/{id}/roles/{role}", "DELETE /api/v1/admin/users/{id}/roles/{role}",
                     "POST /api/v1/admin/orders/{id}/delivery-view", "GET /api/v1/admin/orders", "GET /api/v1/admin/orders/{id}", "POST /api/v1/admin/orders/{id}/status",
                     "POST /api/v1/admin/inventory/receive", "POST /api/v1/admin/inventory/adjust", "GET /api/v1/admin/warehouses",
                 })
        {
            Assert.Contains(expected, all);
        }

        Assert.True(all.Count >= 26);
    }

    [Fact]
    public async Task Anonymous_gets_401_on_every_admin_route()
    {
        var anonymous = fixture.Anonymous();
        var failures = new List<string>();
        foreach (var route in Routes())
        {
            var response = await anonymous.SendAsync(new HttpMethod(route.Method), route.Url, "{}");
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{route.Method} {route.Template} -> {(int)response.StatusCode}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task A_customer_gets_403_on_every_admin_route_staff_order_routes_included()
    {
        var customer = await fixture.CustomerAsync();
        var failures = new List<string>();
        foreach (var route in Routes())
        {
            var response = await customer.SendAsync(new HttpMethod(route.Method), route.Url, "{}");
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{route.Method} {route.Template} -> {(int)response.StatusCode}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task A_warehouse_user_gets_403_on_every_AdminOnly_route_catalog_stock_dashboard_reports_roles_and_delivery_included()
    {
        var warehouse = await fixture.WarehouseAsync();
        var failures = new List<string>();
        var checkedRoutes = 0;
        foreach (var route in Routes().Where(r => !r.IsStaff))
        {
            checkedRoutes++;
            var response = await warehouse.SendAsync(new HttpMethod(route.Method), route.Url, "{}");
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{route.Method} {route.Template} -> {(int)response.StatusCode}");
            }
        }

        Assert.Empty(failures);
        Assert.True(checkedRoutes >= 24);
    }

    [Fact]
    public async Task A_warehouse_user_may_use_the_staff_order_routes_list_detail_and_forward_status()
    {
        var warehouse = await fixture.WarehouseAsync();

        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync("/api/v1/admin/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync("/api/v1/admin/orders?open=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await warehouse.GetAsync($"/api/v1/admin/orders/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await warehouse.StaffStatusAsync(Guid.NewGuid(), "processing")).StatusCode);
    }

    [Fact]
    public async Task Every_admin_write_route_requires_a_csrf_token_and_rejects_a_foreign_origin()
    {
        var admin = await fixture.AdminAsync();
        var failures = new List<string>();
        var writes = Routes().Where(r => WriteMethods.Contains(r.Method)).ToList();
        Assert.True(writes.Count >= 12);
        foreach (var route in writes)
        {
            var method = new HttpMethod(route.Method);
            var noCsrf = await admin.SendAsync(method, route.Url, "{}", sendCsrf: false);
            if (noCsrf.StatusCode != HttpStatusCode.BadRequest || !(await noCsrf.TextAsync()).Contains("csrf_invalid", StringComparison.Ordinal))
            {
                failures.Add($"{route.Method} {route.Template} csrf -> {(int)noCsrf.StatusCode}");
            }

            var foreign = await admin.SendAsync(method, route.Url, "{}", origin: "https://evil.example");
            if (foreign.StatusCode != HttpStatusCode.Forbidden || !(await foreign.TextAsync()).Contains("origin_not_allowed", StringComparison.Ordinal))
            {
                failures.Add($"{route.Method} {route.Template} origin -> {(int)foreign.StatusCode}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Admin_responses_are_never_cacheable_including_401_and_403()
    {
        var admin = await fixture.AdminAsync();
        var customer = await fixture.CustomerAsync();
        var anonymous = fixture.Anonymous();

        foreach (var response in new[]
                 {
                     await admin.GetAsync("/api/v1/admin/categories"),
                     await admin.GetAsync("/api/v1/admin/dashboard"),
                     await customer.GetAsync("/api/v1/admin/dashboard"),
                     await anonymous.GetAsync("/api/v1/admin/dashboard"),
                     await admin.PostAsync($"/api/v1/admin/orders/{Guid.NewGuid()}/delivery-view", null),
                 })
        {
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }
}

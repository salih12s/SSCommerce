using System.Net;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Application.Showcase;

namespace SSCommerce.Api.Tests.Showcase;

/// <summary>
/// Üç sabit vitrin hesabının rolleri yönetim ucundan DEĞİŞTİRİLEMEZ (<c>showcase_account_protected</c>, 409). Güvenlik denetimi bulgusu: paylaşılan demo yöneticisi
/// demo depocudan rol alarak vitrini bozabilir ya da demo müşteriye kalıcı Admin verebilirdi. Koruma Showcase:Enabled'dan BAĞIMSIZDIR (e-postalar zaten ayrılmıştır);
/// başka kullanıcılara rol verme kapatılmadı. Gerçek PostgreSQL, tam HTTP hattı (yetki, CSRF, transaction).
/// </summary>
public class ShowcaseAccountProtectionTests(SeededShowcaseFixture fixture) : IClassFixture<SeededShowcaseFixture>
{
    private static async Task AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = await response.JsonAsync();
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> ChangeRoleAsync(TestSession session, HttpMethod method, Guid targetId, string role)
    {
        var token = await session.FetchCsrfAsync(); // token oturumdaki kullanıcıya bağlıdır
        return await session.SendAsync(method, $"/api/v1/admin/users/{targetId}/roles/{role}", csrf: token);
    }

    private Task<Guid> IdOfAsync(string email) =>
        ShowcaseKit.Sql<Guid>(fixture.Database, $"select \"Id\" from \"AspNetUsers\" where \"Email\" = '{email}'");

    private async Task<string> RolesSnapshotAsync() =>
        string.Join(
            ';',
            await ShowcaseKit.RolesOfAsync(fixture.Database, ShowcaseAccounts.CustomerEmail),
            await ShowcaseKit.RolesOfAsync(fixture.Database, ShowcaseAccounts.AdminEmail),
            await ShowcaseKit.RolesOfAsync(fixture.Database, ShowcaseAccounts.WarehouseEmail));

    private async Task<TestSession> DemoAdminAsync()
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);
        var token = await session.FetchCsrfAsync();
        Assert.Equal(HttpStatusCode.OK, (await session.PostJsonAsync("/api/v1/auth/demo", new { account = "admin" }, token)).StatusCode);
        return session;
    }

    // Satır: (yöntem, hedef e-posta, rol). Hepsi 409; "zaten istenen durumda" olan (admin'e Admin ver, müşteriden Warehouse al) da dahil: koruma no-op'u da kapsar.
    public static TheoryData<string, string, string> ProtectedChanges => new()
    {
        { "PUT", ShowcaseAccounts.CustomerEmail, "Admin" },        // demo müşteriye kalıcı Admin verme
        { "PUT", ShowcaseAccounts.CustomerEmail, "Warehouse" },
        { "PUT", ShowcaseAccounts.WarehouseEmail, "Admin" },
        { "DELETE", ShowcaseAccounts.WarehouseEmail, "Warehouse" }, // demo depocunun rolünü alma (vitrini bozma)
        { "PUT", ShowcaseAccounts.AdminEmail, "Warehouse" },
        { "PUT", ShowcaseAccounts.AdminEmail, "Admin" },            // zaten var
        { "DELETE", ShowcaseAccounts.AdminEmail, "Admin" },         // kendi Admin'ini alma: self-demotion yerine daha özgül kod
        { "DELETE", ShowcaseAccounts.CustomerEmail, "Warehouse" },  // zaten yok
    };

    [Theory]
    [MemberData(nameof(ProtectedChanges))]
    public async Task Negative_a_demo_admin_session_cannot_change_the_roles_of_any_showcase_account(string method, string targetEmail, string role)
    {
        var before = await RolesSnapshotAsync();
        var audits = await ShowcaseKit.Sql<long>(fixture.Database, "select count(*) from admin_audit_log");
        var session = await DemoAdminAsync();

        var response = await ChangeRoleAsync(session, new HttpMethod(method), await IdOfAsync(targetEmail), role);

        await AssertProblem(response, 409, "showcase_account_protected");
        Assert.Equal(before, await RolesSnapshotAsync()); // roller DEĞİŞMEDİ
        Assert.Equal(audits, await ShowcaseKit.Sql<long>(fixture.Database, "select count(*) from admin_audit_log")); // denetim satırı da yazılmadı
        Assert.Equal("Customer;Admin;Warehouse", before);
    }

    [Theory]
    [MemberData(nameof(ProtectedChanges))]
    public async Task Negative_the_protection_holds_for_an_ordinary_admin_and_when_the_showcase_switch_is_off(string method, string targetEmail, string role)
    {
        // Koruma Showcase:Enabled'a bağlı DEĞİL: bayrak kapalıyken (demo uçları yok) sıradan bir Admin de bu hesapları değiştiremez.
        await using var factory = ShowcaseKit.Factory(fixture.Database, enabled: false);
        var (email, password, _) = await AuthApiFixture.CreateUserWithRoleAsync(factory, "Admin");
        var admin = ShowcaseKit.NewSession(factory);
        Assert.Equal(HttpStatusCode.OK, (await admin.LoginAsync(email, password)).StatusCode);
        var before = await RolesSnapshotAsync();

        var response = await ChangeRoleAsync(admin, new HttpMethod(method), await IdOfAsync(targetEmail), role);

        await AssertProblem(response, 409, "showcase_account_protected");
        Assert.Equal(before, await RolesSnapshotAsync());
    }

    [Fact]
    public async Task Edge_other_users_can_still_receive_and_lose_roles_so_the_protection_is_not_over_broad()
    {
        // Bilinen sınır (README): demo yönetici başka kullanıcılara Admin/Warehouse verebilir. Showcase yalnız Development'ta açılabildiği için etkisi yerel DB ile sınırlıdır.
        var victim = ShowcaseKit.NewSession(fixture.Factory);
        using var registered = await (await victim.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword())).JsonAsync();
        var victimId = registered.RootElement.GetProperty("id").GetGuid();
        var admin = await DemoAdminAsync();

        var granted = await ChangeRoleAsync(admin, HttpMethod.Put, victimId, "Warehouse");
        using var grantedJson = await granted.JsonAsync();
        var revoked = await ChangeRoleAsync(admin, HttpMethod.Delete, victimId, "Warehouse");

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        Assert.True(grantedJson.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
    }

    [Fact]
    public async Task Negative_an_unknown_user_is_still_404_and_a_non_admin_demo_session_is_still_403()
    {
        var admin = await DemoAdminAsync();
        await AssertProblem(await ChangeRoleAsync(admin, HttpMethod.Put, Guid.NewGuid(), "Warehouse"), 404, "user_not_found");

        var warehouse = ShowcaseKit.NewSession(fixture.Factory);
        var token = await warehouse.FetchCsrfAsync();
        Assert.Equal(HttpStatusCode.OK, (await warehouse.PostJsonAsync("/api/v1/auth/demo", new { account = "warehouse" }, token)).StatusCode);

        var response = await ChangeRoleAsync(warehouse, HttpMethod.Put, await IdOfAsync(ShowcaseAccounts.CustomerEmail), "Admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // yetki kapısı korumadan ÖNCE: depocu 409 ile hesap bilgisi öğrenemez
    }

    [Fact]
    public async Task Edge_the_role_name_validation_still_runs_before_the_protection()
    {
        var admin = await DemoAdminAsync();

        var response = await ChangeRoleAsync(admin, HttpMethod.Put, await IdOfAsync(ShowcaseAccounts.WarehouseEmail), "admin"); // küçük harf

        await AssertProblem(response, 400, "validation_failed");
    }
}

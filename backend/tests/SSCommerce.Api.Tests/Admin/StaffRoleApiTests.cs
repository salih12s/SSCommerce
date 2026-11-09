using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Api.Tests.Orders;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>Rol testleri kendi izole veritabanında koşar: "tam olarak iki Admin" gibi durumları kurmak için Admin satırlarını silmek gerekir ve bu diğer testleri bozardı.</summary>
[CollectionDefinition(Name)]
public sealed class StaffApiCollection : ICollectionFixture<InventoryApiFixture>
{
    public const string Name = "admin-staff-api";
}

/// <summary>
/// INTEGRATION / API testleri: personel rol yönetimi (D-070). Para ve yetki en riskli alandır; burada özellikle: yarış (iki Admin birbirini aynı anda düşürür),
/// taze SQL yeniden doğrulaması (cookie'deki rol eski olabilir), oturum iptali (rol değişince eski cookie 401) ve "yalnız {role}" denetim kaydı sınanır.
/// </summary>
[Collection(StaffApiCollection.Name)]
public class StaffRoleApiTests(InventoryApiFixture fixture)
{
    private static string RoleUrl(Guid userId, string role) => $"/api/v1/admin/users/{userId}/roles/{role}";

    /// <summary>Veritabanında YALNIZ verilen kullanıcılar Admin kalır (diğer Admin rol satırları silinir). Koleksiyon seri çalıştığı için güvenli.</summary>
    private async Task OnlyAdminsAsync(params Guid[] keep)
    {
        var list = string.Join(",", keep.Select(k => $"'{k}'"));
        await fixture.ExecuteAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"RoleId\" = '{AppRoles.AdminId}' AND \"UserId\" NOT IN ({list})");
    }

    private async Task GrantAdminBySqlAsync(Guid userId) =>
        await fixture.ExecuteAsync($"INSERT INTO \"AspNetUserRoles\" (\"UserId\", \"RoleId\") VALUES ('{userId}', '{AppRoles.AdminId}') ON CONFLICT DO NOTHING");

    private async Task<string> StampOfAsync(Guid userId) =>
        (await fixture.ScalarAsync<string>($"SELECT \"SecurityStamp\" FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'"))!;

    private async Task<long> AdminCountAsync() => await fixture.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"RoleId\" = '{AppRoles.AdminId}'");

    // ---------- Verme / alma ----------

    [Fact]
    public async Task Granting_warehouse_returns_changed_true_audits_only_the_role_and_closes_the_targets_old_sessions()
    {
        var admin = await fixture.AdminAsync();
        var target = await fixture.CustomerAsync();
        var stampBefore = await StampOfAsync(target.UserId);
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/me")).StatusCode);

        var response = await admin.SendAsync(HttpMethod.Put, RoleUrl(target.UserId, "Warehouse"));

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(["Customer", "Warehouse"], json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.NotEqual(stampBefore, await StampOfAsync(target.UserId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/auth/me")).StatusCode); // eski cookie düştü
        var changes = await fixture.ScalarAsync<string>($"SELECT changes::text FROM admin_audit_log WHERE action = 'role.grant' AND target_id = '{target.UserId}'");
        using var audit = JsonDocument.Parse(changes!);
        Assert.Equal(["role"], audit.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Warehouse", audit.RootElement.GetProperty("role").GetString());
        Assert.DoesNotContain(target.Email, changes!, StringComparison.Ordinal);
        Assert.Equal("user", await fixture.ScalarAsync<string>($"SELECT target_type FROM admin_audit_log WHERE action = 'role.grant' AND target_id = '{target.UserId}'"));
        Assert.Equal(admin.UserId, await fixture.ScalarAsync<Guid>($"SELECT actor_user_id FROM admin_audit_log WHERE action = 'role.grant' AND target_id = '{target.UserId}'"));
    }

    [Fact]
    public async Task Granting_a_role_the_user_already_has_is_200_changed_false_with_no_stamp_change_and_no_audit_row()
    {
        var admin = await fixture.AdminAsync();
        var target = await fixture.WarehouseAsync();
        var stamp = await StampOfAsync(target.UserId);
        var auditBefore = await fixture.AuditTotalAsync();

        var response = await admin.SendAsync(HttpMethod.Put, RoleUrl(target.UserId, "Warehouse"));

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(stamp, await StampOfAsync(target.UserId));
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/admin/orders")).StatusCode); // oturumu hâlâ geçerli
    }

    [Fact]
    public async Task Revoking_a_role_removes_it_closes_sessions_and_a_second_revoke_is_a_changed_false_no_op()
    {
        var admin = await fixture.AdminAsync();
        var target = await fixture.WarehouseAsync();

        var first = await admin.SendAsync(HttpMethod.Delete, RoleUrl(target.UserId, "Warehouse"));
        var second = await admin.SendAsync(HttpMethod.Delete, RoleUrl(target.UserId, "Warehouse"));

        using var firstJson = await first.JsonAsync();
        using var secondJson = await second.JsonAsync();
        Assert.True(firstJson.RootElement.GetProperty("changed").GetBoolean());
        Assert.False(secondJson.RootElement.GetProperty("changed").GetBoolean());
        Assert.DoesNotContain("Warehouse", firstJson.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/admin/orders")).StatusCode);
        Assert.Equal(1, await fixture.AuditCountAsync("role.revoke", target.UserId));
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("admin")]
    [InlineData("%20Admin")]
    [InlineData("Admin%20")]
    [InlineData("Customer")]
    [InlineData("warehouse")]
    [InlineData("Superuser")]
    [InlineData("Admin,Warehouse")]
    public async Task Role_names_must_match_exactly_everything_else_is_400_and_changes_nothing(string role)
    {
        var admin = await fixture.AdminAsync();
        var target = await fixture.CustomerAsync();
        var stamp = await StampOfAsync(target.UserId);
        var auditBefore = await fixture.AuditTotalAsync();

        var put = await admin.SendAsync(HttpMethod.Put, RoleUrl(target.UserId, role));
        var delete = await admin.SendAsync(HttpMethod.Delete, RoleUrl(target.UserId, role));

        await ApiAssert.ProblemAsync(put, 400, "validation_failed");
        await ApiAssert.ProblemAsync(delete, 400, "validation_failed");
        Assert.Equal(stamp, await StampOfAsync(target.UserId));
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
    }

    [Fact]
    public async Task An_unknown_user_is_404_user_not_found_and_a_malformed_id_is_400()
    {
        var admin = await fixture.AdminAsync();

        var unknown = await admin.SendAsync(HttpMethod.Put, RoleUrl(Guid.NewGuid(), "Warehouse"));
        var malformed = await admin.SendAsync(HttpMethod.Put, "/api/v1/admin/users/not-a-uuid/roles/Warehouse");
        var getUnknown = await admin.GetAsync($"/api/v1/admin/users/{Guid.NewGuid()}");

        await ApiAssert.ProblemAsync(unknown, 404, "user_not_found");
        await ApiAssert.ProblemAsync(malformed, 400, "validation_failed");
        await ApiAssert.ProblemAsync(getUnknown, 404, "user_not_found");
    }

    // ---------- Kendini düşürme, son Admin ----------

    [Fact]
    public async Task An_admin_cannot_remove_their_own_admin_role()
    {
        var admin = await fixture.AdminAsync();
        var other = await fixture.AdminAsync();
        var stamp = await StampOfAsync(admin.UserId);

        var response = await admin.SendAsync(HttpMethod.Delete, RoleUrl(admin.UserId, "Admin"));

        await ApiAssert.ProblemAsync(response, 409, "role_self_demotion");
        Assert.Equal(stamp, await StampOfAsync(admin.UserId));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/me")).StatusCode);
        _ = other;
    }

    [Fact]
    public async Task The_last_eligible_admin_rule_blocks_removal_when_fewer_than_two_unlocked_admins_exist()
    {
        var a = await fixture.AdminAsync();
        var b = await fixture.AdminAsync();
        await OnlyAdminsAsync(a.UserId, b.UserId);
        await fixture.ExecuteAsync($"UPDATE \"AspNetUsers\" SET \"LockoutEnabled\" = true, \"LockoutEnd\" = now() + interval '1 hour' WHERE \"Id\" = '{b.UserId}'");

        var blocked = await a.SendAsync(HttpMethod.Delete, RoleUrl(b.UserId, "Admin"));

        await ApiAssert.ProblemAsync(blocked, 409, "last_admin");
        Assert.Equal(2, await AdminCountAsync());

        await fixture.ExecuteAsync($"UPDATE \"AspNetUsers\" SET \"LockoutEnd\" = NULL WHERE \"Id\" = '{b.UserId}'");
        var allowed = await a.SendAsync(HttpMethod.Delete, RoleUrl(b.UserId, "Admin"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, await AdminCountAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/v1/admin/me")).StatusCode); // düşürülen Admin'in eski cookie'si
    }

    [Fact]
    public async Task Two_admins_removing_each_other_in_parallel_always_leave_exactly_one_admin()
    {
        for (var round = 0; round < 8; round++)
        {
            var a = await fixture.AdminAsync();
            var b = await fixture.AdminAsync();
            await OnlyAdminsAsync(a.UserId, b.UserId);

            var results = await OrderKit.RunParallelAsync(2, i => i == 0
                ? a.SendAsync(HttpMethod.Delete, RoleUrl(b.UserId, "Admin"))
                : b.SendAsync(HttpMethod.Delete, RoleUrl(a.UserId, "Admin")));

            Assert.Equal(1, await AdminCountAsync());
            Assert.Single(results, r => r.Status == HttpStatusCode.OK);
            var loser = Assert.Single(results, r => r.Status != HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.Forbidden, loser.Status); // kaybeden, kilidi aldığında artık Admin değildir (taze SQL)
            Assert.Equal(1, await fixture.CountAsync($"SELECT count(*) FROM admin_audit_log WHERE action = 'role.revoke' AND actor_user_id IN ('{a.UserId}', '{b.UserId}')"));
        }
    }

    [Fact]
    public async Task A_sole_admin_cannot_remove_their_own_role_and_stays_admin_with_no_stamp_audit_or_session_change()
    {
        var sole = await fixture.AdminAsync();
        await OnlyAdminsAsync(sole.UserId);
        var stamp = await StampOfAsync(sole.UserId);
        var auditBefore = await fixture.AuditTotalAsync();

        var response = await sole.SendAsync(HttpMethod.Delete, RoleUrl(sole.UserId, "Admin"));

        await ApiAssert.ProblemAsync(response, 409, "role_self_demotion");
        Assert.Equal(1, await AdminCountAsync());
        Assert.Equal(stamp, await StampOfAsync(sole.UserId));
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
        Assert.Equal(HttpStatusCode.OK, (await sole.GetAsync("/api/v1/admin/me")).StatusCode);
    }

    [Fact]
    public async Task With_exactly_two_unlocked_admins_one_can_be_removed_and_the_remaining_admin_can_then_not_be_removed_by_anyone()
    {
        var a = await fixture.AdminAsync();
        var b = await fixture.AdminAsync();
        await OnlyAdminsAsync(a.UserId, b.UserId);

        var first = await a.SendAsync(HttpMethod.Delete, RoleUrl(b.UserId, "Admin"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode); // 2 uygun Admin: biri alınabilir
        Assert.Equal(1, await AdminCountAsync());
        // Geriye kalan tek Admin kendini düşüremez (self_demotion); düşürülen Admin'in eski cookie'si de kapanmıştır (401), yani o da başkasını düşüremez.
        await ApiAssert.ProblemAsync(await a.SendAsync(HttpMethod.Delete, RoleUrl(a.UserId, "Admin")), 409, "role_self_demotion");
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.SendAsync(HttpMethod.Delete, RoleUrl(a.UserId, "Admin"))).StatusCode);
        Assert.Equal(1, await AdminCountAsync());
    }

    [Fact]
    public async Task The_last_admin_rule_does_not_block_no_ops_or_the_warehouse_role_when_only_one_admin_exists()
    {
        var sole = await fixture.AdminAsync();
        var warehouse = await fixture.WarehouseAsync();
        await OnlyAdminsAsync(sole.UserId);

        var removeAdminFromNonAdmin = await sole.SendAsync(HttpMethod.Delete, RoleUrl(warehouse.UserId, "Admin")); // hedef zaten Admin değil: değişiklik yok
        var removeWarehouse = await sole.SendAsync(HttpMethod.Delete, RoleUrl(warehouse.UserId, "Warehouse"));

        using var noOp = await removeAdminFromNonAdmin.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, removeAdminFromNonAdmin.StatusCode);
        Assert.False(noOp.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, removeWarehouse.StatusCode);
        Assert.Equal(1, await AdminCountAsync());
    }

    [Fact]
    public async Task Three_admins_removing_each_other_in_a_cycle_at_the_same_time_never_leave_zero_admins()
    {
        for (var round = 0; round < 6; round++)
        {
            var a = await fixture.AdminAsync();
            var b = await fixture.AdminAsync();
            var c = await fixture.AdminAsync();
            await OnlyAdminsAsync(a.UserId, b.UserId, c.UserId);

            // a→b, b→c, c→a. Her istek advisory kilitle sıraya girer ve kilidi aldığında taze SQL ile yeniden karar verir.
            var results = await OrderKit.RunParallelAsync(3, i => i switch
            {
                0 => a.SendAsync(HttpMethod.Delete, RoleUrl(b.UserId, "Admin")),
                1 => b.SendAsync(HttpMethod.Delete, RoleUrl(c.UserId, "Admin")),
                _ => c.SendAsync(HttpMethod.Delete, RoleUrl(a.UserId, "Admin")),
            });

            var remaining = await AdminCountAsync();
            Assert.InRange(remaining, 1, 2); // asla 0; kaç istek başarılıysa o kadar düşmüş olmalı
            Assert.Equal(3 - remaining, results.Count(r => r.Status == HttpStatusCode.OK));
            Assert.All(results, r => Assert.Contains(r.Status, new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Conflict }));
        }
    }

    [Fact]
    public async Task The_actors_admin_role_is_re_checked_with_fresh_sql_so_a_stale_cookie_cannot_change_roles()
    {
        var a = await fixture.AdminAsync();
        var b = await fixture.AdminAsync();
        var target = await fixture.CustomerAsync();
        await OnlyAdminsAsync(a.UserId, b.UserId);
        // Cookie hâlâ geçerli ve Admin talebi taşıyor; ama veritabanında rol yok (örn. başka bir Admin kaldırdı, damga henüz yenilenmedi).
        await fixture.ExecuteAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{a.UserId}' AND \"RoleId\" = '{AppRoles.AdminId}'");
        var auditBefore = await fixture.AuditTotalAsync();

        var response = await a.SendAsync(HttpMethod.Put, RoleUrl(target.UserId, "Warehouse"));

        await ApiAssert.ProblemAsync(response, 403, "forbidden");
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{target.UserId}' AND \"RoleId\" = '{AppRoles.WarehouseId}'"));
    }

    // ---------- Okuma uçları ----------

    [Fact]
    public async Task The_user_lookup_returns_a_safe_summary_by_account_id_and_there_is_no_email_search()
    {
        var admin = await fixture.AdminAsync();
        var customer = await fixture.CustomerAsync();

        var response = await admin.GetAsync($"/api/v1/admin/users/{customer.UserId}");
        var byEmail = await admin.GetAsync($"/api/v1/admin/users/{Uri.EscapeDataString(customer.Email)}");

        var text = await response.TextAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["createdAt", "email", "emailConfirmed", "id", "roles"], json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(customer.Email, json.RootElement.GetProperty("email").GetString());
        Assert.False(json.RootElement.GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal(["Customer"], json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        foreach (var secret in new[] { "passwordHash", "securityStamp", "concurrencyStamp", "phone", "lockout", "accessFailed" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        await ApiAssert.ProblemAsync(byEmail, 400, "validation_failed");
    }

    [Fact]
    public async Task The_staff_list_contains_only_admin_and_warehouse_accounts_with_their_roles()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.WarehouseAsync();
        var customer = await fixture.CustomerAsync();

        using var json = await (await admin.GetAsync("/api/v1/admin/staff")).JsonAsync();

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        var rows = json.RootElement.EnumerateArray().ToList();
        var ids = rows.Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(admin.UserId, ids);
        Assert.Contains(warehouse.UserId, ids);
        Assert.DoesNotContain(customer.UserId, ids);
        Assert.All(rows, r => Assert.Contains(r.GetProperty("roles").EnumerateArray().Select(x => x.GetString()), role => role is "Admin" or "Warehouse"));
        Assert.True(rows.Count <= 200);
        Assert.Equal(["Admin"], rows.Single(r => r.GetProperty("id").GetGuid() == admin.UserId).GetProperty("roles").EnumerateArray().Select(x => x.GetString()));
    }
}

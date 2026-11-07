using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// INTEGRATION testleri: <c>--bootstrap-admin</c> komutu. Süreç başlatmadan, aynı gövde (<see cref="BootstrapAdminCommand.RunAsync"/>) metin okuyucu/yazıcılarla
/// çağrılır (Kestrel zaten hiç başlamaz). HER test kendi izole veritabanında koşar: komutun yazdığı tek yer o veritabanıdır; geliştirme veritabanına dokunulmaz.
/// Güvenlik iddiaları: e-posta hiçbir çıktıda yoktur, bağlantı dizesi hiçbir çıktıda yoktur, hata iletilerinde yalnız istisna TÜR adı olur.
/// </summary>
public class BootstrapAdminTests
{
    private const string Password = "Pw1-bootstrap-test-only";

    private sealed record Run(int ExitCode, string Output, string Error);

    private static Task<T?> Sql<T>(PostgresTestDatabase database, string sql) => database.ScalarAsync<T>(sql, TestContext.Current.CancellationToken);

    private static async Task<Run> RunAsync(WebApplicationFactory<Program> factory, string stdin, IHostEnvironment? environment = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await BootstrapAdminCommand.RunAsync(
            factory.Services,
            environment ?? factory.Services.GetRequiredService<IHostEnvironment>(),
            new StringReader(stdin),
            output,
            error,
            TestContext.Current.CancellationToken);
        return new Run(code, output.ToString(), error.ToString());
    }

    private static async Task<(string Email, Guid Id)> RegisterAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = AuthApiFixture.NewEmail();
        var user = new AppUser { UserName = email, Email = email };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, AppRoles.Customer)).Succeeded);
        return (email, user.Id);
    }

    private static async Task<long> AdminCountAsync(PostgresTestDatabase database) =>
        await Sql<long>(database, $"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"RoleId\" = '{AppRoles.AdminId}'");

    private static WebApplicationFactory<Program> Factory(PostgresTestDatabase database, string environment = "Development") =>
        ApiFactory.Create(environment, database.ConnectionString, extraConfiguration: AuthApiFixture.HighLimits);

    [Fact]
    public async Task The_first_admin_is_assigned_audited_with_the_bootstrap_action_and_the_old_session_is_revoked_and_the_email_is_never_echoed()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = Factory(database);
        var (email, id) = await RegisterAsync(factory);
        var session = new TestSession(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }));
        Assert.True((await session.LoginAsync(email, Password)).IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
        var stamp = await Sql<string>(database, $"SELECT \"SecurityStamp\" FROM \"AspNetUsers\" WHERE \"Id\" = '{id}'");

        var run = await RunAsync(factory, $"{email}\n{email}\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, await AdminCountAsync(database));
        Assert.Equal(1, await Sql<long>(database, $"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{id}' AND \"RoleId\" = '{AppRoles.AdminId}'"));
        Assert.NotEqual(stamp, await Sql<string>(database, $"SELECT \"SecurityStamp\" FROM \"AspNetUsers\" WHERE \"Id\" = '{id}'"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode); // eski oturum kapandı
        Assert.Equal(1, await Sql<long>(database, "SELECT count(*) FROM admin_audit_log"));
        Assert.Equal("role.bootstrap", await Sql<string>(database, "SELECT action FROM admin_audit_log"));
        Assert.Equal(id, await Sql<Guid>(database, "SELECT actor_user_id FROM admin_audit_log"));
        Assert.Equal(id, await Sql<Guid>(database, "SELECT target_id FROM admin_audit_log"));
        Assert.Equal("{\"role\": \"Admin\"}", await Sql<string>(database, "SELECT changes::text FROM admin_audit_log"));
        Assert.DoesNotContain(email, run.Output + run.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, run.Output + run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_development_nothing_is_written_and_the_exit_code_is_1()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = Factory(database, "Production");
        var (email, _) = await RegisterAsync(factory);

        var run = await RunAsync(factory, $"{email}\n{email}\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(0, await AdminCountAsync(database));
        Assert.Equal(0, await Sql<long>(database, "SELECT count(*) FROM admin_audit_log"));
        Assert.DoesNotContain(email, run.Output + run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task When_an_admin_already_exists_the_command_refuses_with_exit_code_1()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = Factory(database);
        var (first, firstId) = await RegisterAsync(factory);
        var (second, secondId) = await RegisterAsync(factory);
        Assert.Equal(0, (await RunAsync(factory, $"{first}\n{first}\n")).ExitCode);

        var run = await RunAsync(factory, $"{second}\n{second}\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(1, await AdminCountAsync(database));
        Assert.Equal(0, await Sql<long>(database, $"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{secondId}' AND \"RoleId\" = '{AppRoles.AdminId}'"));
        Assert.Equal(1, await Sql<long>(database, "SELECT count(*) FROM admin_audit_log"));
        _ = firstId;
    }

    [Fact]
    public async Task Pending_migrations_make_the_command_refuse_with_exit_code_1()
    {
        var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await using (database)
        {
            await using var factory = Factory(database);

            var run = await RunAsync(factory, "kimse@example.test\nkimse@example.test\n");

            Assert.Equal(1, run.ExitCode);
            Assert.Contains("migration", run.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("", "boş giriş")]
    [InlineData("\n\n", "boş satırlar")]
    [InlineData("a@example.test\nb@example.test\n", "uyuşmayan onay")]
    [InlineData("kayitsiz@example.test\nkayitsiz@example.test\n", "kayıtsız hesap")]
    [InlineData("tek-satir@example.test\n", "onay satırı yok")]
    public async Task Empty_mismatching_or_unknown_input_writes_nothing_exits_1_and_never_echoes_the_typed_email(string stdin, string why)
    {
        _ = why;
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = Factory(database);
        await RegisterAsync(factory);

        var run = await RunAsync(factory, stdin);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(0, await AdminCountAsync(database));
        Assert.Equal(0, await Sql<long>(database, "SELECT count(*) FROM admin_audit_log"));
        foreach (var typed in stdin.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.DoesNotContain(typed, run.Output + run.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_matching_email_is_found_regardless_of_case()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = Factory(database);
        var (email, id) = await RegisterAsync(factory);
        var typed = email.ToUpperInvariant();

        var run = await RunAsync(factory, $"{typed}\n{typed}\n");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, await Sql<long>(database, $"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{id}' AND \"RoleId\" = '{AppRoles.AdminId}'"));
    }

    [Fact]
    public async Task A_development_profile_pointing_at_a_remote_database_is_refused_before_any_connection_and_the_connection_string_is_never_printed()
    {
        await using var factory = ApiFactory.Create("Development", "Host=db.prod.example.com;Database=sscommerce;Username=app;Password=SECRET-REMOTE-PW");

        var run = await RunAsync(factory, "kimse@example.test\nkimse@example.test\n");

        Assert.Equal(1, run.ExitCode);
        var all = run.Output + run.Error;
        Assert.DoesNotContain("db.prod.example.com", all, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-REMOTE-PW", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_exits_2_and_prints_only_the_exception_type_name()
    {
        await using var factory = ApiFactory.Create("Development", "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=SECRET-PW-123;Timeout=2");

        var run = await RunAsync(factory, "kimse@example.test\nkimse@example.test\n");

        Assert.Equal(2, run.ExitCode);
        Assert.DoesNotContain("SECRET-PW-123", run.Output + run.Error, StringComparison.Ordinal);
        Assert.Matches("\\(\\w*Exception\\)", run.Error);
    }

    [Fact]
    public async Task Two_concurrent_runs_for_two_accounts_leave_exactly_one_admin_and_one_audit_row()
    {
        for (var round = 0; round < 3; round++)
        {
            await using var database = await PostgresTestDatabase.CreateAsync();
            await using var factory = Factory(database);
            var (first, _) = await RegisterAsync(factory);
            var (second, _) = await RegisterAsync(factory);
            var gate = new TaskCompletionSource();

            var tasks = new[] { first, second }.Select(async email =>
            {
                await gate.Task;
                return await RunAsync(factory, $"{email}\n{email}\n");
            }).ToArray();
            gate.SetResult();
            var runs = await Task.WhenAll(tasks);

            Assert.Equal(1, await AdminCountAsync(database));
            Assert.Equal(1, await Sql<long>(database, "SELECT count(*) FROM admin_audit_log WHERE action = 'role.bootstrap'"));
            Assert.Single(runs, r => r.ExitCode == 0);
            Assert.Single(runs, r => r.ExitCode == 1);
        }
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Database=x", true)]
    [InlineData("Host=localhost;Database=x", true)]
    [InlineData("Host=LOCALHOST;Database=x", true)]
    [InlineData("Host=::1;Database=x", true)]
    [InlineData("Host=127.0.0.1,localhost;Database=x", true)]
    [InlineData("Host=db.example.com;Database=x", false)]
    [InlineData("Host=10.0.0.5;Database=x", false)]
    [InlineData("Host=localhost,db.example.com;Database=x", false)]
    [InlineData("Host=127.0.0.1.evil.example.com;Database=x", false)]
    [InlineData("Host=/var/run/postgresql;Database=x", false)]
    [InlineData("Database=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("%%% not a connection string %%%", false)]
    public void Only_loopback_hosts_count_as_local(string? connectionString, bool expected)
    {
        Assert.Equal(expected, BootstrapAdminCommand.IsLoopbackConnection(connectionString));
    }
}

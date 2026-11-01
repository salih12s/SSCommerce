using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Infrastructure.Persistence;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.E2eHost;

internal static partial class Program
{
    private const string ApiUrl = "http://127.0.0.1:5081";
    private const string TestDatabasePrefix = "sscommerce_test_";

    [GeneratedRegex("^sscommerce_test_[0-9a-f]{32}$")]
    private static partial Regex SafeDatabaseName();

    public static async Task<int> Main()
    {
        var source = Environment.GetEnvironmentVariable("SSCOMMERCE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("SSCOMMERCE_TEST_POSTGRES is required for browser E2E tests.");
            return 1;
        }

        // The source string supplies only server credentials. It never selects the database we write to.
        NpgsqlConnectionStringBuilder admin;
        try
        {
            admin = new NpgsqlConnectionStringBuilder(source) { Database = "postgres" };
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("SSCOMMERCE_TEST_POSTGRES is invalid.");
            return 1;
        }

        var name = TestDatabasePrefix + Guid.NewGuid().ToString("N");
        var created = false;
        Process? api = null;
        Microsoft.AspNetCore.Builder.WebApplication? control = null;
        try
        {
            await EnsurePortAvailableAsync(5081);
            await EnsurePortAvailableAsync(FixtureControl.Port);
            await ExecuteAdminAsync(admin.ConnectionString, $"CREATE DATABASE \"{name}\"");
            created = true;

            var test = new NpgsqlConnectionStringBuilder(source) { Database = name };
            var options = new DbContextOptionsBuilder<SSCommerceDbContext>()
                .UseNpgsql(test.ConnectionString).Options;
            await using (var db = new SSCommerceDbContext(options, TimeProvider.System))
            {
                await db.Database.MigrateAsync();
                await new DemoCatalogSeeder(db).SeedAsync();
            }

            // Test fixture: E2E-DEPO + belirlenimli stok (SKU sırası mod 4) ve loopback kontrol ucu (127.0.0.1:5082).
            await FixtureControl.SeedStockAsync(test.ConnectionString);
            control = FixtureControl.StartControlServer(test.ConnectionString);

            var apiDll = FindApiDll();
            api = StartApi(apiDll, test.ConnectionString);
            await WaitForApiAsync(api);
            Console.WriteLine("E2E_API_READY");
            Console.Out.Flush();

            // Playwright owns stdin. Its global setup cleanup sends "stop"; a closed pipe also stops us.
            // If the API crashes mid-suite, exit and release the database instead of hanging.
            // Console.In.ReadLineAsync is NOT truly asynchronous on the synchronised reader: it blocks the awaiting caller, so Task.WhenAny
            // would never observe the API exit. The blocking ReadLine therefore runs on its own thread-pool task (background; it does not keep
            // the process alive), and Ctrl+C completes a separate TaskCompletionSource.
            var interrupted = new TaskCompletionSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                interrupted.TrySetResult();
            };
            var input = Task.Run(() => Console.In.ReadLine());
            var exited = api.WaitForExitAsync();
            var completed = await Task.WhenAny(input, interrupted.Task, exited);
            if (completed == exited)
            {
                Console.Error.WriteLine("E2E API exited before the test suite completed.");
                return 1;
            }

            return 0; // "stop" line, closed stdin or Ctrl+C
        }
        catch (Exception exception)
        {
            // Npgsql exceptions can include the connection string. Never print the exception message.
            Console.Error.WriteLine($"E2E API setup failed ({exception.GetType().Name}).");
            return 1;
        }
        finally
        {
            if (control is not null)
            {
                try
                {
                    await control.StopAsync();
                    await control.DisposeAsync();
                }
                catch (Exception)
                {
                    // Kapanış hatası iletisi yazdırılmaz.
                }
            }

            if (api is not null)
            {
                try
                {
                    if (!api.HasExited)
                    {
                        api.Kill(entireProcessTree: true);
                    }
                    await api.WaitForExitAsync();
                }
                catch (InvalidOperationException) { }
                api.Dispose();
            }

            if (created)
            {
                try
                {
                    // Destructive SQL is restricted to this process's freshly generated GUID name.
                    if (!SafeDatabaseName().IsMatch(name))
                    {
                        throw new InvalidOperationException("Unsafe E2E database name.");
                    }
                    var test = new NpgsqlConnectionStringBuilder(source) { Database = name };
                    using (var pooled = new NpgsqlConnection(test.ConnectionString))
                    {
                        NpgsqlConnection.ClearPool(pooled);
                    }
                    await ExecuteAdminAsync(admin.ConnectionString,
                        $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
                    Console.WriteLine("E2E_DB_CLEANED");
                    Console.Out.Flush();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"E2E database cleanup failed ({exception.GetType().Name}).");
                    Environment.ExitCode = 1;
                }
            }
        }
    }

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsurePortAvailableAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port);
        }
        catch (SocketException)
        {
            return;
        }
        throw new InvalidOperationException($"E2E port {port} is already in use.");
    }

    private static string FindApiDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend", "SSCommerce.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException("SSCommerce repository root was not found.");
        }

        var path = Path.Combine(directory.FullName, "backend", "src", "SSCommerce.Api", "bin", "Release", "net10.0", "SSCommerce.Api.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Release API assembly was not built.");
        }
        return path;
    }

    private static Process StartApi(string dll, string connectionString)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = ApiUrl;
        start.Environment["ConnectionStrings__Postgres"] = connectionString;
        start.Environment["Security__AllowedOrigins__0"] = "http://localhost:3200";
        start.Environment["Security__AllowedOrigins__1"] = "http://127.0.0.1:3200";
        start.Environment["Security__AssumeHttps"] = "false";
        start.Environment["Security__RateLimit__CredentialsPermitLimit"] =
            Environment.GetEnvironmentVariable("E2E_CREDENTIALS_LIMIT") ?? "1000";
        start.Environment["Security__RateLimit__GeneralPermitLimit"] = "2000";
        // Sepet/adres/stok yazmaları (`mutation`) E2E'de tek IP'den çok sayıda gelir: sınır yalnız bu child process'te yükseltilir.
        start.Environment["Security__RateLimit__MutationPermitLimit"] = "100000";
        start.Environment["Security__RateLimit__WindowSeconds"] = "60";

        var process = Process.Start(start) ?? throw new InvalidOperationException("API process did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForApiAsync(Process api)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (api.HasExited)
            {
                throw new InvalidOperationException("API process exited before readiness.");
            }
            try
            {
                using var response = await http.GetAsync(ApiUrl + "/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("E2E API did not become ready.");
    }
}

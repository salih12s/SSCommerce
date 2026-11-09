using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Showcase;

namespace SSCommerce.Api.Tests.Showcase;

/// <summary>VT-2 test yardımcıları: Showcase açık uygulama, komut çalıştırma, satır sayısı anlık görüntüsü ve invariants.sql eşdeğeri.</summary>
internal static class ShowcaseKit
{
    public sealed record Run(int ExitCode, string Output, string Error);

    /// <summary>Günlük satırlarını bellekte tutar (parola/e-posta sızıntısı taraması için). Seviye Trace: sızıntı varsa görünür.</summary>
    public sealed class MemoryLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
                    : string.Empty;
                lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {fields} {exception}");
            }
        }
    }

    /// <summary>
    /// Development uygulaması. <paramref name="enabled"/>: Showcase:Enabled. <paramref name="withClock"/>: komutun tarihleri geçmişe yaymasını sağlayan
    /// <see cref="ShowcaseClock"/> (Program.cs komut modunda aynı kaydı yapar). Hız sınırları çok yüksektir; test özel sınır verirse <paramref name="extra"/> ezer.
    /// </summary>
    public static WebApplicationFactory<Program> Factory(
        PostgresTestDatabase database,
        bool enabled = true,
        bool withClock = true,
        MemoryLogs? logs = null,
        IReadOnlyDictionary<string, string?>? extra = null,
        string environment = "Development")
    {
        var config = new Dictionary<string, string?>(AuthApiFixture.HighLimits) { ["Showcase:Enabled"] = enabled ? "true" : "false" };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        return ApiFactory.Create(
            environment,
            database.ConnectionString,
            services =>
            {
                if (withClock)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider, ShowcaseClock>();
                }

                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace));
                }
            },
            config);
    }

    public static async Task<Run> SeedAsync(WebApplicationFactory<Program> factory)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await ShowcaseSeedCommand.RunAsync(
            factory.Services,
            factory.Services.GetRequiredService<IHostEnvironment>(),
            output,
            error,
            TestContext.Current.CancellationToken);
        return new Run(code, output.ToString(), error.ToString());
    }

    public static Task<T?> Sql<T>(PostgresTestDatabase database, string sql) => database.ScalarAsync<T>(sql, TestContext.Current.CancellationToken);

    private static readonly string[] CountedTables =
    [
        "\"AspNetUsers\"", "\"AspNetUserRoles\"", "warehouses", "inventory_balances", "stock_movements", "orders", "order_items",
        "order_inventory_allocations", "order_status_history", "customer_addresses", "customer_favorites", "carts", "cart_items",
        "admin_audit_log", "categories", "products", "product_variants", "product_images",
    ];

    /// <summary>Tablo → satır sayısı. İkinci koşu sonrası değişmemesi "çoğaltmadı" kanıtıdır.</summary>
    public static async Task<string> SnapshotAsync(PostgresTestDatabase database)
    {
        var parts = new List<string>();
        foreach (var table in CountedTables)
        {
            parts.Add($"{table}={await Sql<long>(database, $"SELECT count(*) FROM {table}")}");
        }

        // Bakiye toplamı da dahil: stok sessizce değişmesin.
        parts.Add($"onhand={await Sql<long>(database, "SELECT coalesce(sum(on_hand_quantity), 0) FROM inventory_balances")}");
        return string.Join(';', parts);
    }

    /// <summary><c>deploy/db/invariants.sql</c> ile AYNI altı kural (kural adı → SQL). Depo kökünü aramamak için burada kopyalanır; hepsi 0 dönmelidir.</summary>
    public static readonly IReadOnlyDictionary<string, string> Invariants = new Dictionary<string, string>
    {
        ["stok_negatif_degil"] = "select count(*) from inventory_balances where on_hand_quantity < 0",
        ["stok_bakiye_esittir_hareket_toplami"] =
            """
            select count(*) from (
              select b.id from inventory_balances b
              left join (select warehouse_id, product_variant_id, sum(quantity_delta) as total from stock_movements group by 1, 2) m
                on m.warehouse_id = b.warehouse_id and m.product_variant_id = b.product_variant_id
              where b.on_hand_quantity <> coalesce(m.total, 0)
            ) x
            """,
        ["siparis_ara_toplam_esittir_kalemler"] =
            """
            select count(*) from (
              select o.id from orders o
              left join (select order_id, sum(line_subtotal) as total from order_items group by 1) i on i.order_id = o.id
              where o.item_subtotal <> coalesce(i.total, 0)
            ) x
            """,
        ["kalem_tutari_esittir_birim_x_adet"] = "select count(*) from order_items where line_subtotal <> unit_price * quantity",
        ["siparis_tahsis_adedi_esittir_kalem_adedi"] =
            """
            select count(*) from (
              select i.id from order_items i
              left join (select order_item_id, sum(quantity) as q from order_inventory_allocations group by 1) a on a.order_item_id = i.id
              where i.quantity <> coalesce(a.q, 0)
            ) x
            """,
        ["tek_idempotency_anahtari_kullanici_basina"] =
            "select count(*) from (select user_id, idempotency_key from orders group by 1, 2 having count(*) > 1) x",
    };

    /// <summary>İhlal edilen kuralların adları (hepsi sağlanıyorsa boş).</summary>
    public static async Task<IReadOnlyList<string>> ViolatedInvariantsAsync(PostgresTestDatabase database)
    {
        var violated = new List<string>();
        foreach (var (rule, sql) in Invariants)
        {
            if (await Sql<long>(database, sql) != 0)
            {
                violated.Add(rule);
            }
        }

        return violated;
    }

    public static async Task AssertInvariantsHoldAsync(PostgresTestDatabase database) =>
        Assert.Empty(await ViolatedInvariantsAsync(database));

    /// <summary>Tarayıcıyı taklit eden oturum (cookie kavanozu elle; bkz. <see cref="TestSession"/>).</summary>
    internal static TestSession NewSession(WebApplicationFactory<Program> factory) =>
        new(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }));

    /// <summary>
    /// Belirli e-postayla ve verilen rollerle kullanıcıyı HTTP DIŞINDAN kurar (ör. vitrin e-postasını önceden "kapmış" biri).
    /// Parola rastgeledir; gerçek sır değildir.
    /// </summary>
    internal static async Task<(Guid Id, string Password)> CreateUserAsync(WebApplicationFactory<Program> factory, string email, params string[] roles)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var password = AuthApiFixture.NewPassword();
        var user = new AppUser { UserName = email, Email = email };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(',', created.Errors.Select(e => e.Code)));
        foreach (var role in roles)
        {
            var added = await users.AddToRoleAsync(user, role);
            Assert.True(added.Succeeded, string.Join(',', added.Errors.Select(e => e.Code)));
        }

        return (user.Id, password);
    }

    /// <summary>E-postaya sahip kullanıcının rolleri, "A,B" biçiminde sıralı (kullanıcı yoksa null).</summary>
    public static Task<string?> RolesOfAsync(PostgresTestDatabase database, string email) => Sql<string>(
        database,
        $"""
        select string_agg(r."Name", ',' order by r."Name") from "AspNetUsers" u
        join "AspNetUserRoles" ur on ur."UserId" = u."Id" join "AspNetRoles" r on r."Id" = ur."RoleId"
        where u."Email" = '{email}'
        """);
}

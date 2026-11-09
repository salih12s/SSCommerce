using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Application.Inventory;
using SSCommerce.Application.Showcase;
using SSCommerce.Domain.Inventory;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Showcase;

/// <summary>
/// <c>--seed-showcase</c> komutu (VT-2, D-092). Integration testleri, gerçek PostgreSQL; komutun gövdesi (<c>ShowcaseSeedCommand.RunAsync</c>) çıkış koduyla birlikte çağrılır.
/// Bu sınıf iki kalıp kullanır: (1) <see cref="SeededShowcaseFixture"/> = bir kez iki koşu, okuma-yalnız testler; (2) veriyi değiştiren / reddi sınayan testler KENDİ
/// izole veritabanını açar. İkinci koşu, ilk koşunun verisini paylaşır.
/// </summary>
public class ShowcaseSeedCommandTests(SeededShowcaseFixture fixture) : IClassFixture<SeededShowcaseFixture>
{
    private static readonly string[] ForbiddenInOutput = ["@", "demo-", "sscommerce.example", "Password", "Host=", "Username", "Pw1"];

    private static Task<long> Count(PostgresTestDatabase db, string sql) => ShowcaseKit.Sql<long>(db, sql);

    private const string CustomerId = "(select \"Id\" from \"AspNetUsers\" where \"Email\" = '" + ShowcaseAccounts.CustomerEmail + "')";

    // ===================== Ilk koşu: veri şekli (Integration) =====================

    [Fact]
    public void Integration_first_run_exits_zero_with_a_summary_and_no_error()
    {
        Assert.Equal(0, fixture.FirstRun.ExitCode);
        Assert.Equal(string.Empty, fixture.FirstRun.Error);
        Assert.StartsWith("Vitrin verisi tamam.", fixture.FirstRun.Output);
        Assert.DoesNotContain("zaten güncel", fixture.FirstRun.Output);
        Assert.Contains("5 sipariş", fixture.FirstRun.Output);
    }

    [Theory]
    [InlineData(ShowcaseAccounts.CustomerEmail, "Customer")]
    [InlineData(ShowcaseAccounts.AdminEmail, "Admin")]
    [InlineData(ShowcaseAccounts.WarehouseEmail, "Warehouse")]
    public async Task Integration_each_showcase_account_has_exactly_its_one_role(string email, string role)
    {
        Assert.Equal(role, await ShowcaseKit.RolesOfAsync(fixture.Database, email));
    }

    [Fact]
    public async Task Integration_only_the_three_showcase_accounts_exist()
    {
        Assert.Equal(3, await Count(fixture.Database, "select count(*) from \"AspNetUsers\""));
    }

    [Fact]
    public async Task Integration_demo_customer_has_five_orders_one_per_status()
    {
        var statuses = await ShowcaseKit.Sql<string>(
            fixture.Database,
            $"select string_agg(status, ',' order by status) from orders where user_id = {CustomerId}");

        Assert.Equal("cancelled,completed,placed,processing,shipped", statuses);
        Assert.Equal(5, await Count(fixture.Database, "select count(*) from orders")); // başka kimsenin siparişi yok
    }

    [Fact]
    public async Task Integration_demo_customer_has_two_addresses_six_favorites_and_two_cart_lines()
    {
        Assert.Equal(2, await Count(fixture.Database, $"select count(*) from customer_addresses where user_id = {CustomerId}"));
        Assert.Equal(6, await Count(fixture.Database, $"select count(*) from customer_favorites where user_id = {CustomerId}"));
        Assert.Equal(
            2,
            await Count(fixture.Database, $"select count(*) from cart_items i join carts c on c.id = i.cart_id where c.user_id = {CustomerId}"));
    }

    [Fact]
    public async Task Integration_every_order_item_is_covered_by_a_real_stock_allocation()
    {
        // Sipariş GERÇEK servisten geçti: her kalemin tahsisi ve yerleştirme hareketi var (invariants da bunu ister; burada doğrudan).
        Assert.Equal(
            0,
            await Count(
                fixture.Database,
                """
                select count(*) from order_items i
                where not exists (select 1 from order_inventory_allocations a join stock_movements m on m.id = a.placement_movement_id where a.order_item_id = i.id)
                """));
        Assert.True(await Count(fixture.Database, "select count(*) from warehouses") >= 1);
    }

    [Fact]
    public async Task Negative_no_showcase_account_can_be_locked_out_and_none_is_locked_after_the_seed()
    {
        // Kilitleme DoS savunması: sabit e-postalara 5 yanlış parola göndermek demo girişi kapatamasın diye LockoutEnabled kapalıdır.
        Assert.Equal(3, await Count(fixture.Database, "select count(*) from \"AspNetUsers\" where \"LockoutEnabled\" = false"));
        Assert.Equal(0, await Count(fixture.Database, "select count(*) from \"AspNetUsers\" where \"LockoutEnd\" is not null or \"AccessFailedCount\" <> 0"));
    }

    [Fact]
    public void Negative_every_run_rotates_the_unknown_password_and_the_security_stamp_of_existing_accounts()
    {
        // Sertleştirme: ikinci koşu satır eklemez (snapshot eşit), ama parola özeti ve stamp YENİ olur.
        Assert.NotEqual(string.Empty, fixture.CredentialAfterFirst);
        Assert.NotEqual(fixture.CredentialAfterFirst.Split('|')[0], fixture.CredentialAfterSecond.Split('|')[0]); // parola
        Assert.NotEqual(fixture.CredentialAfterFirst.Split('|')[1], fixture.CredentialAfterSecond.Split('|')[1]); // stamp
    }

    // ===================== Tarihler (Edge: geçmişe yayma) =====================

    [Theory]
    [InlineData("completed", 12 * 24.0)]
    [InlineData("cancelled", 6 * 24.0)]
    [InlineData("shipped", 4 * 24.0)]
    [InlineData("processing", 30.0)]
    [InlineData("placed", 3.0)]
    public async Task Edge_each_order_is_written_at_its_own_past_moment_not_at_the_real_clock(string suffix, double hoursAgo)
    {
        var createdAt = await ShowcaseKit.Sql<DateTime>(
            fixture.Database, $"select created_at at time zone 'UTC' from orders where idempotency_key = 'showcase-order-{suffix}'");
        var created = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
        var ago = TimeSpan.FromHours(hoursAgo);

        // Çapa = ilk koşunun başlangıcı; tam an [başlangıç - ago, bitiş - ago] aralığındadır.
        Assert.InRange(
            created,
            fixture.FirstRunStartedAt.UtcDateTime - ago - TimeSpan.FromSeconds(1),
            fixture.FirstRunFinishedAt.UtcDateTime - ago + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Edge_no_timestamp_the_command_wrote_is_in_the_future()
    {
        var limit = fixture.FirstRunFinishedAt.UtcDateTime.AddMinutes(1).ToString("O");
        foreach (var column in new[]
        {
            "orders.created_at", "orders.updated_at", "order_status_history.occurred_at", "stock_movements.occurred_at", "customer_favorites.created_at",
            "customer_addresses.created_at", "carts.created_at", "cart_items.created_at", "\"AspNetUsers\".\"CreatedAt\"",
        })
        {
            var table = column[..column.LastIndexOf('.')];
            var future = await Count(fixture.Database, $"select count(*) from {table} where {column[(column.LastIndexOf('.') + 1)..]} > '{limit}'::timestamptz");
            Assert.True(future == 0, $"{column}: gelecekte {future} satır var.");
        }
    }

    [Fact]
    public async Task Edge_order_status_history_is_strictly_chronological_and_follows_the_state_machine()
    {
        var expected = new Dictionary<string, string>
        {
            ["completed"] = "placed>processing>shipped>completed",
            ["cancelled"] = "placed>cancelled",
            ["shipped"] = "placed>processing>shipped",
            ["processing"] = "placed>processing",
            ["placed"] = "placed",
        };

        foreach (var (suffix, path) in expected)
        {
            var actual = await ShowcaseKit.Sql<string>(
                fixture.Database,
                $"""
                select string_agg(h.to_status, '>' order by h.occurred_at) from order_status_history h
                join orders o on o.id = h.order_id where o.idempotency_key = 'showcase-order-{suffix}'
                """);
            Assert.Equal(path, actual);
        }

        // Her siparişte: satır zamanları kesin artan ve ilk satır siparişin verildiği andır (geçmişi geriye doğru yazılmış satır yok).
        Assert.Equal(
            0,
            await Count(
                fixture.Database,
                """
                select count(*) from (
                  select h.occurred_at, lag(h.occurred_at) over (partition by h.order_id order by h.occurred_at, h.id) as previous
                  from order_status_history h
                ) x where previous is not null and occurred_at <= previous
                """));
        Assert.Equal(
            0,
            await Count(
                fixture.Database,
                "select count(*) from orders o where (select min(h.occurred_at) from order_status_history h where h.order_id = o.id) < o.created_at"));
    }

    [Fact]
    public async Task Edge_orders_are_ordered_oldest_to_newest_as_the_recipe_says()
    {
        var order = await ShowcaseKit.Sql<string>(
            fixture.Database,
            "select string_agg(replace(idempotency_key, 'showcase-order-', ''), ',' order by created_at) from orders");

        Assert.Equal("completed,cancelled,shipped,processing,placed", order);
    }

    [Fact]
    public void Edge_the_pinned_clock_is_released_after_the_command()
    {
        // Regression: komut bittikten sonra saat gerçek saate dönmezse sunucu geçmişte yaşar (finally { Release() }).
        var clock = fixture.Factory.Services.GetRequiredService<TimeProvider>();

        Assert.IsType<SSCommerce.Infrastructure.Showcase.ShowcaseClock>(clock);
        Assert.InRange(clock.GetUtcNow(), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    // ===================== Idempotency (Integration) =====================

    [Fact]
    public void Integration_second_run_exits_zero_and_reports_nothing_new()
    {
        Assert.Equal(0, fixture.SecondRun.ExitCode);
        Assert.Equal(string.Empty, fixture.SecondRun.Error);
        Assert.Contains("zaten güncel; değişiklik yok", fixture.SecondRun.Output);
        Assert.Contains("Vitrin müşterisi toplamı: 5 sipariş, 6 favori, 2 adres", fixture.SecondRun.Output);
    }

    [Fact]
    public void Integration_second_run_leaves_every_table_and_the_stock_total_byte_for_byte_the_same()
    {
        Assert.NotEqual(string.Empty, fixture.SnapshotAfterFirst);
        Assert.Equal(fixture.SnapshotAfterFirst, fixture.SnapshotAfterSecond);
    }

    [Fact]
    public void Integration_invariants_hold_after_the_first_and_the_second_run()
    {
        Assert.Empty(fixture.ViolatedAfterFirst);
        Assert.Empty(fixture.ViolatedAfterSecond);
    }

    // ===================== Sızıntı (Negative) =====================

    [Fact]
    public void Negative_command_output_contains_no_password_email_or_connection_data()
    {
        foreach (var text in new[] { fixture.FirstRun.Output, fixture.FirstRun.Error, fixture.SecondRun.Output, fixture.SecondRun.Error })
        {
            foreach (var forbidden in ForbiddenInOutput)
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Negative_logs_written_during_the_seed_do_not_contain_emails_hashes_or_the_database_password()
    {
        var lines = fixture.Logs.Lines.ToArray();
        Assert.NotEmpty(lines); // pozitif kontrol: günlük gerçekten toplandı (boş liste "sızıntı yok" gibi görünmesin)

        var hash = await ShowcaseKit.Sql<string>(fixture.Database, $"select \"PasswordHash\" from \"AspNetUsers\" where \"Email\" = '{ShowcaseAccounts.CustomerEmail}'");
        var dbPassword = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString).Password;
        Assert.False(string.IsNullOrEmpty(hash));

        var joined = string.Join('\n', lines);
        Assert.DoesNotContain(ShowcaseAccounts.CustomerEmail, joined);
        Assert.DoesNotContain(ShowcaseAccounts.AdminEmail, joined);
        Assert.DoesNotContain(ShowcaseAccounts.WarehouseEmail, joined);
        Assert.DoesNotContain(hash!, joined);
        if (!string.IsNullOrEmpty(dbPassword))
        {
            Assert.DoesNotContain(dbPassword, joined);
        }
    }

    // ===================== Reddedilen koşullar: KENDİ veritabanı (Negative) =====================

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Negative_command_refuses_outside_development_and_writes_nothing(string environment)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, enabled: false, environment: environment);
        var before = await ShowcaseKit.SnapshotAsync(database);

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(environment, run.Error);
        Assert.Contains("Hiçbir şey yazılmadı", run.Error);
        Assert.Equal(string.Empty, run.Output);
        Assert.Equal(before, await ShowcaseKit.SnapshotAsync(database));
        Assert.Equal(0, await Count(database, "select count(*) from \"AspNetUsers\""));
    }

    [Fact]
    public async Task Negative_command_refuses_a_non_loopback_database_host_without_echoing_it()
    {
        // Veritabanına hiç ulaşılmaz: ret, bağlantı kurulmadan önce verilir. Sunucu adı hata iletisinde GÖRÜNMEZ.
        await using var factory = ApiFactory.Create(
            "Development",
            "Host=uretim-db.example.test;Port=5432;Database=sscommerce;Username=app;Password=hunter2-gizli;Timeout=2",
            services => services.AddSingleton<TimeProvider, SSCommerce.Infrastructure.Showcase.ShowcaseClock>());

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("loopback", run.Error);
        Assert.DoesNotContain("uretim-db", run.Error);
        Assert.DoesNotContain("hunter2", run.Error);
        Assert.Equal(string.Empty, run.Output);
    }

    [Fact]
    public async Task Negative_an_unreachable_database_exits_two_naming_only_the_exception_type()
    {
        // Loopback ama kapalı port: bekleyen-migration sorgusu patlar. Npgsql iletisi sunucu/kullanıcı taşıyabilir; yalnız TÜR adı yazılmalı.
        await using var factory = ApiFactory.Create(
            "Development",
            "Host=127.0.0.1;Port=1;Database=sscommerce;Username=app;Password=hunter2-gizli;Timeout=2;Pooling=false");

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(2, run.ExitCode);
        // Ileti SABİT biçimdedir: yalnız istisna TÜR adı (kelime), sunucu/kullanıcı/parola yok.
        Assert.Matches(@"^HATA: vitrin verisi yazılamadı \(\w+\)\. Veritabanı çalışıyor mu ve ConnectionStrings__Postgres tanımlı mı\?\s*$", run.Error);
        Assert.Equal(string.Empty, run.Output);
    }

    [Fact]
    public async Task Negative_pending_migrations_make_the_command_exit_one_without_applying_them()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await using var factory = ShowcaseKit.Factory(database, enabled: false);

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("bekleyen migration", run.Error);
        Assert.Null(await ShowcaseKit.Sql<string>(database, "select to_regclass('\"AspNetUsers\"')::text")); // şema UYGULANMADI
    }

    [Theory]
    [InlineData(ShowcaseAccounts.AdminEmail, "Customer", "Admin")]
    [InlineData(ShowcaseAccounts.WarehouseEmail, "Customer", "Warehouse")]
    [InlineData(ShowcaseAccounts.CustomerEmail, "Admin", "Customer")]
    public async Task Negative_a_showcase_email_registered_with_another_role_stops_the_command_and_grants_no_role(
        string squattedEmail, string existingRole, string roleItWouldHaveGained)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, enabled: false);
        await ShowcaseKit.CreateUserAsync(factory, squattedEmail, existingRole);

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("beklenen rolde değil", run.Error);
        foreach (var leak in new[] { "@", "demo-", "example" })
        {
            Assert.DoesNotContain(leak, run.Error);
        }

        // Kapılan hesabın rolü AYNEN kaldı; hedef rol (yetki) hiçbir kullanıcıya verilmedi.
        Assert.Equal(existingRole, await ShowcaseKit.RolesOfAsync(database, squattedEmail));
        var roleMembers = await Count(
            database,
            $"""
            select count(*) from "AspNetUserRoles" ur join "AspNetRoles" r on r."Id" = ur."RoleId"
            join "AspNetUsers" u on u."Id" = ur."UserId" where r."Name" = '{roleItWouldHaveGained}' and u."Email" = '{squattedEmail}'
            """);
        Assert.Equal(0, roleMembers);
        Assert.Equal(0, await Count(database, "select count(*) from orders")); // sipariş aşamasına hiç gelinmedi
    }

    [Fact]
    public async Task Negative_a_visitor_who_registered_the_customer_showcase_email_first_loses_the_password_and_the_open_session()
    {
        // Squatting: biri vitrin müşteri e-postasını kendi parolasıyla kaydeder. Seed hesabı (Customer rolünde) yeniden kullanır; eski sahibin
        // parolası ve oturumu geçersiz KALMALIDIR, yoksa saldırgan "demo" hesaba parolayla girer.
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, enabled: false);
        var password = Auth.AuthApiFixture.NewPassword();
        var squatter = ShowcaseKit.NewSession(factory);
        Assert.Equal(System.Net.HttpStatusCode.Created, (await squatter.RegisterAsync(ShowcaseAccounts.CustomerEmail, password)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await squatter.GetAsync("/api/v1/auth/me")).StatusCode);

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await squatter.GetAsync("/api/v1/auth/me")).StatusCode); // açık oturum düştü
        var attacker = ShowcaseKit.NewSession(factory);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await attacker.LoginAsync(ShowcaseAccounts.CustomerEmail, password)).StatusCode); // eski parola işlemez
        Assert.Equal("Customer", await ShowcaseKit.RolesOfAsync(database, ShowcaseAccounts.CustomerEmail));
        Assert.Equal(0, await Count(database, $"select count(*) from \"AspNetUsers\" where \"Email\" = '{ShowcaseAccounts.CustomerEmail}' and \"LockoutEnabled\""));
        await ShowcaseKit.AssertInvariantsHoldAsync(database);
    }

    [Fact]
    public async Task Edge_an_existing_locked_showcase_account_from_an_older_seed_is_unlocked_by_the_next_run()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database);
        Assert.Equal(0, (await ShowcaseKit.SeedAsync(factory)).ExitCode);
        await database.ExecuteAsync(
            "update \"AspNetUsers\" set \"LockoutEnabled\" = true, \"LockoutEnd\" = now() + interval '1 hour', \"AccessFailedCount\" = 3",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, (await ShowcaseKit.SeedAsync(factory)).ExitCode);

        Assert.Equal(3, await Count(database, "select count(*) from \"AspNetUsers\" where not \"LockoutEnabled\" and \"LockoutEnd\" is null and \"AccessFailedCount\" = 0"));
    }

    // ===================== Yarım kalmış durumu tamamlama (Edge) =====================

    [Fact]
    public async Task Edge_a_partially_wiped_showcase_is_completed_without_duplicating_anything()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database);
        Assert.Equal(0, (await ShowcaseKit.SeedAsync(factory)).ExitCode);
        var complete = await ShowcaseKit.SnapshotAsync(database);

        // Ziyaretçi sepeti boşalttı ve bir favoriyi kaldırdı.
        await database.ExecuteAsync(
            $"delete from cart_items where cart_id in (select id from carts where user_id = {CustomerId})", TestContext.Current.CancellationToken);
        await database.ExecuteAsync(
            $"delete from customer_favorites where ctid in (select ctid from customer_favorites where user_id = {CustomerId} limit 1)",
            TestContext.Current.CancellationToken);
        Assert.NotEqual(complete, await ShowcaseKit.SnapshotAsync(database));

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("1 favori", run.Output);
        Assert.Contains("2 sepet satırı", run.Output);
        Assert.Contains("0 sipariş,", run.Output); // siparişler çoğaltılmadı
        Assert.Equal(complete, await ShowcaseKit.SnapshotAsync(database));
        await ShowcaseKit.AssertInvariantsHoldAsync(database);
    }

    [Fact]
    public async Task Regression_edge_a_variant_already_at_or_near_the_stock_limit_is_skipped_not_fatal()
    {
        // E2E'de bulunan hata: bir spec bazı varyantların bakiyesini MaxOnHandQuantity'ye çıkarınca seed her varyana +50 girmeye çalışıp
        // StockLimitExceeded ile TÜM komutu durduruyordu (exit 1). "Stok garantisi" zaten dolu varyantı atlamalıdır.
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database);
        var ct = TestContext.Current.CancellationToken;
        await using (var context = database.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(ct);
        }

        var (adminId, _) = await ShowcaseKit.CreateUserAsync(factory, Auth.AuthApiFixture.NewEmail(), "Admin");
        Guid warehouseId;
        using (var scope = factory.Services.CreateScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            var created = await inventory.CreateWarehouseAsync(adminId, new NewWarehouse("ANA", "Ana Depo"), ct);
            Assert.Equal(WarehouseWriteStatus.Created, created.Status);
            warehouseId = created.Warehouse!.Id;

            async Task FillAsync(string sku, int quantity)
            {
                var variantId = await ShowcaseKit.Sql<Guid>(database, $"select id from product_variants where sku = '{sku}'");
                var result = await inventory.ApplyStockOperationAsync(
                    new StockCommand(StockOperations.Receive, warehouseId, variantId, quantity, StockOperationType.Receiving, "test dolumu", null, $"test-fill-{Guid.NewGuid():N}", adminId),
                    ct);
                Assert.Equal(StockOperationStatus.Applied, result.Status);
            }

            await FillAsync("SSC-3001-SEY", InventoryRules.MaxOnHandQuantity); // tam sınırda, hiçbir siparişte yok
            await FillAsync("SSC-3002-S", InventoryRules.MaxOnHandQuantity - 10); // sınırın 10 altı: +50 sığmaz
            await FillAsync("SSC-3001-STD", InventoryRules.MaxOnHandQuantity); // tam sınırda ve tamamlanmış siparişte (1 adet düşer)
        }

        async Task<Guid> IdOf(string sku) => await ShowcaseKit.Sql<Guid>(database, $"select id from product_variants where sku = '{sku}'");
        async Task<long> OnHand(Guid variantId) =>
            await ShowcaseKit.Sql<long>(database, $"select on_hand_quantity from inventory_balances where product_variant_id = '{variantId}'");
        async Task<long> SeedReceipts(Guid variantId) =>
            await Count(database, $"select count(*) from stock_movements where product_variant_id = '{variantId}' and operation_key like 'showcase-receive-%'");

        var full = await IdOf("SSC-3001-SEY");
        var near = await IdOf("SSC-3002-S");
        var ordered = await IdOf("SSC-3001-STD");
        var normal = await IdOf("SSC-4001-1");

        var first = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(string.Empty, first.Error);
        Assert.Equal(0, first.ExitCode);
        // Dolu varyantlar: bakiye değişmedi ve seed için hareket YAZILMADI.
        Assert.Equal(InventoryRules.MaxOnHandQuantity, await OnHand(full));
        Assert.Equal(InventoryRules.MaxOnHandQuantity - 10, await OnHand(near));
        Assert.Equal(InventoryRules.MaxOnHandQuantity - 1, await OnHand(ordered)); // yalnız siparişin 1 adedi düştü
        Assert.Equal(0, await SeedReceipts(full) + await SeedReceipts(near) + await SeedReceipts(ordered));
        // Sıradan varyant normal şekilde dolduruldu (+50) ve bunun için hareket var.
        Assert.Equal(50, await OnHand(normal));
        Assert.Equal(1, await SeedReceipts(normal));
        Assert.Equal(5, await Count(database, "select count(*) from orders"));
        await ShowcaseKit.AssertInvariantsHoldAsync(database);

        // İkinci koşu: hâlâ 0 ve hiçbir şey yeni değil, hiçbir tablo değişmedi.
        var afterFirst = await ShowcaseKit.SnapshotAsync(database);
        var second = await ShowcaseKit.SeedAsync(factory);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("zaten güncel; değişiklik yok", second.Output);
        Assert.Equal(afterFirst, await ShowcaseKit.SnapshotAsync(database));
        await ShowcaseKit.AssertInvariantsHoldAsync(database);
    }

    [Fact]
    public async Task Edge_with_the_real_clock_the_command_behaves_the_same_but_writes_current_dates()
    {
        // ShowcaseClock kayıtlı DEĞİL (withClock: false): tohumlayıcı saati sabitleyemez; davranış aynı, tarihler "şimdi".
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, withClock: false);
        var started = DateTimeOffset.UtcNow;

        var run = await ShowcaseKit.SeedAsync(factory);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(5, await Count(database, "select count(*) from orders"));
        var oldest = await ShowcaseKit.Sql<DateTime>(database, "select min(created_at) at time zone 'UTC' from orders");
        Assert.True(DateTime.SpecifyKind(oldest, DateTimeKind.Utc) >= started.UtcDateTime.AddSeconds(-5), "Sabit saat yokken siparişler geçmişe yazılmamalı.");
        await ShowcaseKit.AssertInvariantsHoldAsync(database);
    }
}

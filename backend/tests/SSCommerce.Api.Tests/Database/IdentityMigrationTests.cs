using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 4.1: Identity kalıcılığı ve ayrı <c>AddIdentity</c> migration'ı (D-032). Zincir InitialCatalog → AddIdentity sırayla uygulanır ve
/// Catalog verisini bozmaz. Şemayı değiştirdikleri için her test KENDİ izole veritabanını açar. Kullanıcı/parola oluşturulmaz:
/// kullanıcı satırları yalnızca kısıt testleri için ham SQL ile eklenir (parola hash'i yok).
/// </summary>
public class IdentityMigrationTests
{
    private const string CatalogMigration = "InitialCatalog";
    private static readonly string[] IdentityTables =
        ["AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUsers", "AspNetUserTokens"];

    private static async Task<long> TableCountAsync(PostgresTestDatabase database, params string[] tables)
    {
        var list = string.Join(", ", tables.Select(t => $"'{t}'"));
        return await database.ScalarAsync<long>(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ({list})",
            TestContext.Current.CancellationToken);
    }

    private static async Task<(long Categories, long Products, long Variants, long Images)> CatalogCountsAsync(PostgresTestDatabase database)
    {
        var ct = TestContext.Current.CancellationToken;
        return (
            await database.ScalarAsync<long>("SELECT count(*) FROM categories", ct),
            await database.ScalarAsync<long>("SELECT count(*) FROM products", ct),
            await database.ScalarAsync<long>("SELECT count(*) FROM product_variants", ct),
            await database.ScalarAsync<long>("SELECT count(*) FROM product_images", ct));
    }

    private static async Task MigrateToAsync(PostgresTestDatabase database, string? targetMigration)
    {
        await using var context = database.CreateDbContext();
        await context.GetService<IMigrator>().MigrateAsync(targetMigration, TestContext.Current.CancellationToken);
    }

    private static async Task SeedCatalogAsync(PostgresTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        await new DemoCatalogSeeder(context).SeedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Migrations_apply_in_order_catalog_first_then_identity_and_a_second_migrate_is_a_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);

        await MigrateToAsync(database, CatalogMigration);
        Assert.Equal(0, await TableCountAsync(database, IdentityTables));
        Assert.Equal(4, await TableCountAsync(database, "categories", "products", "product_variants", "product_images"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));

        await using (var context = database.CreateDbContext())
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToArray();
            Assert.Equal(4, pending.Length); // AddIdentity, ardından Faz 5'in AddInventory'si, Faz 6'nın AddCartAndAddresses'i ve Faz 7'nin AddOrdering'i
            Assert.EndsWith("_AddIdentity", pending[0]);
            Assert.EndsWith("_AddInventory", pending[1]);
            Assert.EndsWith("_AddCartAndAddresses", pending[2]);
            Assert.EndsWith("_AddOrdering", pending[3]);
            await context.Database.MigrateAsync(ct);
        }

        Assert.Equal(IdentityTables.Length, await TableCountAsync(database, IdentityTables));

        await using (var context = database.CreateDbContext())
        {
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
            await context.Database.MigrateAsync(ct);
        }

        var history = await database.ScalarAsync<string>(
            "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\"", ct);
        Assert.NotNull(history);
        var ids = history.Split(',');
        Assert.Equal(5, ids.Length);
        Assert.EndsWith("_InitialCatalog", ids[0]);
        Assert.EndsWith("_AddIdentity", ids[1]);
        Assert.EndsWith("_AddInventory", ids[2]);
        Assert.EndsWith("_AddCartAndAddresses", ids[3]);
        Assert.EndsWith("_AddOrdering", ids[4]);
    }

    [Fact]
    public async Task Seeded_catalog_data_survives_the_identity_migration_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, CatalogMigration);
        await SeedCatalogAsync(database);

        var before = await CatalogCountsAsync(database);
        Assert.Equal(4, before.Categories);
        var priceBefore = await database.ScalarAsync<string>("SELECT price::text FROM product_variants WHERE sku = 'SSC-3001-STD'", ct);
        var snapshotBefore = await database.ScalarAsync<string>(
            "SELECT md5(string_agg(id::text || slug || name || is_demo::text, ',' ORDER BY id)) FROM products", ct);

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(before, await CatalogCountsAsync(database));
        Assert.Equal(priceBefore, await database.ScalarAsync<string>("SELECT price::text FROM product_variants WHERE sku = 'SSC-3001-STD'", ct));
        Assert.Equal(snapshotBefore, await database.ScalarAsync<string>(
            "SELECT md5(string_agg(id::text || slug || name || is_demo::text, ',' ORDER BY id)) FROM products", ct));
        Assert.Equal(IdentityTables.Length, await TableCountAsync(database, IdentityTables));
    }

    [Fact]
    public async Task Rolling_identity_back_leaves_the_catalog_intact_and_reapplying_restores_roles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await SeedCatalogAsync(database);
        var before = await CatalogCountsAsync(database);

        await MigrateToAsync(database, CatalogMigration);

        Assert.Equal(0, await TableCountAsync(database, IdentityTables));
        Assert.Equal(before, await CatalogCountsAsync(database));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(before, await CatalogCountsAsync(database));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetRoles\"", ct));
    }

    [Fact]
    public async Task Idempotent_script_upgrades_a_catalog_only_database_and_is_safe_to_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, CatalogMigration);
        await SeedCatalogAsync(database);
        var before = await CatalogCountsAsync(database);

        string script;
        await using (var context = database.CreateDbContext())
        {
            script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await database.ExecuteAsync(script, ct);
        await database.ExecuteAsync(script, ct);

        Assert.Equal(before, await CatalogCountsAsync(database));
        Assert.Equal(IdentityTables.Length, await TableCountAsync(database, IdentityTables));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetRoles\"", ct));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
    }

    [Fact]
    public async Task Exactly_the_three_roles_exist_once_with_fixed_ids_and_no_users()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync();

        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetRoles\"", ct));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUsers\"", ct));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUserRoles\"", ct));

        (string Name, Guid Id)[] expected =
        [
            (AppRoles.Customer, AppRoles.CustomerId),
            (AppRoles.Warehouse, AppRoles.WarehouseId),
            (AppRoles.Admin, AppRoles.AdminId),
        ];
        foreach (var (name, id) in expected)
        {
            Assert.Equal(1, await database.ScalarAsync<long>(
                $"SELECT count(*) FROM \"AspNetRoles\" WHERE \"Id\" = '{id}' AND \"Name\" = '{name}' AND \"NormalizedName\" = '{name.ToUpperInvariant()}'", ct));
        }

        // Rol adı benzersizdir: aynı rol ikinci kez eklenemez.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            $"INSERT INTO \"AspNetRoles\" (\"Id\", \"Name\", \"NormalizedName\") VALUES ('{Guid.NewGuid()}', 'Admin', 'ADMIN')", ct));
        Assert.Equal("23505", exception.SqlState);
        Assert.Equal("RoleNameIndex", exception.ConstraintName);
    }

    private static string UserSql(string userName, string normalizedUserName, string? normalizedEmail) =>
        $"""
        INSERT INTO "AspNetUsers" ("Id", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
            "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
        VALUES ('{Guid.NewGuid()}', now(), '{userName}', '{normalizedUserName}', {(normalizedEmail is null ? "NULL" : $"'{normalizedEmail.ToLowerInvariant()}'")},
            {(normalizedEmail is null ? "NULL" : $"'{normalizedEmail}'")}, false, false, false, true, 0)
        """;

    [Fact]
    public async Task Same_normalized_email_twice_is_a_unique_violation_even_when_the_app_check_is_bypassed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await database.ExecuteAsync(UserSql($"a{suffix}@ornek.test", $"A{suffix}@ORNEK.TEST", $"A{suffix}@ORNEK.TEST"), ct);

        // Farklı kullanıcı adı, aynı normalize e-posta (uygulama denetimi atlanmış yarış durumu simülasyonu).
        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            UserSql($"b{suffix}", $"B{suffix}", $"A{suffix}@ORNEK.TEST"), ct));
        Assert.Equal("23505", exception.SqlState);
        Assert.Equal("EmailIndex", exception.ConstraintName);

        // Farklı e-posta serbest.
        await database.ExecuteAsync(UserSql($"c{suffix}", $"C{suffix}", $"C{suffix}@ORNEK.TEST"), ct);
    }

    [Fact]
    public async Task Same_normalized_user_name_twice_is_a_unique_violation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await database.ExecuteAsync(UserSql($"u{suffix}", $"U{suffix}", $"U{suffix}@ORNEK.TEST"), ct);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            UserSql($"u{suffix}", $"U{suffix}", $"V{suffix}@ORNEK.TEST"), ct));
        Assert.Equal("23505", exception.SqlState);
        Assert.Equal("UserNameIndex", exception.ConstraintName);
    }

    [Fact]
    public async Task Users_without_an_email_do_not_collide_on_the_unique_email_index()
    {
        // PostgreSQL UNIQUE birden çok NULL'a izin verir; e-postasız satırlar birbirini engellemez.
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

        await database.ExecuteAsync(UserSql($"n1{suffix}", $"N1{suffix}", null), ct);
        await database.ExecuteAsync(UserSql($"n2{suffix}", $"N2{suffix}", null), ct);
    }

    [Fact]
    public async Task Added_user_gets_a_utc_created_at_from_the_injected_clock_and_demo_seed_creates_no_roles_or_users()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync();
        await SeedCatalogAsync(database);
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetRoles\"", ct));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUsers\"", ct));

        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        Guid userId;
        await using (var context = database.CreateDbContext(new FixedTimeProvider(now)))
        {
            var user = new AppUser
            {
                UserName = $"k{suffix}",
                NormalizedUserName = $"K{suffix}".ToUpperInvariant(),
                Email = $"k{suffix}@ornek.test",
                NormalizedEmail = $"K{suffix}@ORNEK.TEST".ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString("N"),
            };
            context.Users.Add(user);
            await context.SaveChangesAsync(ct);
            userId = user.Id;
        }

        await using var read = database.CreateDbContext();
        var loaded = await read.Users.SingleAsync(u => u.Id == userId, ct);
        Assert.Equal(now, loaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
        Assert.Null(loaded.PasswordHash); // parola yok: yalnızca hash alanı vardır, düz metin alanı yoktur
        Assert.False(loaded.EmailConfirmed);
    }

    [Fact]
    public async Task Identity_columns_follow_the_data_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync();

        Assert.Equal("uuid", await database.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'AspNetUsers' AND column_name = 'Id'", ct));
        Assert.Equal("uuid", await database.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'AspNetRoles' AND column_name = 'Id'", ct));
        Assert.Equal("timestamp with time zone", await database.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'AspNetUsers' AND column_name = 'CreatedAt'", ct));
        // Gereksiz kişisel veri alanı eklenmedi: ad/soyad sütunu yok (PhoneNumber Identity'nin kendi alanıdır, kullanılmaz).
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'AspNetUsers' AND column_name IN ('FirstName','LastName','FullName','Name')", ct));
    }
}

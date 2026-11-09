using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Domain.Administration;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 8 <c>admin_audit_log</c> (docs/CONTRACT_PHASE_8.md "Persistence" 2) ve <c>version</c> sütunları: kısıtlar, yalnız-ekle trigger'ları, FK RESTRICT ve
/// Domain kuralları. Ham SQL kullanılır (savunma derinliği: uygulama hatası ya da elle SQL kirli veri üretemesin). Her test kendi verisini kurar.
/// SqlState: 23514 check, 23502 not null, 23503 foreign key, 23001 değişmezlik trigger'ı (restrict_violation).
/// </summary>
public class AdminAuditLogTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private PostgresTestDatabase Db => fixture.Database;

    private OrderingTestData Data => new(Db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AuditSql(
        Guid actorId,
        string action = "'category.create'",
        string targetType = "'category'",
        string targetId = "gen_random_uuid()",
        string changes = "'{\"name\": \"Yeni\"}'::jsonb",
        string occurredAt = "now()") =>
        $"""
        INSERT INTO admin_audit_log (id, actor_user_id, action, target_type, target_id, changes, occurred_at)
        VALUES ('{Guid.NewGuid()}', '{actorId}', {action}, {targetType}, {targetId}, {changes}, {occurredAt})
        """;

    private async Task<Guid> InsertAuditAsync(Guid actorId)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(
            $"""
            INSERT INTO admin_audit_log (id, actor_user_id, action, target_type, target_id, changes, occurred_at)
            VALUES ('{id}', '{actorId}', 'product.update', 'product', gen_random_uuid(), jsonb_build_object('price', 1), now())
            """,
            Ct);
        return id;
    }

    // ---- version sütunları ----

    [Fact]
    public async Task Raw_inserts_without_a_version_column_start_at_one_on_categories_products_and_variants()
    {
        var (variantId, _) = await Data.InsertVariantAsync(); // ham INSERT: version verilmez

        Assert.Equal(1, await Db.ScalarAsync<int>($"SELECT version FROM product_variants WHERE id = '{variantId}'", Ct));
        Assert.Equal(1, await Db.ScalarAsync<int>(
            $"SELECT p.version FROM products p JOIN product_variants v ON v.product_id = p.id WHERE v.id = '{variantId}'", Ct));
        Assert.Equal(1, await Db.ScalarAsync<int>(
            $"SELECT c.version FROM categories c JOIN products p ON p.category_id = c.id JOIN product_variants v ON v.product_id = p.id WHERE v.id = '{variantId}'", Ct));
    }

    [Theory]
    [InlineData("categories", "ck_categories_version_positive", "0")]
    [InlineData("categories", "ck_categories_version_positive", "-1")]
    [InlineData("products", "ck_products_version_positive", "0")]
    [InlineData("products", "ck_products_version_positive", "-5")]
    [InlineData("product_variants", "ck_product_variants_version_positive", "0")]
    [InlineData("product_variants", "ck_product_variants_version_positive", "-2147483648")]
    public async Task Version_below_one_is_rejected(string table, string constraint, string value)
    {
        var (variantId, _) = await Data.InsertVariantAsync();
        var idSql = table switch
        {
            "categories" => $"(SELECT c.id FROM categories c JOIN products p ON p.category_id = c.id JOIN product_variants v ON v.product_id = p.id WHERE v.id = '{variantId}')",
            "products" => $"(SELECT product_id FROM product_variants WHERE id = '{variantId}')",
            _ => $"'{variantId}'",
        };

        await Data.AssertViolationAsync("23514", $"UPDATE {table} SET version = {value} WHERE id = {idSql}", constraint);
    }

    [Theory]
    [InlineData("categories")]
    [InlineData("products")]
    [InlineData("product_variants")]
    public async Task Version_cannot_be_null(string table)
    {
        var (variantId, _) = await Data.InsertVariantAsync();
        var idSql = table switch
        {
            "categories" => $"(SELECT c.id FROM categories c JOIN products p ON p.category_id = c.id JOIN product_variants v ON v.product_id = p.id WHERE v.id = '{variantId}')",
            "products" => $"(SELECT product_id FROM product_variants WHERE id = '{variantId}')",
            _ => $"'{variantId}'",
        };

        await Data.AssertViolationAsync("23502", $"UPDATE {table} SET version = NULL WHERE id = {idSql}");
    }

    [Fact]
    public async Task Version_columns_are_integer_not_null_default_one()
    {
        foreach (var table in new[] { "categories", "products", "product_variants" })
        {
            var definition = await Db.ScalarAsync<string>(
                $"SELECT data_type || ':' || is_nullable || ':' || column_default FROM information_schema.columns WHERE table_name = '{table}' AND column_name = 'version'", Ct);
            Assert.Equal("integer:NO:1", definition);
        }
    }

    [Fact]
    public async Task Domain_entities_start_at_version_one_and_bump_by_exactly_one_through_ef()
    {
        var categoryId = Guid.NewGuid();
        await using (var context = Db.CreateDbContext())
        {
            var category = new SSCommerce.Domain.Catalog.Category("Sürüm kategorisi", $"surum-{OrderingTestData.Unique()}");
            categoryId = category.Id;
            Assert.Equal(1, category.Version);
            context.Categories.Add(category);
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await Db.ScalarAsync<int>($"SELECT version FROM categories WHERE id = '{categoryId}'", Ct));

        await using (var context = Db.CreateDbContext())
        {
            var category = await context.Categories.SingleAsync(c => c.Id == categoryId, Ct);
            category.BumpVersion();
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(2, await Db.ScalarAsync<int>($"SELECT version FROM categories WHERE id = '{categoryId}'", Ct));
    }

    // ---- admin_audit_log: geçerli satırlar ----

    [Theory]
    [InlineData("category.create", "category")]
    [InlineData("category.update", "category")]
    [InlineData("product.images_replace", "product")]
    [InlineData("variant.update", "variant")]
    [InlineData("warehouse.create", "warehouse")]
    [InlineData("role.grant", "user")]
    [InlineData("role.bootstrap", "user")]
    [InlineData("order.delivery_view", "order")]
    public async Task Contract_actions_and_target_types_are_accepted(string action, string targetType)
    {
        var actor = await Data.InsertUserAsync();

        await Db.ExecuteAsync(AuditSql(actor, $"'{action}'", $"'{targetType}'"), Ct);

        Assert.Equal(1L, await Db.ScalarAsync<long>(
            $"SELECT count(*) FROM admin_audit_log WHERE actor_user_id = '{actor}' AND action = '{action}'", Ct));
    }

    [Theory]
    [InlineData("'category'")] // nokta yok
    [InlineData("'Category.create'")] // büyük harf
    [InlineData("'category.Create'")]
    [InlineData("'category.create1'")] // rakam
    [InlineData("'.create'")]
    [InlineData("'category.'")]
    [InlineData("'category..create'")]
    [InlineData("'category create'")]
    [InlineData("'category.create' || chr(10)")] // sondaki satır sonu
    [InlineData("''")]
    public async Task Action_format_is_enforced(string action)
    {
        var actor = await Data.InsertUserAsync();

        await Data.AssertViolationAsync("23514", AuditSql(actor, action), "ck_admin_audit_log_action_format");
    }

    [Theory]
    [InlineData("'basket'")]
    [InlineData("'Category'")]
    [InlineData("''")]
    [InlineData("'orders'")]
    public async Task Target_type_must_be_one_of_the_contract_values(string targetType)
    {
        var actor = await Data.InsertUserAsync();

        await Data.AssertViolationAsync("23514", AuditSql(actor, targetType: targetType), "ck_admin_audit_log_target_type");
    }

    [Theory]
    [InlineData("'[]'::jsonb")]
    [InlineData("'\"metin\"'::jsonb")]
    [InlineData("'12'::jsonb")]
    [InlineData("'null'::jsonb")]
    [InlineData("'true'::jsonb")]
    public async Task Changes_must_be_a_json_object(string changes)
    {
        var actor = await Data.InsertUserAsync();

        await Data.AssertViolationAsync("23514", AuditSql(actor, changes: changes), "ck_admin_audit_log_changes_object");
    }

    [Fact]
    public async Task Changes_size_boundary_is_8192_octets_of_the_jsonb_text()
    {
        var actor = await Data.InsertUserAsync();
        // jsonb metni: {"note": "<n karakter>"} = 12 + n bayt -> 8180 tam sınır.
        var exactly = "jsonb_build_object('note', repeat('a', 8180))";
        var oneOver = "jsonb_build_object('note', repeat('a', 8181))";
        Assert.Equal(8192, await Db.ScalarAsync<int>($"SELECT octet_length({exactly}::text)", Ct));

        await Db.ExecuteAsync(AuditSql(actor, changes: exactly), Ct);
        await Data.AssertViolationAsync("23514", AuditSql(actor, changes: oneOver), "ck_admin_audit_log_changes_object");
    }

    [Fact]
    public async Task Changes_size_is_counted_in_octets_not_characters()
    {
        var actor = await Data.InsertUserAsync();
        // 'ş' UTF-8'de 2 bayttır: 4100 karakter = 8200 bayt + 12 > 8192, karakter sayısı ise sınırın çok altında.
        await Data.AssertViolationAsync(
            "23514",
            AuditSql(actor, changes: "jsonb_build_object('note', repeat('ş', 4100))"),
            "ck_admin_audit_log_changes_object");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("password_hash")]
    [InlineData("security_stamp")]
    [InlineData("token")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("address")]
    [InlineData("street_address")]
    public async Task Forbidden_top_level_keys_are_rejected(string key)
    {
        var actor = await Data.InsertUserAsync();

        await Data.AssertViolationAsync(
            "23514", AuditSql(actor, changes: $"jsonb_build_object('{key}', 'x', 'ok', 1)"), "ck_admin_audit_log_changes_object");
    }

    [Fact]
    public async Task Forbidden_keys_below_the_top_level_are_not_a_database_concern_the_domain_does_not_write_them()
    {
        // Sözleşme "üst düzey anahtar" der; iç içe nesneyi DB denetlemez (uygulama {old,new} yalnız alan adı/değer yazar).
        var actor = await Data.InsertUserAsync();

        await Db.ExecuteAsync(AuditSql(actor, changes: "'{\"old\": {\"email\": \"x\"}}'::jsonb"), Ct);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("actor_user_id")]
    [InlineData("action")]
    [InlineData("target_type")]
    [InlineData("target_id")]
    [InlineData("changes")]
    [InlineData("occurred_at")]
    public async Task Every_audit_column_is_required(string column)
    {
        var actor = await Data.InsertUserAsync();
        var values = new Dictionary<string, string>
        {
            ["id"] = $"'{Guid.NewGuid()}'",
            ["actor_user_id"] = $"'{actor}'",
            ["action"] = "'category.create'",
            ["target_type"] = "'category'",
            ["target_id"] = "gen_random_uuid()",
            ["changes"] = "'{}'::jsonb",
            ["occurred_at"] = "now()",
        };
        values[column] = "NULL";
        var sql = $"INSERT INTO admin_audit_log ({string.Join(", ", values.Keys)}) VALUES ({string.Join(", ", values.Values)})";

        await Data.AssertViolationAsync("23502", sql);
    }

    [Fact]
    public async Task Occurred_at_has_no_database_default_it_comes_from_the_application_clock()
    {
        Assert.Null(await Db.ScalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_name = 'admin_audit_log' AND column_name = 'occurred_at'", Ct));
    }

    // ---- FK RESTRICT ----

    [Fact]
    public async Task An_unknown_actor_is_rejected_by_the_foreign_key()
    {
        await Data.AssertViolationAsync("23503", AuditSql(Guid.NewGuid()), "fk_admin_audit_log_actor_user");
    }

    [Fact]
    public async Task A_user_with_audit_rows_cannot_be_deleted()
    {
        var actor = await Data.InsertUserAsync();
        await InsertAuditAsync(actor);

        await Data.AssertViolationAsync("23503", $"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = '{actor}'", "fk_admin_audit_log_actor_user");
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Id\" = '{actor}'", Ct));
    }

    // ---- yalnız-ekle ----

    [Fact]
    public async Task Audit_rows_cannot_be_updated()
    {
        var actor = await Data.InsertUserAsync();
        var id = await InsertAuditAsync(actor);
        var before = await Db.ScalarAsync<string>($"SELECT a::text FROM admin_audit_log a WHERE id = '{id}'", Ct);

        await Data.AssertViolationAsync("23001", $"UPDATE admin_audit_log SET changes = '{{}}'::jsonb WHERE id = '{id}'");
        await Data.AssertViolationAsync("23001", $"UPDATE admin_audit_log SET action = 'product.create' WHERE id = '{id}'");
        await Data.AssertViolationAsync("23001", $"UPDATE admin_audit_log SET occurred_at = occurred_at WHERE id = '{id}'"); // değişmeyen atama bile

        Assert.Equal(before, await Db.ScalarAsync<string>($"SELECT a::text FROM admin_audit_log a WHERE id = '{id}'", Ct));
    }

    [Fact]
    public async Task Audit_rows_cannot_be_deleted()
    {
        var actor = await Data.InsertUserAsync();
        var id = await InsertAuditAsync(actor);

        await Data.AssertViolationAsync("23001", $"DELETE FROM admin_audit_log WHERE id = '{id}'");

        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM admin_audit_log WHERE id = '{id}'", Ct));
    }

    [Fact]
    public async Task The_audit_table_cannot_be_truncated()
    {
        var actor = await Data.InsertUserAsync();
        var id = await InsertAuditAsync(actor);

        await Data.AssertViolationAsync("23001", "TRUNCATE admin_audit_log");
        await Data.AssertViolationAsync("23001", "TRUNCATE admin_audit_log CASCADE");

        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM admin_audit_log WHERE id = '{id}'", Ct));
    }

    [Fact]
    public async Task The_append_only_function_is_generic_and_names_the_table()
    {
        var actor = await Data.InsertUserAsync();
        var id = await InsertAuditAsync(actor);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync($"DELETE FROM admin_audit_log WHERE id = '{id}'", Ct));

        Assert.Contains("admin_audit_log is append-only", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(
            "admin_audit_log.trg_admin_audit_log_no_truncate,admin_audit_log.trg_admin_audit_log_no_update_delete",
            await Db.ScalarAsync<string>(
                "SELECT string_agg(tgrelid::regclass::text || '.' || tgname, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'admin_audit_log'::regclass AND NOT tgisinternal", Ct));
        Assert.Equal(1L, await Db.ScalarAsync<long>("SELECT count(*) FROM pg_proc WHERE proname = 'append_only_reject_change'", Ct));
    }

    // ---- indeksler ----

    [Fact]
    public async Task Audit_indexes_serve_the_newest_first_listings()
    {
        async Task<string> DefinitionAsync(string name) =>
            (await Db.ScalarAsync<string>($"SELECT indexdef FROM pg_indexes WHERE indexname = '{name}'", Ct))!;

        Assert.Contains("(occurred_at DESC, id DESC)", await DefinitionAsync("ix_admin_audit_log_occurred_at_id"));
        Assert.Contains("(target_type, target_id, occurred_at DESC, id DESC)", await DefinitionAsync("ix_admin_audit_log_target_type_target_id_occurred_at_id"));
        Assert.Contains("(actor_user_id)", await DefinitionAsync("ix_admin_audit_log_actor_user_id"));
        Assert.Contains("(occurred_at DESC, id DESC)", await DefinitionAsync("ix_stock_movements_occurred_at_id"));
        Assert.Contains("(occurred_at DESC, id DESC)", await DefinitionAsync("ix_order_status_history_occurred_at_id"));
        var open = await DefinitionAsync("ix_orders_open_created_at");
        Assert.Contains("(created_at, id)", open);
        Assert.Contains("'placed'", open);
        Assert.Contains("'processing'", open);
        var openUser = await DefinitionAsync("ix_orders_open_user");
        Assert.Contains("(user_id)", openUser);
        Assert.Contains("'processing'", openUser);
    }

    // ---- Domain kuralları ve EF eşlemesi ----

    [Fact]
    public async Task A_domain_audit_entry_round_trips_through_ef_with_a_v7_id_and_utc_time()
    {
        var actor = await Data.InsertUserAsync();
        var targetId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(3));
        var entry = new AdminAuditEntry(actor, "category.update", "category", targetId, "{\"name\":{\"old\":\"A\",\"new\":\"B\"}}", occurredAt);
        Assert.Equal(7, (entry.Id.ToByteArray(bigEndian: true)[6] >> 4));

        await using (var context = Db.CreateDbContext())
        {
            context.AdminAuditLog.Add(entry);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = Db.CreateDbContext())
        {
            var loaded = await context.AdminAuditLog.AsNoTracking().SingleAsync(e => e.Id == entry.Id, Ct);
            Assert.Equal(actor, loaded.ActorUserId);
            Assert.Equal("category.update", loaded.Action);
            Assert.Equal("category", loaded.TargetType);
            Assert.Equal(targetId, loaded.TargetId);
            Assert.Equal(occurredAt.UtcDateTime, loaded.OccurredAt.UtcDateTime);
            Assert.Equal(TimeSpan.Zero, loaded.OccurredAt.Offset);
            Assert.Contains("\"new\": \"B\"", loaded.Changes, StringComparison.Ordinal); // jsonb metin biçimi
        }
    }

    [Fact]
    public async Task Ef_cannot_update_or_delete_a_persisted_audit_entry()
    {
        var actor = await Data.InsertUserAsync();
        var entry = new AdminAuditEntry(actor, "warehouse.create", "warehouse", Guid.NewGuid(), "{}", DateTimeOffset.UtcNow);
        await using (var context = Db.CreateDbContext())
        {
            context.AdminAuditLog.Add(entry);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = Db.CreateDbContext())
        {
            context.AdminAuditLog.Remove(await context.AdminAuditLog.SingleAsync(e => e.Id == entry.Id, Ct));
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
            Assert.Equal("23001", Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        }
    }

    [Theory]
    [InlineData("Category.create")]
    [InlineData("category")]
    [InlineData("category.create1")]
    [InlineData("category.create\n")]
    [InlineData("")]
    public void The_domain_rejects_a_bad_action(string action)
    {
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(Guid.NewGuid(), action, "category", Guid.NewGuid(), "{}", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_domain_rejects_bad_targets_ids_and_changes_before_the_database_does()
    {
        var actor = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "basket", Guid.NewGuid(), "{}", now));
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(Guid.Empty, "category.create", "category", Guid.NewGuid(), "{}", now));
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "category", Guid.Empty, "{}", now));
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "category", Guid.NewGuid(), "[]", now)); // nesne değil
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "category", Guid.NewGuid(), "not json", now));
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "category", Guid.NewGuid(), "", now));
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "category", Guid.NewGuid(), "{\"Email\":\"a@b.c\"}", now)); // büyük/küçük harf duyarsız
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "category", Guid.NewGuid(), $"{{\"note\":\"{new string('a', 8200)}\"}}", now));
    }

    [Fact]
    public void The_domain_size_check_matches_the_database_boundary_for_ascii_text()
    {
        var actor = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Sıkıştırılmış JSON {"note":"..."} jsonb metninden 1 bayt kısadır (": " yerine ":"), sınır jsonb metnine göre: 8180 karakter kabul, 8181 ret.
        _ = new AdminAuditEntry(actor, "category.create", "category", Guid.NewGuid(), $"{{\"note\":\"{new string('a', 8180)}\"}}", now);
        Assert.Throws<AdminAuditRuleException>(() => new AdminAuditEntry(actor, "category.create", "category", Guid.NewGuid(), $"{{\"note\":\"{new string('a', 8181)}\"}}", now));
    }
}

using Npgsql;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 8 <c>trg_categories_guard_parent</c> (docs/CONTRACT_PHASE_8.md "Persistence" 3): döngü, derinlik (en çok 5, kök = 1) ve eşzamanlı yazma.
/// Eşzamanlılık testleri GERÇEK PostgreSQL ister (mock ile yapılamaz): iki bağlantı, her biri READ COMMITTED, sözleşmedeki kilit sırasıyla
/// (önce ağaç advisory kilidi, sonra satır FOR UPDATE). Kilit bağlantıları havuzsuzdur: oturum kilidi havuza sızmaz.
/// SqlState: 23514 check_violation (CONSTRAINT adıyla), 55P03 lock_not_available, 40P01 deadlock_detected.
/// </summary>
public class CategoryTreeGuardTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private const string TreeLock = "pg_advisory_xact_lock(hashtextextended('catalog:category-tree', 0))";

    private sealed record SwapResult(bool Committed, string? SqlState, string? Constraint);

    private PostgresTestDatabase Db => fixture.Database;

    private OrderingTestData Data => new(Db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> InsertCategoryAsync(Guid? parentId = null)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(InsertSql(id, parentId), Ct);
        return id;
    }

    private static string InsertSql(Guid id, Guid? parentId) =>
        $"INSERT INTO categories (id, name, slug, parent_id, sort_order, is_active, created_at, updated_at) " +
        $"VALUES ('{id}', 'K', 'kat-{OrderingTestData.Unique()}', {(parentId is null ? "NULL" : $"'{parentId}'")}, 0, true, now(), now())";

    /// <summary>Kökten başlayan <paramref name="length"/> düzeyli zincir; kökten yaprağa kimlikler.</summary>
    private async Task<Guid[]> InsertChainAsync(int length)
    {
        var chain = new Guid[length];
        for (var i = 0; i < length; i++)
        {
            chain[i] = await InsertCategoryAsync(i == 0 ? null : chain[i - 1]);
        }

        return chain;
    }

    private NpgsqlConnection NewSession()
    {
        var builder = new NpgsqlConnectionStringBuilder(Db.ConnectionString) { Pooling = false };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
    }

    // ---- döngü ----

    [Fact]
    public async Task Self_parent_is_still_rejected_by_the_existing_check_not_the_trigger()
    {
        var id = await InsertCategoryAsync();

        await Data.AssertViolationAsync("23514", $"UPDATE categories SET parent_id = id WHERE id = '{id}'", "ck_categories_parent_not_self");
        await Data.AssertViolationAsync(
            "23514",
            $"INSERT INTO categories (id, name, slug, parent_id, sort_order, is_active, created_at, updated_at) VALUES ('{Guid.Parse("00000000-0000-0000-0000-0000000000aa")}', 'K', 'kat-{OrderingTestData.Unique()}', '{Guid.Parse("00000000-0000-0000-0000-0000000000aa")}', 0, true, now(), now())",
            "ck_categories_parent_not_self");
    }

    [Fact]
    public async Task A_simple_cycle_a_to_b_to_a_is_rejected_with_the_cycle_constraint()
    {
        var a = await InsertCategoryAsync();
        var b = await InsertCategoryAsync(a);

        await Data.AssertViolationAsync("23514", $"UPDATE categories SET parent_id = '{b}' WHERE id = '{a}'", "ck_categories_no_cycle");

        Assert.Null(await Db.ScalarAsync<Guid?>($"SELECT parent_id FROM categories WHERE id = '{a}'", Ct));
    }

    [Fact]
    public async Task A_longer_cycle_through_three_levels_is_rejected()
    {
        var chain = await InsertChainAsync(3);

        await Data.AssertViolationAsync("23514", $"UPDATE categories SET parent_id = '{chain[2]}' WHERE id = '{chain[0]}'", "ck_categories_no_cycle");
        await Data.AssertViolationAsync("23514", $"UPDATE categories SET parent_id = '{chain[1]}' WHERE id = '{chain[0]}'", "ck_categories_no_cycle");
    }

    [Fact]
    public async Task Moving_a_subtree_under_a_sibling_branch_is_allowed()
    {
        var root = await InsertCategoryAsync();
        var left = await InsertCategoryAsync(root);
        var right = await InsertCategoryAsync(root);
        var leaf = await InsertCategoryAsync(left);

        await Db.ExecuteAsync($"UPDATE categories SET parent_id = '{right}' WHERE id = '{left}'", Ct);

        Assert.Equal(right, await Db.ScalarAsync<Guid>($"SELECT parent_id FROM categories WHERE id = '{left}'", Ct));
        Assert.Equal(left, await Db.ScalarAsync<Guid>($"SELECT parent_id FROM categories WHERE id = '{leaf}'", Ct));
    }

    // ---- derinlik ----

    [Fact]
    public async Task Depth_five_is_accepted_and_depth_six_is_rejected_on_insert()
    {
        var chain = await InsertChainAsync(5); // 5. düzey kabul (INSERT zinciri zaten bunu yaptı)
        Assert.Equal(5, chain.Length);

        await Data.AssertViolationAsync("23514", InsertSql(Guid.NewGuid(), chain[4]), "ck_categories_max_depth");
        // Dördüncü düzeye ikinci bir yaprak (yine 5. düzey) kabul edilir.
        await InsertCategoryAsync(chain[3]);
    }

    [Fact]
    public async Task Reparenting_a_leaf_respects_the_depth_boundary()
    {
        var chain = await InsertChainAsync(5);
        var loneLeaf = await InsertCategoryAsync();

        await Data.AssertViolationAsync("23514", $"UPDATE categories SET parent_id = '{chain[4]}' WHERE id = '{loneLeaf}'", "ck_categories_max_depth");
        await Db.ExecuteAsync($"UPDATE categories SET parent_id = '{chain[3]}' WHERE id = '{loneLeaf}'", Ct); // 5. düzey

        Assert.Equal(chain[3], await Db.ScalarAsync<Guid>($"SELECT parent_id FROM categories WHERE id = '{loneLeaf}'", Ct));
    }

    [Fact]
    public async Task Moving_a_subtree_of_height_two_under_a_depth_three_parent_is_rejected()
    {
        var parentChain = await InsertChainAsync(3);
        var subtreeRoot = await InsertCategoryAsync();
        var child = await InsertCategoryAsync(subtreeRoot);
        var grandchild = await InsertCategoryAsync(child); // yükseklik 2: kök + 2 düzey

        // 3. düzeyin altına: kök 4., torun 6. düzey -> ret.
        await Data.AssertViolationAsync("23514", $"UPDATE categories SET parent_id = '{parentChain[2]}' WHERE id = '{subtreeRoot}'", "ck_categories_max_depth");
        // 2. düzeyin altına: kök 3., torun 5. düzey -> kabul.
        await Db.ExecuteAsync($"UPDATE categories SET parent_id = '{parentChain[1]}' WHERE id = '{subtreeRoot}'", Ct);

        Assert.Equal(parentChain[1], await Db.ScalarAsync<Guid>($"SELECT parent_id FROM categories WHERE id = '{subtreeRoot}'", Ct));
        Assert.Equal(child, await Db.ScalarAsync<Guid>($"SELECT parent_id FROM categories WHERE id = '{grandchild}'", Ct));
    }

    [Fact]
    public async Task Moving_a_subtree_of_height_one_under_a_depth_three_parent_is_accepted()
    {
        var parentChain = await InsertChainAsync(3);
        var subtreeRoot = await InsertCategoryAsync();
        await InsertCategoryAsync(subtreeRoot); // yükseklik 1

        await Db.ExecuteAsync($"UPDATE categories SET parent_id = '{parentChain[2]}' WHERE id = '{subtreeRoot}'", Ct); // en derin 5. düzey

        Assert.Equal(parentChain[2], await Db.ScalarAsync<Guid>($"SELECT parent_id FROM categories WHERE id = '{subtreeRoot}'", Ct));
    }

    [Fact]
    public async Task Depth_checks_cover_wide_subtrees_not_only_the_first_branch()
    {
        var parentChain = await InsertChainAsync(3);
        var subtreeRoot = await InsertCategoryAsync();
        await InsertCategoryAsync(subtreeRoot); // kısa dal
        var tall = await InsertCategoryAsync(subtreeRoot);
        var tallChild = await InsertCategoryAsync(tall); // uzun dal: yükseklik 2

        await Data.AssertViolationAsync("23514", $"UPDATE categories SET parent_id = '{parentChain[2]}' WHERE id = '{subtreeRoot}'", "ck_categories_max_depth");
        Assert.NotEqual(Guid.Empty, tallChild);
    }

    // ---- kilit yalnızca gerektiğinde alınır ----

    [Fact]
    public async Task Rename_deactivate_root_insert_and_unchanged_parent_do_not_take_the_tree_lock_but_a_real_reparent_does()
    {
        var root = await InsertCategoryAsync();
        var child = await InsertCategoryAsync(root);
        var other = await InsertCategoryAsync();

        await using var holder = NewSession();
        await using var worker = NewSession();
        await holder.OpenAsync(Ct);
        await worker.OpenAsync(Ct);
        await ExecAsync(holder, "SELECT pg_advisory_lock(hashtextextended('catalog:category-tree', 0))");
        try
        {
            await ExecAsync(worker, "SET lock_timeout = '400ms'");

            // Kilit başka oturumda tutulurken bunlar ENGELLENMEZ:
            await ExecAsync(worker, $"UPDATE categories SET name = 'Yeni ad' WHERE id = '{child}'");
            await ExecAsync(worker, $"UPDATE categories SET is_active = false, sort_order = 7 WHERE id = '{child}'");
            await ExecAsync(worker, InsertSql(Guid.NewGuid(), null)); // kök olarak ekleme
            await ExecAsync(worker, $"UPDATE categories SET parent_id = parent_id WHERE id = '{child}'"); // değişmeyen üst
            await ExecAsync(worker, $"UPDATE categories SET parent_id = NULL WHERE id = '{child}'"); // köke taşıma
            await ExecAsync(worker, $"UPDATE categories SET parent_id = '{root}' WHERE id = '{child}'"); // ... ve geri: gerçek değişiklik -> kilit ister

            Assert.Fail("Gerçek üst değişikliği tree kilidini beklemeliydi.");
        }
        catch (PostgresException exception) when (exception.SqlState == "55P03")
        {
            // Beklenen: yalnız son (gerçek üst değişikliği) deyimi kilit zaman aşımına uğradı.
        }
        finally
        {
            await ExecAsync(holder, "SELECT pg_advisory_unlock(hashtextextended('catalog:category-tree', 0))");
        }

        // Rename/deactivate gerçekten uygulanmıştı; son deyim reddedilip geri alındı (child köke taşınmış kaldı).
        Assert.Equal("Yeni ad", await Db.ScalarAsync<string>($"SELECT name FROM categories WHERE id = '{child}'", Ct));
        Assert.False(await Db.ScalarAsync<bool>($"SELECT is_active FROM categories WHERE id = '{child}'", Ct));

        // Karşıt kanıt: kilit yeniden tutulurken PARENT'LI bir INSERT de kilidi bekler (kök ekleme beklemiyordu).
        await ExecAsync(holder, "SELECT pg_advisory_lock(hashtextextended('catalog:category-tree', 0))");
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(worker, InsertSql(Guid.NewGuid(), other)));
            Assert.Equal("55P03", exception.SqlState);
        }
        finally
        {
            await ExecAsync(holder, "SELECT pg_advisory_unlock(hashtextextended('catalog:category-tree', 0))");
        }
    }

    // ---- eşzamanlı yazma ----

    [Fact]
    public async Task A_parallel_swap_with_the_contract_lock_order_commits_exactly_one_and_leaves_no_cycle()
    {
        var a = await InsertCategoryAsync();
        var b = await InsertCategoryAsync();

        await using var first = NewSession();
        await using var second = NewSession();
        await first.OpenAsync(Ct);
        await second.OpenAsync(Ct);

        // 1. oturum: advisory kilit -> satır FOR UPDATE -> A.parent = B (commit etmeden bekler).
        var firstTx = await first.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        await ExecAsync(first, $"SELECT {TreeLock}", firstTx);
        await ExecAsync(first, $"SELECT 1 FROM categories WHERE id = '{a}' FOR UPDATE", firstTx);
        await ExecAsync(first, $"UPDATE categories SET parent_id = '{b}' WHERE id = '{a}'", firstTx);

        // 2. oturum paralel başlar; ağaç kilidinde BEKLER.
        var secondTask = Task.Run(async () =>
        {
            var tx = await second.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
            try
            {
                await ExecAsync(second, $"SELECT {TreeLock}", tx);
                await ExecAsync(second, $"SELECT 1 FROM categories WHERE id = '{b}' FOR UPDATE", tx);
                await ExecAsync(second, $"UPDATE categories SET parent_id = '{a}' WHERE id = '{b}'", tx);
                await tx.CommitAsync(Ct);
                return new SwapResult(true, null, null);
            }
            catch (PostgresException exception)
            {
                await tx.RollbackAsync(Ct);
                return new SwapResult(false, exception.SqlState, exception.ConstraintName);
            }
        }, Ct);

        await WaitUntilBlockedOnLockAsync();
        Assert.False(secondTask.IsCompleted, "İkinci oturum ağaç kilidini beklemeli.");

        await firstTx.CommitAsync(Ct);
        var result = await secondTask;

        Assert.False(result.Committed);
        Assert.Equal("23514", result.SqlState);
        Assert.Equal("ck_categories_no_cycle", result.Constraint);
        Assert.Equal(b, await Db.ScalarAsync<Guid>($"SELECT parent_id FROM categories WHERE id = '{a}'", Ct));
        Assert.Null(await Db.ScalarAsync<Guid?>($"SELECT parent_id FROM categories WHERE id = '{b}'", Ct));
        Assert.Equal(0L, await CycleCountAsync(a, b));
    }

    [Fact]
    public async Task Repeated_parallel_swaps_never_commit_both_and_never_deadlock()
    {
        for (var round = 0; round < 12; round++)
        {
            var a = await InsertCategoryAsync();
            var b = await InsertCategoryAsync();
            using var barrier = new Barrier(2);

            async Task<(bool Committed, string? SqlState)> SwapAsync(Guid self, Guid newParent)
            {
                await using var session = NewSession();
                await session.OpenAsync(Ct);
                barrier.SignalAndWait(Ct);
                var tx = await session.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
                try
                {
                    await ExecAsync(session, $"SELECT {TreeLock}", tx);
                    await ExecAsync(session, $"SELECT 1 FROM categories WHERE id = '{self}' FOR UPDATE", tx);
                    await ExecAsync(session, $"UPDATE categories SET parent_id = '{newParent}' WHERE id = '{self}'", tx);
                    await tx.CommitAsync(Ct);
                    return (true, null);
                }
                catch (PostgresException exception)
                {
                    await tx.RollbackAsync(Ct);
                    return (false, exception.SqlState);
                }
            }

            var results = await Task.WhenAll(Task.Run(() => SwapAsync(a, b), Ct), Task.Run(() => SwapAsync(b, a), Ct));

            Assert.Equal(1, results.Count(r => r.Committed));
            Assert.Equal("23514", results.Single(r => !r.Committed).SqlState); // 40P01 (deadlock) değil
            Assert.Equal(0L, await CycleCountAsync(a, b));
        }
    }

    [Fact]
    public async Task Taking_the_row_lock_before_the_tree_lock_deadlocks_which_is_why_the_contract_fixes_the_order()
    {
        // Yanlış sıra: 2. oturum b satırını FOR UPDATE ile tutup ağaç kilidini ister; 1. oturum ağaç kilidini tutar ve FK kontrolü b için
        // 'FOR KEY SHARE' ister (FOR UPDATE ile çakışır). Karşılıklı bekleme -> PostgreSQL 40P01 ile birini keser.
        var a = await InsertCategoryAsync();
        var b = await InsertCategoryAsync();
        await using var first = NewSession();
        await using var second = NewSession();
        await first.OpenAsync(Ct);
        await second.OpenAsync(Ct);

        var secondTx = await second.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        await ExecAsync(second, $"SELECT 1 FROM categories WHERE id = '{b}' FOR UPDATE", secondTx);

        var firstTask = Task.Run(async () =>
        {
            var tx = await first.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
            try
            {
                await ExecAsync(first, $"SELECT {TreeLock}", tx);
                await ExecAsync(first, $"UPDATE categories SET parent_id = '{b}' WHERE id = '{a}'", tx); // FK: b için KEY SHARE bekler
                await tx.CommitAsync(Ct);
                return (string?)null;
            }
            catch (PostgresException exception)
            {
                await tx.RollbackAsync(Ct);
                return exception.SqlState;
            }
        }, Ct);

        await WaitUntilBlockedOnLockAsync();

        string? secondState = null;
        try
        {
            await ExecAsync(second, $"SELECT {TreeLock}", secondTx); // 1. oturumun kilidini bekler -> kilitlenme döngüsü
            await secondTx.CommitAsync(Ct);
        }
        catch (PostgresException exception)
        {
            secondState = exception.SqlState;
            await secondTx.RollbackAsync(Ct);
        }

        var firstState = await firstTask;
        Assert.Contains("40P01", new[] { firstState, secondState });
    }

    // ---- yardımcılar ----

    private async Task<long> CycleCountAsync(Guid a, Guid b) =>
        await Db.ScalarAsync<long>(
            $"SELECT count(*) FROM categories x JOIN categories y ON y.id = x.parent_id AND y.parent_id = x.id WHERE x.id IN ('{a}', '{b}')", Ct);

    /// <summary>Başka bir oturum bu veritabanında bir kilit beklemeye başlayana dek yoklar (en çok ~10 sn).</summary>
    private async Task WaitUntilBlockedOnLockAsync()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await Db.ScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()", Ct);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("Hiçbir oturum kilit beklemeye geçmedi.");
    }
}

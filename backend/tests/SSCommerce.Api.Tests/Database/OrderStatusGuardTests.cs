namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 8 <c>trg_orders_status_transition</c> ve <c>trg_orders_insert_placed</c> (docs/CONTRACT_PHASE_8.md "Persistence" 4, D-067 madde 2).
/// Trigger yalnız geçiş TABLOSUNU zorlar; rol yetkisi <c>OrderStateMachine</c>'dedir. Bilinmeyen durum metni trigger'a değil ck_orders_status'a bırakılır.
/// SqlState: 23514 check_violation (CONSTRAINT adıyla), 23001 değişmezlik trigger'ı.
/// </summary>
public class OrderStatusGuardTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private static readonly string[] Statuses = ["placed", "processing", "shipped", "completed", "cancelled"];

    private static readonly HashSet<(string From, string To)> Legal =
    [
        ("placed", "processing"), ("placed", "cancelled"), ("processing", "shipped"), ("processing", "cancelled"), ("shipped", "completed"),
    ];

    private PostgresTestDatabase Db => fixture.Database;

    private OrderingTestData Data => new(Db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> LegalTransitions()
    {
        var data = new TheoryData<string, string>();
        foreach (var (from, to) in Legal)
        {
            data.Add(from, to);
        }

        return data;
    }

    public static TheoryData<string, string> IllegalTransitions()
    {
        var data = new TheoryData<string, string>();
        foreach (var from in Statuses)
        {
            foreach (var to in Statuses)
            {
                if (from != to && !Legal.Contains((from, to)))
                {
                    data.Add(from, to);
                }
            }
        }

        return data;
    }

    /// <summary>Yeni siparişi YASAL yoldan istenen duruma getirir (INSERT 'placed' ile başlar).</summary>
    private async Task<Guid> OrderAtAsync(string status)
    {
        var orderId = await Data.InsertOrderAsync(await Data.InsertUserAsync());
        string[] path = status switch
        {
            "placed" => [],
            "processing" => ["processing"],
            "shipped" => ["processing", "shipped"],
            "completed" => ["processing", "shipped", "completed"],
            "cancelled" => ["cancelled"],
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        foreach (var step in path)
        {
            await Db.ExecuteAsync($"UPDATE orders SET status = '{step}' WHERE id = '{orderId}'", Ct);
        }

        Assert.Equal(status, await Db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = '{orderId}'", Ct));
        return orderId;
    }

    [Fact]
    public void The_theory_data_covers_five_legal_and_fifteen_illegal_pairs()
    {
        Assert.Equal(5, LegalTransitions().Count);
        Assert.Equal(15, IllegalTransitions().Count);
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public async Task The_five_legal_transitions_pass(string from, string to)
    {
        var orderId = await OrderAtAsync(from);

        await Db.ExecuteAsync($"UPDATE orders SET status = '{to}', updated_at = now() WHERE id = '{orderId}'", Ct);

        Assert.Equal(to, await Db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = '{orderId}'", Ct));
    }

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public async Task Every_other_transition_is_rejected_and_the_row_is_unchanged(string from, string to)
    {
        var orderId = await OrderAtAsync(from);

        await Data.AssertViolationAsync("23514", $"UPDATE orders SET status = '{to}' WHERE id = '{orderId}'", "ck_orders_status_transition");

        Assert.Equal(from, await Db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = '{orderId}'", Ct));
    }

    [Theory]
    [InlineData("completed", "placed")]
    [InlineData("cancelled", "placed")]
    [InlineData("placed", "shipped")]
    [InlineData("shipped", "cancelled")]
    public async Task The_contract_named_illegal_cases_are_rejected(string from, string to)
    {
        var orderId = await OrderAtAsync(from);

        await Data.AssertViolationAsync("23514", $"UPDATE orders SET status = '{to}' WHERE id = '{orderId}'", "ck_orders_status_transition");
    }

    [Fact]
    public async Task Assigning_the_same_status_is_not_a_transition()
    {
        foreach (var status in Statuses)
        {
            var orderId = await OrderAtAsync(status);

            await Db.ExecuteAsync($"UPDATE orders SET status = '{status}' WHERE id = '{orderId}'", Ct);
            await Db.ExecuteAsync($"UPDATE orders SET status = status, updated_at = now() + interval '1 second' WHERE id = '{orderId}'", Ct);

            Assert.Equal(status, await Db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = '{orderId}'", Ct));
        }
    }

    [Theory]
    [InlineData("placed", "'paid'")]
    [InlineData("completed", "'Placed'")]
    [InlineData("cancelled", "''")]
    public async Task An_unknown_target_status_is_left_to_the_existing_status_check(string from, string to)
    {
        var orderId = await OrderAtAsync(from);

        await Data.AssertViolationAsync("23514", $"UPDATE orders SET status = {to} WHERE id = '{orderId}'", "ck_orders_status");
    }

    [Fact]
    public async Task A_null_status_is_a_not_null_violation_not_a_transition_error()
    {
        var orderId = await OrderAtAsync("placed");

        await Data.AssertViolationAsync("23502", $"UPDATE orders SET status = NULL WHERE id = '{orderId}'");
    }

    [Theory]
    [InlineData("processing")]
    [InlineData("shipped")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    public async Task A_new_order_with_a_status_other_than_placed_is_rejected(string status)
    {
        var userId = await Data.InsertUserAsync();
        var sql = OrderingTestData.OrderSql(Guid.NewGuid(), userId, new Dictionary<string, string> { ["status"] = $"'{status}'" });

        await Data.AssertViolationAsync("23514", sql, "ck_orders_insert_status");
    }

    [Theory]
    [InlineData("'paid'")]
    [InlineData("'Placed'")]
    [InlineData("''")]
    public async Task A_new_order_with_an_unknown_status_still_hits_the_status_check(string status)
    {
        var userId = await Data.InsertUserAsync();
        var sql = OrderingTestData.OrderSql(Guid.NewGuid(), userId, new Dictionary<string, string> { ["status"] = status });

        await Data.AssertViolationAsync("23514", sql, "ck_orders_status");
    }

    [Fact]
    public async Task A_new_placed_order_is_accepted()
    {
        var userId = await Data.InsertUserAsync();

        var orderId = await Data.InsertOrderAsync(userId);

        Assert.Equal("placed", await Db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = '{orderId}'", Ct));
    }

    [Fact]
    public async Task The_snapshot_guard_fires_before_the_transition_guard()
    {
        // trg_orders_restrict_update < trg_orders_status_transition (alfabetik): yasadışı geçişle birlikte anlık görüntü değişimi 23001 verir.
        var orderId = await OrderAtAsync("placed");

        await Data.AssertViolationAsync("23001", $"UPDATE orders SET status = 'completed', item_subtotal = item_subtotal + 1 WHERE id = '{orderId}'");
    }

    [Fact]
    public async Task The_triggers_have_the_contract_shape()
    {
        var update = (await Db.ScalarAsync<string>("SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'trg_orders_status_transition'", Ct))!;
        Assert.Contains("BEFORE UPDATE OF status ON public.orders", update, StringComparison.Ordinal);
        Assert.Contains("FOR EACH ROW", update, StringComparison.Ordinal);
        Assert.Contains("WHEN (", update, StringComparison.Ordinal);
        Assert.Contains("IS DISTINCT FROM", update, StringComparison.Ordinal);

        var insert = (await Db.ScalarAsync<string>("SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'trg_orders_insert_placed'", Ct))!;
        Assert.Contains("BEFORE INSERT ON public.orders", insert, StringComparison.Ordinal);
        Assert.Contains("FOR EACH ROW", insert, StringComparison.Ordinal);

        // Hepsi 'O' (origin) kipinde etkin.
        Assert.Equal("O", await Db.ScalarAsync<string>(
            "SELECT string_agg(DISTINCT tgenabled::text, ',') FROM pg_trigger WHERE tgname IN ('trg_orders_status_transition', 'trg_orders_insert_placed')", Ct));
    }

    [Fact]
    public async Task The_existing_order_triggers_keep_working_after_the_new_ones()
    {
        var placed = await Data.InsertFullOrderAsync();

        await Data.AssertViolationAsync("23001", $"DELETE FROM orders WHERE id = '{placed.OrderId}'");
        await Data.AssertViolationAsync("23001", "TRUNCATE orders CASCADE");
        await Data.AssertViolationAsync("23001", $"UPDATE orders SET delivery_label = 'Başka' WHERE id = '{placed.OrderId}'");
    }
}

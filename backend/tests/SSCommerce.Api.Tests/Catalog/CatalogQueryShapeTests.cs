using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SSCommerce.Api.Tests.Catalog;

/// <summary>
/// Sorgu SAYISI ve biçimi: "N+1 yok", "kullanıcı metni SQL'e gömülmüyor". EF Core'un her çalıştırılan komut için yazdığı
/// log olayı (CommandExecuted, EventId 20101) sayılır. Duyarlı veri günlüğü kapalıdır: parametre DEĞERLERİ log'da görünmez,
/// yalnız SQL metni görünür; bu yüzden "SQL metninde kullanıcı girdisi yok" kontrolü anlamlıdır.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class CatalogQueryShapeTests(CatalogApiFixture fixture)
{
    internal const int CommandExecuted = 20101;
    internal const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    internal sealed class CommandLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            categoryName == EfCommandCategory ? new Capture(Commands) : new Silent();

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == CommandExecuted)
                {
                    sink.Enqueue(formatter(state, exception));
                }
            }
        }

        private sealed class Silent : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }
    }

    private async Task<string[]> ExecutedCommandsAsync(string url)
    {
        var log = new CommandLog();
        await using var factory = ApiFactory.Create(
            "Production",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(log);
                services.AddLogging(logging => logging.AddFilter(EfCommandCategory, LogLevel.Information));
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return [.. log.Commands];
    }

    [Fact]
    public async Task Categories_use_a_single_query()
    {
        var commands = await ExecutedCommandsAsync("/api/v1/categories");

        Assert.Single(commands);
    }

    [Fact]
    public async Task Product_list_uses_two_queries_regardless_of_page_size_no_n_plus_one()
    {
        // 1 sayım + 1 sayfa (ana görsel ilişkili alt sorgu olarak aynı sorguda). 9 ürün de olsa 1 de olsa sayı aynı.
        var many = await ExecutedCommandsAsync("/api/v1/products?pageSize=50");
        var one = await ExecutedCommandsAsync("/api/v1/products?pageSize=1");

        Assert.Equal(2, many.Length);
        Assert.Equal(2, one.Length);
    }

    [Fact]
    public async Task Product_list_with_category_adds_exactly_one_lookup_query()
    {
        var commands = await ExecutedCommandsAsync("/api/v1/products?category=fx-elektronik&pageSize=50");

        Assert.Equal(3, commands.Length); // kategori çözümleme + sayım + sayfa
    }

    [Fact]
    public async Task Product_detail_uses_three_split_queries_independent_of_variant_and_image_count()
    {
        var rich = await ExecutedCommandsAsync("/api/v1/products/fx-telefon-a");   // 3 varyant, 2 görsel
        var plain = await ExecutedCommandsAsync("/api/v1/products/fx-telefon-b");  // 1 varyant, 0 görsel

        Assert.Equal(3, rich.Length); // ürün + varyantlar + görseller
        Assert.Equal(3, plain.Length);
    }

    [Fact]
    public async Task Search_text_is_passed_as_a_parameter_never_inlined_into_sql()
    {
        const string hostile = "zzz'; DROP TABLE products; --";
        var commands = await ExecutedCommandsAsync($"/api/v1/products?q={Uri.EscapeDataString(hostile)}");

        Assert.NotEmpty(commands);
        Assert.All(commands, sql =>
        {
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("zzz", sql, StringComparison.Ordinal);
        });
        Assert.Contains(commands, sql => sql.Contains("ILIKE", StringComparison.Ordinal) && sql.Contains("ESCAPE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sort_choice_never_comes_from_user_text_into_sql()
    {
        // Allowlist: sort anahtarı enum'a çevrilir; ORDER BY metni sabit kodlardan gelir.
        var commands = await ExecutedCommandsAsync("/api/v1/products?sort=price_desc");

        Assert.All(commands, sql => Assert.DoesNotContain("price_desc", sql, StringComparison.Ordinal));
        Assert.Contains(commands, sql => sql.Contains("ORDER BY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_queries_do_not_select_heavy_columns_description_or_specifications()
    {
        var commands = await ExecutedCommandsAsync("/api/v1/products?pageSize=50");

        // Liste projection ile yalnız gereken sütunları çeker (varlık yüklenmez).
        Assert.All(commands, sql =>
        {
            Assert.DoesNotContain("description", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("specifications", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("option_signature", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credit_key", sql, StringComparison.OrdinalIgnoreCase);
        });
    }
}

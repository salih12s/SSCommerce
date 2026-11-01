using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>Test içinde açılıp kapatılan hata enjeksiyonu: Identity'nin iyimser (ConcurrencyStamp'li) kullanıcı güncellemesi hep çatışır.</summary>
internal sealed class StoreFaults
{
    public volatile bool FailUserUpdates;
}

/// <summary>
/// <c>UserStore.UpdateAsync</c>'i "ConcurrencyFailure" döndürmeye zorlar. Gerçek yarış (aynı satıra eşzamanlı yazım) bu sonucu
/// üretir; burada yarış DETERMİNİSTİK olarak taklit edilir. Güvenlik açısından önemli işlemler (çıkışta oturum iptali,
/// başarısız giriş sayacı) iyimser güncellemeye bağımlı olmamalıdır.
/// </summary>
internal sealed class FaultInjectingUserStore(SSCommerceDbContext context, StoreFaults faults, IdentityErrorDescriber describer)
    : UserStore<AppUser, IdentityRole<Guid>, SSCommerceDbContext, Guid>(context, describer)
{
    public override Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken cancellationToken = default) =>
        faults.FailUserUpdates
            ? Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure()))
            : base.UpdateAsync(user, cancellationToken);
}

/// <summary>Parola hash'leme/doğrulama çağrılarını sayar: giriş yanıt süresi hesap durumuna göre değişmemeli.</summary>
internal sealed class CountingPasswordHasher : IPasswordHasher<AppUser>
{
    private readonly PasswordHasher<AppUser> _inner = new();

    public int Calls;

    public string HashPassword(AppUser user, string password)
    {
        Interlocked.Increment(ref Calls);
        return _inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref Calls);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

/// <summary>QA düzeltme turu: logout iptali, atomik kilitleme ve zamanlama eşitliği. Her test kendi uygulama örneğini açar.</summary>
[Collection(AuthApiCollection.Name)]
public class SessionAndLockoutHardeningTests(AuthApiFixture fixture)
{
    private WebApplicationFactory<Program> NewFactory(StoreFaults? faults = null, CountingPasswordHasher? hasher = null, Action<IServiceCollection>? more = null) =>
        ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services =>
            {
                if (faults is not null)
                {
                    services.AddSingleton(faults);
                    services.RemoveAll<IUserStore<AppUser>>();
                    services.AddScoped<IUserStore<AppUser>, FaultInjectingUserStore>();
                }

                if (hasher is not null)
                {
                    services.RemoveAll<IPasswordHasher<AppUser>>();
                    services.AddSingleton<IPasswordHasher<AppUser>>(hasher);
                }

                more?.Invoke(services);
            },
            AuthApiFixture.HighLimits);

    // ============ 1) Logout: sunucu tarafı iptal iyimser güncellemeye bağlı değil ============

    [Fact]
    public async Task Logout_revokes_the_old_cookie_even_when_optimistic_user_updates_conflict()
    {
        var faults = new StoreFaults();
        await using var factory = NewFactory(faults);
        var session = fixture.NewSession(factory);
        await session.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
        var stolen = session.CloneWithCookies();
        faults.FailUserUpdates = true; // ConcurrencyFailure: Identity'nin UpdateSecurityStampAsync'i sessizce başarısız olurdu

        var logout = await session.LogoutAsync();

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        faults.FailUserUpdates = false;
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/api/v1/auth/me")).StatusCode); // çalınmış cookie tekrar oynatılamaz
    }

    [Fact]
    public async Task Logout_racing_with_concurrent_writes_to_the_same_user_row_still_revokes()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var victim = fixture.Dev();
        await victim.LoginAsync(email, password);
        var stolen = victim.CloneWithCookies();

        // Aynı kullanıcı satırına eşzamanlı yazımlar: başka cihazlardan yanlış parolalar (AccessFailedCount/ConcurrencyStamp yazar).
        var noise = Enumerable.Range(0, 8).Select(async _ =>
        {
            var other = fixture.Dev();
            await other.LoginAsync(email, "Yanlis-parola-1");
        }).ToArray();
        var logout = victim.LogoutAsync();
        await Task.WhenAll(noise.Append(logout));

        Assert.Equal(HttpStatusCode.NoContent, (await logout).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // ============ 2) Kilitleme: başarısız deneme sayacı atomik ============

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_when_optimistic_user_updates_conflict()
    {
        var faults = new StoreFaults();
        await using var factory = NewFactory(faults);
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.NewSession(factory).RegisterAsync(email, password);
        faults.FailUserUpdates = true;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.NewSession(factory).LoginAsync(email, "Yanlis-parola-1")).StatusCode);
        }

        faults.FailUserUpdates = false;
        var correct = await fixture.NewSession(factory).LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode); // kilit oluştu: doğru parola da reddedilir
        Assert.True(await fixture.Database.ScalarAsync<bool>(
            $"SELECT \"LockoutEnd\" > now() FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Twenty_parallel_wrong_passwords_lock_the_account_and_the_correct_password_is_then_rejected()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            (await fixture.Dev().LoginAsync(email, "Yanlis-parola-1")).StatusCode));

        Assert.All(results, status => Assert.Equal(HttpStatusCode.Unauthorized, status));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Dev().LoginAsync(email, password)).StatusCode);
        Assert.True(await fixture.Database.ScalarAsync<bool>(
            $"SELECT \"LockoutEnd\" > now() FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_failures_are_all_counted_none_are_swallowed()
    {
        // 4 paralel yanlış deneme (< eşik 5): sayaç TAM 4 olmalı. Çatışmada yutulan artışlar sayacı düşük bırakırdı.
        var email = AuthApiFixture.NewEmail();
        await fixture.Dev().RegisterAsync(email, AuthApiFixture.NewPassword());

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Dev().LoginAsync(email, "Yanlis-parola-1")));

        Assert.Equal(4, await fixture.Database.ScalarAsync<long>(
            $"SELECT \"AccessFailedCount\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_login_outcome_costs_exactly_one_password_hash_operation_so_timing_does_not_leak_account_state()
    {
        var hasher = new CountingPasswordHasher();
        await using var factory = NewFactory(hasher: hasher);
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.NewSession(factory).RegisterAsync(email, password);
        var lockedEmail = AuthApiFixture.NewEmail();
        var lockedPassword = AuthApiFixture.NewPassword();
        await fixture.NewSession(factory).RegisterAsync(lockedEmail, lockedPassword);
        for (var i = 0; i < 5; i++)
        {
            await fixture.NewSession(factory).LoginAsync(lockedEmail, "Yanlis-parola-1");
        }

        async Task<int> CostOf(string e, string p)
        {
            var session = fixture.NewSession(factory);
            var token = await session.FetchCsrfAsync();
            var before = hasher.Calls;
            await session.PostJsonAsync("/api/v1/auth/login", new { email = e, password = p }, token);
            return hasher.Calls - before;
        }

        Assert.Equal(1, await CostOf(email, password));                       // doğru parola
        Assert.Equal(1, await CostOf(email, "Yanlis-parola-1"));              // yanlış parola
        Assert.Equal(1, await CostOf(AuthApiFixture.NewEmail(), "Yanlis-parola-1")); // olmayan hesap
        Assert.Equal(1, await CostOf(lockedEmail, lockedPassword));           // kilitli hesap (doğru parolayla bile)
        Assert.Equal(1, await CostOf(lockedEmail, "Yanlis-parola-1"));        // kilitli hesap, yanlış parola
    }

    [Fact]
    public async Task A_locked_account_does_not_accumulate_more_failures_and_unlock_time_is_not_extended_by_rejected_attempts()
    {
        var email = AuthApiFixture.NewEmail();
        await fixture.Dev().RegisterAsync(email, AuthApiFixture.NewPassword());
        for (var i = 0; i < 5; i++)
        {
            await fixture.Dev().LoginAsync(email, "Yanlis-parola-1");
        }

        var ct = TestContext.Current.CancellationToken;
        var before = await fixture.Database.ScalarAsync<string>($"SELECT \"LockoutEnd\"::text || '|' || \"AccessFailedCount\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", ct);
        for (var i = 0; i < 3; i++)
        {
            await fixture.Dev().LoginAsync(email, "Yanlis-parola-1"); // kilitliyken gelen denemeler
        }

        Assert.Equal(before, await fixture.Database.ScalarAsync<string>($"SELECT \"LockoutEnd\"::text || '|' || \"AccessFailedCount\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", ct));
    }

    // ============ 3) DataProtection anahtarları ============

    private static string NewKeysDirectory() => Path.Combine(Path.GetTempPath(), "ssc-dp-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> ProductionWithKeys(string? keysPath, Action<IServiceCollection>? more = null) =>
        ApiFactory.Create(
            "Production",
            fixture.Database.ConnectionString,
            more,
            new Dictionary<string, string?>(AuthApiFixture.HighLimits) { ["DataProtection:KeysPath"] = keysPath });

    [Fact]
    public async Task Data_protection_keys_are_written_to_the_configured_directory_and_survive_a_restart()
    {
        var directory = NewKeysDirectory();
        try
        {
            string cookieName;
            string cookieValue;
            await using (var first = ProductionWithKeys(directory))
            {
                var session = fixture.NewSession(first);
                await session.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
                cookieName = "__Host-ssc.auth";
                cookieValue = session.Cookies[cookieName];
                Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
            }

            Assert.True(Directory.Exists(directory), "Anahtar dizini oluşturulmalı.");
            Assert.NotEmpty(Directory.GetFiles(directory, "key-*.xml"));

            // "Yeniden başlatma": aynı dizinle YENİ uygulama örneği, eski cookie'yi kabul eder.
            await using var second = ProductionWithKeys(directory);
            var restarted = fixture.NewSession(second);
            restarted.Cookies[cookieName] = cookieValue;
            Assert.Equal(HttpStatusCode.OK, (await restarted.GetAsync("/api/v1/auth/me")).StatusCode);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class WarningLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(Warnings);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                warnings.Enqueue(formatter(state, exception));
        }
    }

    [Fact]
    public async Task Production_without_a_keys_directory_logs_a_warning_but_starts()
    {
        var log = new WarningLog();
        await using var factory = ProductionWithKeys(null, s => s.AddSingleton<ILoggerProvider>(log));

        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // uygulama düşmez
        Assert.Contains(log.Warnings, w => w.Contains("DataProtection:KeysPath", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_with_a_keys_directory_and_Development_without_one_do_not_warn()
    {
        var directory = NewKeysDirectory();
        try
        {
            var configured = new WarningLog();
            await using (var factory = ProductionWithKeys(directory, s => s.AddSingleton<ILoggerProvider>(configured)))
            {
                await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
            }

            var development = new WarningLog();
            await using (var factory = NewFactory(more: s => s.AddSingleton<ILoggerProvider>(development)))
            {
                await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
            }

            Assert.DoesNotContain(configured.Warnings, w => w.Contains("DataProtection", StringComparison.Ordinal));
            Assert.DoesNotContain(development.Warnings, w => w.Contains("DataProtection", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

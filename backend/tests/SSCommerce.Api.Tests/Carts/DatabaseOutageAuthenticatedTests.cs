using System.Net;
using System.Net.Sockets;
using Npgsql;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>
/// ÇALIŞAN uygulamada, GİRİŞ YAPILDIKTAN SONRA veritabanı erişilemez olursa: cookie doğrulaması (security stamp, her kimlikli istekte DB'ye gider) ve
/// iş uçları 500 değil 503 service_unavailable döner; bağlantı gelince AYNI cookie yeniden çalışır (oturum kaybolmaz, 401 olmaz).
/// Kesinti, gerçek test PostgreSQL'ine giden küçük bir işlem-içi TCP yönlendiricisinin kapatılmasıyla simüle edilir; hiçbir sunucu/konteyner durdurulmaz.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class DatabaseOutageAuthenticatedTests(InventoryApiFixture fixture)
{
    /// <summary>127.0.0.1'de bir portu dinler ve gerçek PostgreSQL'e aktarır. <see cref="Close"/> dinlemeyi bırakır ve açık bağlantıları koparır; <see cref="Resume"/> AYNI portu yeniden açar.</summary>
    internal sealed class TcpForwarder : IDisposable
    {
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly List<TcpClient> _open = [];
        private readonly object _gate = new();
        private TcpListener? _listener;

        public TcpForwarder(string targetHost, int targetPort)
        {
            _targetHost = targetHost;
            _targetPort = targetPort;
            var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            Port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Resume();
        }

        public int Port { get; }

        public void Resume()
        {
            lock (_gate)
            {
                if (_listener is not null)
                {
                    return;
                }

                _listener = new TcpListener(System.Net.IPAddress.Loopback, Port);
                _listener.Start();
                _ = AcceptLoopAsync(_listener);
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _listener?.Stop();
                _listener = null;
                foreach (var client in _open)
                {
                    client.Close();
                }

                _open.Clear();
            }
        }

        public void Dispose() => Close();

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return; // dinleyici kapatıldı
                }

                _ = PipeAsync(client);
            }
        }

        private async Task PipeAsync(TcpClient client)
        {
            var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(_targetHost, _targetPort);
                lock (_gate)
                {
                    _open.Add(client);
                    _open.Add(upstream);
                }

                var a = client.GetStream().CopyToAsync(upstream.GetStream());
                var b = upstream.GetStream().CopyToAsync(client.GetStream());
                await Task.WhenAny(a, b);
            }
            catch (Exception)
            {
                // kopan bağlantı: iki ucu da kapat
            }
            finally
            {
                client.Close();
                upstream.Close();
            }
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task A_logged_in_user_gets_503_while_the_database_is_unreachable_and_the_same_cookie_works_again_when_it_returns(string environment)
    {
        var real = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString);
        using var forwarder = new TcpForwarder(real.Host!, real.Port);
        var viaForwarder = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString) { Host = "127.0.0.1", Port = forwarder.Port, Timeout = 2 };
        await using var factory = ApiFactory.Create(
            environment,
            viaForwarder.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { ["Security:AllowedOrigins:0"] = InventoryApiFixture.ProductionOrigin });
        var (variant, _, _) = await fixture.StockedVariantAsync(10);

        // 1) Bağlantı varken: giriş yapılır, kimlikli istekler çalışır.
        var customer = await fixture.LoginAsync(factory, AppRoles.Customer);
        Assert.Equal(HttpStatusCode.OK, (await customer.AddToCartAsync(variant, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetCartAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/v1/auth/me")).StatusCode);

        // 2) Kesinti: yönlendirici kapanır (açık havuz bağlantıları kopar, yeni bağlantı reddedilir). Aynı çalışan uygulama, aynı cookie.
        forwarder.Close();
        for (var round = 0; round < 2; round++)
        {
            await DatabaseUnavailableTests.AssertServiceUnavailableAsync(await customer.GetCartAsync());
            await DatabaseUnavailableTests.AssertServiceUnavailableAsync(await customer.GetAsync("/api/v1/auth/me"));
            await DatabaseUnavailableTests.AssertServiceUnavailableAsync(await customer.AddToCartAsync(variant, 1)); // CSRF'li yazma
        }

        Assert.Equal(1, await fixture.LineQuantityAsync(customer.UserId, variant)); // kesintide hiçbir şey yazılmadı

        // 3) Bağlantı geri gelir. Kopmuş havuz bağlantıları ilk kullanımda atılır: o sırada yalnız 503 görülebilir (asla 500/401), sonra normale döner.
        forwarder.Resume();
        await EventuallyOkAsync(() => customer.GetCartAsync());
        await EventuallyOkAsync(() => customer.GetAsync("/api/v1/auth/me"));
        var write = await EventuallyOkAsync(() => customer.AddToCartAsync(variant, 1));

        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal(2, await fixture.LineQuantityAsync(customer.UserId, variant)); // aynı oturum, yazma gerçekten uygulandı
    }

    /// <summary>Yeniden bağlanırken kopuk bağlantılar yüzünden birkaç deneme 503 dönebilir; hiçbir deneme 500 ya da 401 olmamalı ve sonunda 200 gelmeli.</summary>
    private static async Task<HttpResponseMessage> EventuallyOkAsync(Func<Task<HttpResponseMessage>> request)
    {
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            last = await request();
            if (last.StatusCode == HttpStatusCode.OK)
            {
                return last;
            }

            await DatabaseUnavailableTests.AssertServiceUnavailableAsync(last); // 500 ya da 401 burada kırmızı olur
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Bağlantı geri geldikten sonra istek hâlâ başarılı olmadı.");
        return last!;
    }
}

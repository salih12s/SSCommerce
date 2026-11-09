using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Addresses;

/// <summary>
/// ADRES YARIŞ testleri: gerçek paralel HTTP, gerçek PostgreSQL. Kullanıcı başına iki argümanlı advisory lock; kilit olmasa
/// sınır denetimi (SELECT count) ve varsayılan taşıma birbirini ezerdi (11. adres, iki varsayılan ya da unique ihlali 500'ü).
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class AddressConcurrencyTests(InventoryApiFixture fixture)
{
    private static async Task<List<(HttpStatusCode Status, JsonDocument Body)>> RunParallelAsync(int count, Func<int, Task<HttpResponseMessage>> send)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.Task;
            return await send(i);
        }).ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(tasks);
        var results = new List<(HttpStatusCode, JsonDocument)>();
        foreach (var response in responses)
        {
            var text = await response.TextAsync();
            results.Add((response.StatusCode, JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text))); // 204 gövdesizdir
        }

        return results;
    }

    [Fact]
    public async Task Parallel_first_addresses_produce_exactly_one_default()
    {
        var customer = await fixture.CustomerAsync();

        var results = await RunParallelAsync(8, i => customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress($"A{i}")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Equal(8, await fixture.AddressCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
        Assert.Equal(1, results.Count(r => r.Body.RootElement.GetProperty("isDefault").GetBoolean()));
    }

    [Fact]
    public async Task Parallel_creates_cannot_exceed_ten_addresses()
    {
        var customer = await fixture.CustomerAsync();

        var results = await RunParallelAsync(25, i => customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress($"A{i}")));

        Assert.Equal(10, results.Count(r => r.Status == HttpStatusCode.Created));
        var rejected = results.Where(r => r.Status != HttpStatusCode.Created).ToList();
        Assert.Equal(15, rejected.Count);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("address_limit", r.Body.RootElement.GetProperty("code").GetString());
        });
        Assert.Equal(10, await fixture.AddressCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Parallel_set_default_calls_leave_exactly_one_default_and_never_fail()
    {
        var customer = await fixture.CustomerAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await customer.CreateAddressAsync($"A{i}"));
        }

        var results = await RunParallelAsync(24, i => customer.PostAsync($"{CartKit.AddressesUrl}/{ids[i % 6]}/default", null));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));   // unique indeks ihlali (500) yok
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
        Assert.Equal(6, await fixture.AddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Parallel_deletes_updates_and_default_moves_keep_the_invariants()
    {
        var customer = await fixture.CustomerAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add(await customer.CreateAddressAsync($"A{i}"));
        }

        var results = await RunParallelAsync(24, i => (i % 3) switch
        {
            0 => customer.DeleteAsync($"{CartKit.AddressesUrl}/{ids[i % 8]}"),
            1 => customer.PostAsync($"{CartKit.AddressesUrl}/{ids[(i + 1) % 8]}/default", null),
            _ => customer.PutAsync($"{CartKit.AddressesUrl}/{ids[(i + 2) % 8]}", CartKit.ValidAddress($"U{i}")),
        });

        Assert.All(results, r => Assert.True(
            r.Status is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound, $"beklenmeyen durum: {r.Status}"));
        var remaining = await fixture.AddressCountAsync(customer.UserId);
        // Kalan adres varsa tam bir varsayılan olmalı; hiç kalmadıysa sıfır.
        Assert.Equal(remaining > 0 ? 1 : 0, await fixture.DefaultAddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Address_writes_of_different_users_are_independent()
    {
        var users = new List<ApiClient>();
        for (var i = 0; i < 6; i++)
        {
            users.Add(await fixture.CustomerAsync());
        }

        var results = await RunParallelAsync(6 * 4, i => users[i % 6].PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress($"A{i}")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        foreach (var user in users)
        {
            Assert.Equal(4, await fixture.AddressCountAsync(user.UserId));
            Assert.Equal(1, await fixture.DefaultAddressCountAsync(user.UserId));
        }
    }
}

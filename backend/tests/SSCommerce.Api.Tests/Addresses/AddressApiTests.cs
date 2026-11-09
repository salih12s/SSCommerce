using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Addresses;

/// <summary>Adres uçlarının sözleşmesi: CRUD, varsayılan kuralları, limit, doğrulama, telefon normalizasyonu, XSS'in değiştirilmeden saklanması.</summary>
[Collection(InventoryApiCollection.Name)]
public class AddressApiTests(InventoryApiFixture fixture)
{
    private static async Task<JsonDocument> ListAsync(ApiClient client) => await (await client.GetAsync(CartKit.AddressesUrl)).JsonAsync();

    private static Guid[] Ids(JsonDocument list) => list.RootElement.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task The_first_address_becomes_the_default_and_the_response_has_the_documented_shape()
    {
        var customer = await fixture.CustomerAsync();

        var response = await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress("Ev"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal(
            new[] { "district", "id", "isDefault", "label", "phone", "postalCode", "province", "recipientName", "streetAddress" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(json.RootElement.GetProperty("isDefault").GetBoolean());
        Assert.Equal("5321234567", json.RootElement.GetProperty("phone").GetString());
        Assert.Equal("İstanbul", json.RootElement.GetProperty("province").GetString());

        var second = await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress("İş"));
        using var secondJson = await second.JsonAsync();
        Assert.False(secondJson.RootElement.GetProperty("isDefault").GetBoolean());          // ikinci adres varsayılan olmaz
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task The_list_puts_the_default_first_and_only_returns_the_callers_addresses()
    {
        var customer = await fixture.CustomerAsync();
        var other = await fixture.CustomerAsync();
        var first = await customer.CreateAddressAsync("Ev");
        var second = await customer.CreateAddressAsync("İş");
        var third = await customer.CreateAddressAsync("Yazlık");
        await other.CreateAddressAsync("Başkası");
        await customer.PostAsync($"{CartKit.AddressesUrl}/{third}/default", null);

        using var list = await ListAsync(customer);

        var ids = Ids(list);
        Assert.Equal(3, ids.Length);
        Assert.Equal(third, ids[0]);                                    // varsayılan önce
        Assert.Contains(first, ids);
        Assert.Contains(second, ids);
    }

    [Fact]
    public async Task Setting_the_default_moves_it_and_there_is_always_exactly_one()
    {
        var customer = await fixture.CustomerAsync();
        var first = await customer.CreateAddressAsync("A");
        var second = await customer.CreateAddressAsync("B");

        var response = await customer.PostAsync($"{CartKit.AddressesUrl}/{second}/default", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.True(json.RootElement.GetProperty("isDefault").GetBoolean());
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
        Assert.Equal(second, await fixture.ScalarAsync<Guid>($"SELECT id FROM customer_addresses WHERE user_id = '{customer.UserId}' AND is_default"));
        Assert.False(await fixture.ScalarAsync<bool>($"SELECT is_default FROM customer_addresses WHERE id = '{first}'"));
        // Zaten varsayılan olana tekrar çağrı sorunsuz.
        Assert.Equal(HttpStatusCode.OK, (await customer.PostAsync($"{CartKit.AddressesUrl}/{second}/default", null)).StatusCode);
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Deleting_the_default_promotes_the_most_recently_updated_address_and_deleting_a_normal_one_keeps_the_default()
    {
        var customer = await fixture.CustomerAsync();
        var a = await customer.CreateAddressAsync("A");   // varsayılan
        var b = await customer.CreateAddressAsync("B");
        var c = await customer.CreateAddressAsync("C");
        // b en son güncellenen olsun.
        Assert.Equal(HttpStatusCode.OK, (await customer.PutAsync($"{CartKit.AddressesUrl}/{b}", CartKit.ValidAddress("B2"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"{CartKit.AddressesUrl}/{c}")).StatusCode); // varsayılan olmayan
        Assert.True(await fixture.ScalarAsync<bool>($"SELECT is_default FROM customer_addresses WHERE id = '{a}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"{CartKit.AddressesUrl}/{a}")).StatusCode); // varsayılan siliniyor
        Assert.Equal(1, await fixture.AddressCountAsync(customer.UserId));
        Assert.True(await fixture.ScalarAsync<bool>($"SELECT is_default FROM customer_addresses WHERE id = '{b}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"{CartKit.AddressesUrl}/{b}")).StatusCode); // sonuncu
        Assert.Equal(0, await fixture.AddressCountAsync(customer.UserId));
        await ApiAssert.ProblemAsync(await customer.DeleteAsync($"{CartKit.AddressesUrl}/{b}"), 404, "address_not_found");
    }

    [Fact]
    public async Task Put_fully_updates_the_address_but_never_the_default_flag_or_the_owner()
    {
        var customer = await fixture.CustomerAsync();
        var stranger = await fixture.CustomerAsync();
        var first = await customer.CreateAddressAsync("A");
        var second = await customer.CreateAddressAsync("B");

        var response = await customer.SendAsync(
            HttpMethod.Put,
            $"{CartKit.AddressesUrl}/{second}",
            JsonSerializer.Serialize(new
            {
                label = "Yeni Ad",
                recipientName = "Mehmet Demir",
                phone = "+90 533 111 22 33",
                province = "Ankara",
                district = "Çankaya",
                streetAddress = "Atatürk Bulvarı 5",
                postalCode = (string?)null,
                isDefault = true,
                userId = stranger.UserId,
                id = first,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal(second, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Yeni Ad", json.RootElement.GetProperty("label").GetString());
        Assert.Equal("5331112233", json.RootElement.GetProperty("phone").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("postalCode").ValueKind);
        Assert.False(json.RootElement.GetProperty("isDefault").GetBoolean());   // isDefault yok sayıldı
        Assert.Equal(customer.UserId, await fixture.ScalarAsync<Guid>($"SELECT user_id FROM customer_addresses WHERE id = '{second}'"));
        Assert.Equal(0, await fixture.AddressCountAsync(stranger.UserId));
        Assert.Equal(1, await fixture.DefaultAddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task More_than_ten_addresses_get_409_address_limit()
    {
        var customer = await fixture.CustomerAsync();
        for (var i = 0; i < 10; i++)
        {
            await customer.CreateAddressAsync($"Adres {i}");
        }

        await ApiAssert.ProblemAsync(await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress("11.")), 409, "address_limit");

        Assert.Equal(10, await fixture.AddressCountAsync(customer.UserId));
        // Silince yeniden eklenebilir.
        using var list = await ListAsync(customer);
        await customer.DeleteAsync($"{CartKit.AddressesUrl}/{Ids(list).Last()}");
        Assert.Equal(HttpStatusCode.Created, (await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress("11."))).StatusCode);
    }

    // ---------- Telefon ----------

    [Theory]
    [InlineData("05321234567", "5321234567")]
    [InlineData("+905321234567", "5321234567")]
    [InlineData("0 532 123 45 67", "5321234567")]
    [InlineData("5321234567", "5321234567")]
    [InlineData("+90 532 123 45 67", "5321234567")]
    public async Task Phone_numbers_are_normalized_to_ten_digits(string input, string expected)
    {
        var customer = await fixture.CustomerAsync();

        var response = await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress(phone: input));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal(expected, json.RootElement.GetProperty("phone").GetString());
        Assert.Equal(expected, (await fixture.ScalarAsync<string>($"SELECT phone FROM customer_addresses WHERE user_id = '{customer.UserId}'"))!.Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("02121234567")]            // sabit hat, 5 ile başlamıyor
    [InlineData("053212345678")]           // fazla hane
    [InlineData("+9053212345")]            // eksik hane
    [InlineData("0532 123 45 6a")]
    [InlineData("0532-123-45-67")]         // tire kabul edilmez
    [InlineData("+49 532 123 45 67")]
    public async Task Bad_phone_numbers_get_400(string phone)
    {
        var customer = await fixture.CustomerAsync();

        var response = await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress(phone: phone));

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        using var json = await response.JsonAsync();
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("phone", out _));
        Assert.Equal(0, await fixture.AddressCountAsync(customer.UserId));
    }

    // ---------- Alan doğrulaması ----------

    [Fact]
    public async Task Each_field_is_validated_empty_over_long_control_characters_province_and_postal_code()
    {
        var customer = await fixture.CustomerAsync();
        string Body(Func<Dictionary<string, object?>, Dictionary<string, object?>> change)
        {
            var baseAddress = new Dictionary<string, object?>
            {
                ["label"] = "Ev", ["recipientName"] = "Ayşe", ["phone"] = "05321234567", ["province"] = "İzmir",
                ["district"] = "Konak", ["streetAddress"] = "Sokak 1", ["postalCode"] = "35000",
            };
            return JsonSerializer.Serialize(change(baseAddress));
        }

        var cases = new (string Field, string Body)[]
        {
            ("label", Body(a => { a["label"] = ""; return a; })),
            ("label", Body(a => { a["label"] = new string('l', 51); return a; })),
            ("label", Body(a => { a["label"] = "Ev\u0000"; return a; })),
            ("recipientName", Body(a => { a["recipientName"] = "   "; return a; })),
            ("recipientName", Body(a => { a["recipientName"] = new string('r', 101); return a; })),
            ("recipientName", Body(a => { a["recipientName"] = "Ayşe\nYılmaz"; return a; })),
            ("province", Body(a => { a["province"] = "Atlantis"; return a; })),
            ("province", Body(a => { a["province"] = "istanbul"; return a; })),    // büyük/küçük harf duyarlı resmi yazım
            ("province", Body(a => { a["province"] = ""; return a; })),
            ("district", Body(a => { a["district"] = ""; return a; })),
            ("district", Body(a => { a["district"] = new string('d', 65); return a; })),
            ("district", Body(a => { a["district"] = "Kadı\tköy"; return a; })),
            ("streetAddress", Body(a => { a["streetAddress"] = ""; return a; })),
            ("streetAddress", Body(a => { a["streetAddress"] = new string('s', 301); return a; })),
            ("streetAddress", Body(a => { a["streetAddress"] = "Sokak\u0007"; return a; })),
            ("postalCode", Body(a => { a["postalCode"] = "123"; return a; })),
            ("postalCode", Body(a => { a["postalCode"] = "00123"; return a; })),   // plaka 00 yok
            ("postalCode", Body(a => { a["postalCode"] = "82000"; return a; })),   // plaka 82 yok
            ("postalCode", Body(a => { a["postalCode"] = "3500a"; return a; })),
            ("recipientName", Body(a => { a.Remove("recipientName"); return a; })),
            ("phone", Body(a => { a["phone"] = 5321234567; return a; })),            // yanlış tip -> body hatası
        };

        foreach (var (field, body) in cases)
        {
            var response = await customer.SendAsync(HttpMethod.Post, CartKit.AddressesUrl, body);
            await ApiAssert.ProblemAsync(response, 400, "validation_failed");
            using var json = await response.JsonAsync();
            var errors = json.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty(field, out _) || errors.TryGetProperty("body", out _), $"{field} için hata beklenirdi");
        }

        Assert.Equal(0, await fixture.AddressCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Boundary_lengths_and_every_province_are_accepted_and_blank_postal_code_is_null()
    {
        var customer = await fixture.CustomerAsync();
        var body = new
        {
            label = new string('l', 50),
            recipientName = new string('r', 100),
            phone = "5321234567",
            province = "Şanlıurfa",
            district = new string('d', 64),
            streetAddress = new string('s', 300),
            postalCode = "  ",
        };

        var response = await customer.PostAsync(CartKit.AddressesUrl, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("postalCode").ValueKind);
        Assert.Equal(300, json.RootElement.GetProperty("streetAddress").GetString()!.Length);
        Assert.Equal(81, SSCommerce.Domain.Customers.AddressRules.Provinces.Distinct().Count());
    }

    [Fact]
    public async Task XSS_looking_text_is_stored_literally_and_returned_unchanged_never_rejected_or_altered()
    {
        var customer = await fixture.CustomerAsync();
        const string script = "<script>alert('x')</script>";
        const string html = "<img src=x onerror=alert(1)> & \"quote\" 'single'";
        var body = new
        {
            label = script,
            recipientName = html,
            phone = "5321234567",
            province = "Bursa",
            district = "O'Hara <b>İlçe</b>",
            streetAddress = "javascript:alert(1) -- ; DROP TABLE customer_addresses; --",
            postalCode = "16000",
        };

        var response = await customer.PostAsync(CartKit.AddressesUrl, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.TextAsync();
        // Yanıt JSON'dır (HTML değil): tarayıcı onu belge olarak yorumlamaz; JSON metin kaçışları (tırnak, ters bölü) geçerli ayrıştırmayı garanti eder.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var parsed = JsonDocument.Parse(raw);
        Assert.Equal(script, parsed.RootElement.GetProperty("label").GetString());
        Assert.Equal(html, parsed.RootElement.GetProperty("recipientName").GetString());
        Assert.Equal("O'Hara <b>İlçe</b>", parsed.RootElement.GetProperty("district").GetString());
        Assert.Equal(script, await fixture.ScalarAsync<string>($"SELECT label FROM customer_addresses WHERE user_id = '{customer.UserId}'"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name = 'customer_addresses'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bad_json_wrong_types_and_oversized_bodies_get_400_in_both_environments(bool production)
    {
        var customer = await fixture.CustomerAsync(production ? fixture.ProductionFactory : fixture.DevelopmentFactory);
        var big = JsonSerializer.Serialize(new { label = "x", note = new string('x', 17 * 1024) });

        foreach (var body in new[] { "{", "[]", "null", "", """{"label":5}""", big })
        {
            await ApiAssert.ProblemAsync(await customer.SendAsync(HttpMethod.Post, CartKit.AddressesUrl, body), 400, "validation_failed");
        }

        Assert.Equal(0, await fixture.AddressCountAsync(customer.UserId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("123")]
    public async Task Malformed_address_ids_get_404_address_not_found(string id)
    {
        var customer = await fixture.CustomerAsync();

        await ApiAssert.ProblemAsync(await customer.PutAsync($"{CartKit.AddressesUrl}/{id}", CartKit.ValidAddress()), 404, "address_not_found");
        await ApiAssert.ProblemAsync(await customer.PostAsync($"{CartKit.AddressesUrl}/{id}/default", null), 404, "address_not_found");
        await ApiAssert.ProblemAsync(await customer.DeleteAsync($"{CartKit.AddressesUrl}/{id}"), 404, "address_not_found");
        await ApiAssert.ProblemAsync(await customer.PutAsync($"{CartKit.AddressesUrl}/{Guid.NewGuid()}", CartKit.ValidAddress()), 404, "address_not_found");
    }
}

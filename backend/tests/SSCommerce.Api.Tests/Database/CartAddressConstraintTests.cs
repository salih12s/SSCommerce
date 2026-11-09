using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Domain.Cart;
using SSCommerce.Domain.Customers;
using DomainCart = SSCommerce.Domain.Cart.Cart;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 6 sepet ve adres şemasının (D-049..D-052) kısıtlarını gerçek PostgreSQL'de kanıtlar. Ham SQL kullanılır: domain sınıfları yanlış girdiyi
/// zaten önce reddeder; soru "uygulama hatası ya da elle SQL kirli veri üretebilir mi?" sorusudur. SqlState: 23505 unique, 23514 check,
/// 23503 foreign key. Her test kendi benzersiz verisini kurar; sıra bağımsızdır.
/// </summary>
public class CartAddressConstraintTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private const string EmptyJson = "{}";

    private PostgresTestDatabase Db => fixture.Database;

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    private async Task<Guid> InsertUserAsync()
    {
        var id = Guid.NewGuid();
        var suffix = Unique().ToUpperInvariant();
        await Db.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{id}', now(), 'u{suffix}', 'U{suffix}', 'u{suffix}@ornek.test', 'U{suffix}@ORNEK.TEST', false, false, false, true, 0)
            """,
            TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<Guid> InsertVariantAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        await Db.ExecuteAsync(
            $"INSERT INTO categories (id, name, slug, parent_id, sort_order, is_active, created_at, updated_at) VALUES ('{categoryId}', 'K', 'kat-{Unique()}', NULL, 0, true, now(), now())", ct);
        await Db.ExecuteAsync(
            $"INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at) VALUES ('{productId}', '{categoryId}', 'U', 'urun-{Unique()}', true, now(), now())", ct);
        await Db.ExecuteAsync(
            $"""
            INSERT INTO product_variants (id, product_id, sku, display_name, options, option_signature, price, currency, is_active, created_at, updated_at)
            VALUES ('{variantId}', '{productId}', 'CRT-{Unique().ToUpperInvariant()}', 'V', '{EmptyJson}'::jsonb, 'default', 10.00, 'TRY', true, now(), now())
            """,
            ct);
        return variantId;
    }

    private static string CartSql(Guid cartId, Guid userId) =>
        $"INSERT INTO carts (id, user_id, created_at, updated_at) VALUES ('{cartId}', '{userId}', now(), now())";

    private async Task<Guid> InsertCartAsync(Guid userId)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(CartSql(id, userId), TestContext.Current.CancellationToken);
        return id;
    }

    private static string ItemSql(Guid cartId, Guid variantId, string quantity = "1", string price = "10.00") =>
        $"""
        INSERT INTO cart_items (id, cart_id, product_variant_id, quantity, unit_price_at_add, created_at, updated_at)
        VALUES ('{Guid.NewGuid()}', '{cartId}', '{variantId}', {quantity}, {price}, now(), now())
        """;

    private static string AddressSql(
        Guid userId,
        bool isDefault = false,
        string phone = "5321234567",
        string? postalCode = "34000",
        string label = "Ev") =>
        $"""
        INSERT INTO customer_addresses (id, user_id, label, recipient_name, phone, province, district, street_address, postal_code, is_default, created_at, updated_at)
        VALUES ('{Guid.NewGuid()}', '{userId}', '{label}', 'Ayşe Yılmaz', '{phone}', 'İstanbul', 'Kadıköy', 'Örnek Mah. Test Sok. No:1',
            {(postalCode is null ? "NULL" : $"'{postalCode}'")}, {(isDefault ? "true" : "false")}, now(), now())
        """;

    private async Task AssertViolationAsync(string expectedSqlState, string sql, string? expectedConstraint = null)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(sql, TestContext.Current.CancellationToken));
        Assert.Equal(expectedSqlState, exception.SqlState);
        if (expectedConstraint is not null)
        {
            Assert.Equal(expectedConstraint, exception.ConstraintName);
        }
    }

    private Task<long> CountAsync(string sql) => Db.ScalarAsync<long>(sql, TestContext.Current.CancellationToken);

    // ---- carts ----

    [Fact]
    public async Task A_second_cart_for_the_same_user_is_rejected_but_another_user_may_have_one()
    {
        var userId = await InsertUserAsync();
        await InsertCartAsync(userId);

        await AssertViolationAsync("23505", CartSql(Guid.NewGuid(), userId), "ux_carts_user_id");

        await InsertCartAsync(await InsertUserAsync());
    }

    [Fact]
    public async Task Cart_insert_on_conflict_do_nothing_creates_a_single_cart_per_user()
    {
        // Sözleşmedeki ilk oluşturma yolu (INSERT … ON CONFLICT (user_id) DO NOTHING): ikinci çağrı 0 satır ekler, hata vermez.
        var userId = await InsertUserAsync();
        string Upsert() =>
            $"INSERT INTO carts (id, user_id, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{userId}', now(), now()) ON CONFLICT (user_id) DO NOTHING RETURNING 1";

        Assert.Equal(1L, await Db.ScalarAsync<long>(Upsert(), TestContext.Current.CancellationToken));
        Assert.Equal(0L, await Db.ScalarAsync<long>(Upsert(), TestContext.Current.CancellationToken));
        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM carts WHERE user_id = '{userId}'"));
    }

    [Fact]
    public async Task A_cart_for_an_unknown_user_is_rejected()
    {
        await AssertViolationAsync("23503", CartSql(Guid.NewGuid(), Guid.NewGuid()), "fk_carts_user");
    }

    // ---- cart_items ----

    [Fact]
    public async Task Duplicate_variant_in_the_same_cart_is_rejected_but_allowed_in_another_cart()
    {
        var cartId = await InsertCartAsync(await InsertUserAsync());
        var variantId = await InsertVariantAsync();
        await Db.ExecuteAsync(ItemSql(cartId, variantId), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23505", ItemSql(cartId, variantId), "ux_cart_items_cart_id_product_variant_id");

        var otherCart = await InsertCartAsync(await InsertUserAsync());
        await Db.ExecuteAsync(ItemSql(otherCart, variantId), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("20")]
    public async Task Quantity_at_the_boundaries_is_accepted(string quantity)
    {
        var cartId = await InsertCartAsync(await InsertUserAsync());

        await Db.ExecuteAsync(ItemSql(cartId, await InsertVariantAsync(), quantity), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("-1")]
    public async Task Quantity_outside_one_to_twenty_is_rejected(string quantity)
    {
        var cartId = await InsertCartAsync(await InsertUserAsync());

        await AssertViolationAsync("23514", ItemSql(cartId, await InsertVariantAsync(), quantity), "ck_cart_items_quantity_range");
    }

    [Fact]
    public async Task A_negative_price_hint_is_rejected_and_zero_is_valid()
    {
        var cartId = await InsertCartAsync(await InsertUserAsync());
        await Db.ExecuteAsync(ItemSql(cartId, await InsertVariantAsync(), price: "0.00"), TestContext.Current.CancellationToken);

        await AssertViolationAsync(
            "23514", ItemSql(cartId, await InsertVariantAsync(), price: "-0.01"), "ck_cart_items_unit_price_at_add_non_negative");
    }

    [Fact]
    public async Task Cart_items_must_reference_an_existing_cart_and_variant()
    {
        var cartId = await InsertCartAsync(await InsertUserAsync());
        var variantId = await InsertVariantAsync();

        await AssertViolationAsync("23503", ItemSql(Guid.NewGuid(), variantId), "fk_cart_items_cart");
        await AssertViolationAsync("23503", ItemSql(cartId, Guid.NewGuid()), "fk_cart_items_product_variant");
    }

    [Fact]
    public async Task Deleting_a_variant_that_is_in_a_cart_is_restricted()
    {
        var cartId = await InsertCartAsync(await InsertUserAsync());
        var variantId = await InsertVariantAsync();
        await Db.ExecuteAsync(ItemSql(cartId, variantId), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23503", $"DELETE FROM product_variants WHERE id = '{variantId}'", "fk_cart_items_product_variant");

        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM product_variants WHERE id = '{variantId}'"));
        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM cart_items WHERE cart_id = '{cartId}'"));
    }

    [Fact]
    public async Task Deleting_a_cart_cascades_to_its_items_and_leaves_the_variant()
    {
        var cartId = await InsertCartAsync(await InsertUserAsync());
        var variantId = await InsertVariantAsync();
        await Db.ExecuteAsync(ItemSql(cartId, variantId), TestContext.Current.CancellationToken);

        await Db.ExecuteAsync($"DELETE FROM carts WHERE id = '{cartId}'", TestContext.Current.CancellationToken);

        Assert.Equal(0L, await CountAsync($"SELECT count(*) FROM cart_items WHERE cart_id = '{cartId}'"));
        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM product_variants WHERE id = '{variantId}'"));
    }

    [Fact]
    public async Task Deleting_a_user_cascades_to_cart_items_and_addresses_but_not_to_other_users()
    {
        var userId = await InsertUserAsync();
        var otherUserId = await InsertUserAsync();
        var cartId = await InsertCartAsync(userId);
        var otherCartId = await InsertCartAsync(otherUserId);
        var variantId = await InsertVariantAsync();
        await Db.ExecuteAsync(ItemSql(cartId, variantId), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(ItemSql(otherCartId, variantId), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(userId, isDefault: true), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(userId), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(otherUserId, isDefault: true), TestContext.Current.CancellationToken);

        await Db.ExecuteAsync($"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'", TestContext.Current.CancellationToken);

        Assert.Equal(0L, await CountAsync($"SELECT count(*) FROM carts WHERE user_id = '{userId}'"));
        Assert.Equal(0L, await CountAsync($"SELECT count(*) FROM cart_items WHERE cart_id = '{cartId}'"));
        Assert.Equal(0L, await CountAsync($"SELECT count(*) FROM customer_addresses WHERE user_id = '{userId}'"));
        // Diğer kullanıcının verisi duruyor.
        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM carts WHERE user_id = '{otherUserId}'"));
        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM cart_items WHERE cart_id = '{otherCartId}'"));
        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM customer_addresses WHERE user_id = '{otherUserId}'"));
    }

    // ---- customer_addresses ----

    [Fact]
    public async Task A_second_default_address_for_the_same_user_is_rejected()
    {
        var userId = await InsertUserAsync();
        await Db.ExecuteAsync(AddressSql(userId, isDefault: true), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23505", AddressSql(userId, isDefault: true), "ux_customer_addresses_user_id_default");
    }

    [Fact]
    public async Task Two_different_users_may_each_have_a_default_address()
    {
        await Db.ExecuteAsync(AddressSql(await InsertUserAsync(), isDefault: true), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(await InsertUserAsync(), isDefault: true), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Non_default_addresses_are_not_limited_by_the_default_index_and_duplicates_are_allowed()
    {
        var userId = await InsertUserAsync();
        await Db.ExecuteAsync(AddressSql(userId, isDefault: true), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(userId), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(userId), TestContext.Current.CancellationToken); // aynı içerik, ikinci kez
        await Db.ExecuteAsync(AddressSql(userId), TestContext.Current.CancellationToken);

        Assert.Equal(4L, await CountAsync($"SELECT count(*) FROM customer_addresses WHERE user_id = '{userId}'"));
    }

    [Fact]
    public async Task Moving_the_default_flag_needs_the_old_default_cleared_first()
    {
        // Partial unique index'in ürettiği kural: tek UPDATE ile iki varsayılan oluşamaz; önce eskisi kapatılır (adres yazmaları bunu sıralar).
        var userId = await InsertUserAsync();
        await Db.ExecuteAsync(AddressSql(userId, isDefault: true, label: "Ev"), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(userId, label: "Is"), TestContext.Current.CancellationToken);

        await AssertViolationAsync(
            "23505", $"UPDATE customer_addresses SET is_default = true WHERE user_id = '{userId}' AND label = 'Is'", "ux_customer_addresses_user_id_default");

        await Db.ExecuteAsync(
            $"UPDATE customer_addresses SET is_default = (label = 'Is') WHERE user_id = '{userId}'", TestContext.Current.CancellationToken);
        Assert.Equal("Is", await Db.ScalarAsync<string>(
            $"SELECT label FROM customer_addresses WHERE user_id = '{userId}' AND is_default", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("4321234567")]    // 5 ile başlamıyor
    [InlineData("05321234567")]   // 11 karakter: sıfırlı biçim normalize edilmeden saklanamaz (char(10))
    [InlineData("532123456")]     // 9 hane
    [InlineData("53212345678")]   // 11 hane
    [InlineData("53212345a7")]    // harf
    public async Task Bad_phone_is_rejected(string phone)
    {
        var userId = await InsertUserAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => Db.ExecuteAsync(AddressSql(userId, phone: phone), TestContext.Current.CancellationToken));

        // 11 hane char(10) sütununa sığmaz (22001); diğerleri CHECK ihlalidir (23514). Hiçbiri satır bırakmaz.
        Assert.Contains(exception.SqlState, new[] { "23514", "22001" });
        Assert.Equal(0L, await CountAsync($"SELECT count(*) FROM customer_addresses WHERE user_id = '{userId}'"));
    }

    [Theory]
    [InlineData("5321234567")]
    [InlineData("5000000000")]
    public async Task Normalized_phone_is_accepted(string phone)
    {
        await Db.ExecuteAsync(AddressSql(await InsertUserAsync(), phone: phone), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("00000")]   // ilk iki hane 00
    [InlineData("82000")]   // ilk iki hane 82
    [InlineData("99999")]
    [InlineData("3400")]    // 4 hane (char(5) boşlukla doldurur; CHECK reddeder)
    [InlineData("34a00")]
    public async Task Bad_postal_code_is_rejected(string postalCode)
    {
        await AssertViolationAsync("23514", AddressSql(await InsertUserAsync(), postalCode: postalCode), "ck_customer_addresses_postal_code_format");
    }

    [Theory]
    [InlineData("01000")]
    [InlineData("34000")]
    [InlineData("81999")]
    public async Task Valid_postal_codes_and_a_missing_one_are_accepted(string postalCode)
    {
        var userId = await InsertUserAsync();
        await Db.ExecuteAsync(AddressSql(userId, postalCode: postalCode), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(AddressSql(userId, postalCode: null), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Blank_text_fields_are_rejected()
    {
        await AssertViolationAsync("23514", AddressSql(await InsertUserAsync(), label: "   "), "ck_customer_addresses_label_not_blank");
    }

    [Fact]
    public async Task An_address_for_an_unknown_user_is_rejected()
    {
        await AssertViolationAsync("23503", AddressSql(Guid.NewGuid()), "fk_customer_addresses_user");
    }

    // ---- EF eşlemesi ----

    [Fact]
    public async Task Cart_item_and_address_round_trip_through_ef_with_utc_timestamps_and_the_price_hint()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var userId = await InsertUserAsync();
        var variantId = await InsertVariantAsync();
        var cart = new DomainCart(userId);
        var item = new CartItem(cart.Id, variantId, 3, 349.90m);
        var address = new CustomerAddress(
            userId, " Ev ", " Ayşe Yılmaz ", "0 532 123 45 67", "İstanbul", " Kadıköy ", "Örnek Mah. Test Sok. No:1", null, isDefault: true);

        await using (var context = Db.CreateDbContext(new FixedTimeProvider(now)))
        {
            context.AddRange(cart, item, address);
            await context.SaveChangesAsync(ct);
        }

        // Adedi değiştir: ipucu fiyatı da yenilenir ve UpdatedAt ilerler.
        await using (var context = Db.CreateDbContext(new FixedTimeProvider(now.AddMinutes(5))))
        {
            var tracked = await context.CartItems.SingleAsync(i => i.Id == item.Id, ct);
            tracked.ChangeQuantity(5, 399.90m);
            await context.SaveChangesAsync(ct);
        }

        await using var read = Db.CreateDbContext();
        var loadedCart = await read.Carts.SingleAsync(c => c.Id == cart.Id, ct);
        Assert.Equal(userId, loadedCart.UserId);
        Assert.Equal(now, loadedCart.CreatedAt);

        var loadedItem = await read.CartItems.SingleAsync(i => i.Id == item.Id, ct);
        Assert.Equal((5, 399.90m), (loadedItem.Quantity, loadedItem.UnitPriceAtAdd));
        Assert.Equal(now, loadedItem.CreatedAt);
        Assert.Equal(now.AddMinutes(5), loadedItem.UpdatedAt);

        var loadedAddress = await read.CustomerAddresses.SingleAsync(a => a.Id == address.Id, ct);
        Assert.Equal("5321234567", loadedAddress.Phone);
        Assert.Equal("Kadıköy", loadedAddress.District);
        Assert.Null(loadedAddress.PostalCode);
        Assert.True(loadedAddress.IsDefault);
        Assert.Equal(AddressRules.PhoneLength, loadedAddress.Phone.Length);
    }
}

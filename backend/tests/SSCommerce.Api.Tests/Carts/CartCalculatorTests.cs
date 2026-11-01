using SSCommerce.Application.Carts;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>
/// SAF (unit) testler: veritabanı yok. Sepet hesabının para ve durum kuralları sınırlarıyla burada hızlı sınanır; aynı kuralların HTTP/DB
/// yolu CartApiTests ve CheckoutPreviewTests'te ayrıca kanıtlanır.
/// </summary>
public class CartCalculatorTests
{
    private static CartLineData Line(
        int quantity = 1,
        decimal current = 100.00m,
        decimal? hint = null,
        bool purchasable = true,
        long available = 50) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "SKU-1", "slug", "Ürün", "Varyant", null, null, quantity, hint ?? current, current, purchasable, available);

    private static CartDto Calc(params CartLineData[] lines)
    {
        var result = CartCalculator.Calculate(lines);
        Assert.False(result.TooLarge);
        return result.Cart!;
    }

    [Fact]
    public void An_empty_cart_is_empty_and_has_no_issues()
    {
        var cart = Calc();

        Assert.Empty(cart.Items);
        Assert.Equal((0, 0L, false, "TRY"), (cart.ItemCount, cart.SubtotalMinor, cart.HasIssues, cart.Currency));
    }

    [Fact]
    public void The_subtotal_is_the_sum_of_current_prices_times_quantities_in_kurus_and_the_hint_never_counts()
    {
        var cart = Calc(Line(quantity: 2, current: 150.50m, hint: 99.00m), Line(quantity: 3, current: 0.10m));

        Assert.Equal(30_100 + 30, cart.SubtotalMinor);
        Assert.Equal(5, cart.ItemCount);
        Assert.Equal(15_050, cart.Items[0].UnitPriceMinor);
        Assert.Equal(9_900, cart.Items[0].PreviousUnitPriceMinor);
        Assert.True(cart.Items[0].PriceChanged);
        Assert.False(cart.Items[1].PriceChanged);
        Assert.Null(cart.Items[1].PreviousUnitPriceMinor);
    }

    [Fact]
    public void Decimal_money_is_exact_ten_times_a_price_of_0_10_is_exactly_100_kurus()
    {
        Assert.Equal(100, Calc(Line(quantity: 10, current: 0.10m)).SubtotalMinor);
        Assert.Equal(2_000, Calc(Line(quantity: 20, current: 1.00m)).SubtotalMinor);
    }

    [Theory]
    [InlineData(5, 50, "available", 20)]
    [InlineData(20, 20, "available", 20)]
    [InlineData(5, 3, "insufficient_stock", 3)]
    [InlineData(5, 0, "out_of_stock", 0)]
    [InlineData(21, 500, "insufficient_stock", 20)]   // cap: gerçek 500 değil 20 konuşulur
    public void The_status_follows_the_capped_availability(int quantity, long available, string status, int shown)
    {
        var item = Calc(Line(quantity: quantity, available: available)).Items[0];

        Assert.Equal(status, item.Status);
        Assert.Equal(shown, item.AvailableQuantity);
    }

    [Fact]
    public void An_unavailable_line_has_null_prices_zero_availability_and_is_excluded_from_the_subtotal_but_not_the_item_count()
    {
        var cart = Calc(Line(quantity: 2), Line(quantity: 4, purchasable: false, available: 99));

        var gone = cart.Items[1];
        Assert.Equal("unavailable", gone.Status);
        Assert.Null(gone.UnitPriceMinor);
        Assert.Null(gone.LineSubtotalMinor);
        Assert.Null(gone.PreviousUnitPriceMinor);
        Assert.Equal(0, gone.AvailableQuantity);
        Assert.Equal(20_000, cart.SubtotalMinor);
        Assert.Equal(6, cart.ItemCount);
        Assert.True(cart.HasIssues);
    }

    [Fact]
    public void A_total_exactly_at_the_money_limit_is_accepted_and_one_kurus_more_is_too_large_never_an_exception()
    {
        var atLimit = CartCalculator.Calculate([Line(quantity: 1, current: Money.MaxAmount)]);
        Assert.False(atLimit.TooLarge);
        Assert.Equal(9_007_199_254_740_991, atLimit.Cart!.SubtotalMinor);

        var overByOneLine = CartCalculator.Calculate([Line(quantity: 1, current: Money.MaxAmount), Line(quantity: 1, current: 0.01m)]);
        Assert.True(overByOneLine.TooLarge);
        Assert.Null(overByOneLine.Cart);

        Assert.True(CartCalculator.Calculate([Line(quantity: 2, current: Money.MaxAmount)]).TooLarge);   // tek satır adetle taşar
        Assert.True(CartCalculator.Calculate([Line(quantity: 20, current: Money.MaxAmount), Line(quantity: 20, current: Money.MaxAmount)]).TooLarge);
    }

    [Fact]
    public void Unavailable_lines_never_make_the_total_too_large()
    {
        var result = CartCalculator.Calculate([Line(quantity: 20, current: Money.MaxAmount, purchasable: false), Line(quantity: 1, current: 10m)]);

        Assert.False(result.TooLarge);
        Assert.Equal(1_000, result.Cart!.SubtotalMinor);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    [InlineData(long.MaxValue, 20)]
    public void The_public_cap_never_goes_negative_or_above_20(long onHand, int expected)
    {
        Assert.Equal(expected, CartCalculator.Cap(onHand));
    }
}

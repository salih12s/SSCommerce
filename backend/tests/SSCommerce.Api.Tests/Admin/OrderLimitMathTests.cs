using SSCommerce.Application.Orders;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// UNIT test (saf fonksiyon, veritabanı yok): 429 order_daily_limit için Retry-After hesabı. Neden unit: yuvarlama, sıkıştırma ve "n = max" ile "n &gt; max"
/// ayrımı saf aritmetiktir; gerçek saatle ve DB ile sınamak yavaş ve flaky olurdu. Saat dışarıdan verilir (flaky testin ilacı: new Date() kullanma).
/// </summary>
public class OrderLimitMathTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static List<DateTimeOffset> Orders(params double[] hoursAgo) => hoursAgo.Select(h => Now.AddHours(-h)).Order().ToList();

    [Fact]
    public void At_exactly_the_limit_the_oldest_order_must_leave_the_window()
    {
        // 3 sipariş, sınır 3 (n = max): (n - max + 1) = 1. yani en eski sipariş pencereden çıkınca yeni sipariş geçer. En eskisi 20 saat önce => 4 saat sonra.
        var seconds = OrderLimitMath.RetryAfterSeconds(Orders(20, 10, 1), max: 3, Now);

        Assert.Equal(4 * 3600, seconds);
    }

    [Fact]
    public void Above_the_limit_after_a_config_decrease_the_nth_minus_max_plus_one_oldest_decides()
    {
        // 5 sipariş, sınır sonradan 3'e indirildi (n > max): 3 siparişin pencereden çıkması gerekir (5 - 3 + 1 = 3. en eski) => 3. en eski 12 saat önce => 12 saat sonra.
        var seconds = OrderLimitMath.RetryAfterSeconds(Orders(23, 20, 12, 5, 1), max: 3, Now);

        Assert.Equal(12 * 3600, seconds);
    }

    [Fact]
    public void Fractions_of_a_second_round_up_so_the_client_never_retries_too_early()
    {
        var order = Now.AddHours(-24).AddMilliseconds(1500);
        var seconds = OrderLimitMath.RetryAfterSeconds([order], max: 1, Now);

        Assert.Equal(2, seconds);
    }

    [Fact]
    public void An_order_that_already_left_the_window_is_clamped_to_one_second_never_zero_or_negative()
    {
        var seconds = OrderLimitMath.RetryAfterSeconds([Now.AddHours(-25)], max: 1, Now);

        Assert.Equal(OrderLimitMath.MinRetryAfterSeconds, seconds);
    }

    [Fact]
    public void A_just_placed_order_is_clamped_to_one_day()
    {
        var seconds = OrderLimitMath.RetryAfterSeconds([Now.AddSeconds(5)], max: 1, Now);

        Assert.Equal(OrderLimitMath.MaxRetryAfterSeconds, seconds);
    }

    [Fact]
    public void Below_the_limit_there_is_nothing_to_wait_for_so_the_safe_minimum_comes_back()
    {
        Assert.Equal(1, OrderLimitMath.RetryAfterSeconds(Orders(1, 2), max: 3, Now));
        Assert.Equal(1, OrderLimitMath.RetryAfterSeconds([], max: 3, Now));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    [InlineData(-5, true)]
    public void Option_values_are_validated_between_1_and_1000_so_a_typo_cannot_disable_the_limit(int value, bool invalid)
    {
        var open = new OrderLimitOptions { MaxOpenOrdersPerUser = value };
        var daily = new OrderLimitOptions { MaxOrdersPerRollingDay = value };

        Assert.Equal(invalid, open.Validate() is not null);
        Assert.Equal(invalid, daily.Validate() is not null);
    }

    [Fact]
    public void Defaults_are_three_open_and_ten_per_day()
    {
        var options = new OrderLimitOptions();

        Assert.Equal(3, options.MaxOpenOrdersPerUser);
        Assert.Equal(10, options.MaxOrdersPerRollingDay);
        Assert.Null(options.Validate());
    }
}

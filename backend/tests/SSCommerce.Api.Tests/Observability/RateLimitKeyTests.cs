using System.Net;
using SSCommerce.Api.Security;

namespace SSCommerce.Api.Tests.Observability;

/// <summary>
/// Güvenlik denetimi B1: hız sınırı kovası anahtarı. Saf fonksiyon testi (gerçek PostgreSQL gerekmez, hızlı; bu yüzden unit).
/// Amaç: bir IPv6 /64 bloğu içinde adres döndürerek kovayı çoğaltmak imkânsız olmalı, IPv4 davranışı değişmemeli.
/// </summary>
public sealed class RateLimitKeyTests
{
    [Fact]
    public void Ipv4_keeps_the_full_address() =>
        Assert.Equal("203.0.113.7", ApiSecurityExtensions.RateLimitKey(IPAddress.Parse("203.0.113.7")));

    [Fact]
    public void Ipv4_mapped_ipv6_is_the_same_bucket_as_plain_ipv4() =>
        Assert.Equal("203.0.113.7", ApiSecurityExtensions.RateLimitKey(IPAddress.Parse("::ffff:203.0.113.7")));

    [Fact]
    public void Every_address_in_one_ipv6_slash64_shares_one_bucket()
    {
        var a = ApiSecurityExtensions.RateLimitKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        var b = ApiSecurityExtensions.RateLimitKey(IPAddress.Parse("2001:db8:1:2::1"));
        var c = ApiSecurityExtensions.RateLimitKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff"));

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.EndsWith("/64", a);
    }

    [Fact]
    public void Different_ipv6_slash64_blocks_get_different_buckets() =>
        Assert.NotEqual(
            ApiSecurityExtensions.RateLimitKey(IPAddress.Parse("2001:db8:1:2::1")),
            ApiSecurityExtensions.RateLimitKey(IPAddress.Parse("2001:db8:1:3::1")));

    [Fact]
    public void Loopback_ipv6_is_a_stable_key() =>
        Assert.Equal(ApiSecurityExtensions.RateLimitKey(IPAddress.IPv6Loopback), ApiSecurityExtensions.RateLimitKey(IPAddress.IPv6Loopback));

    [Fact]
    public void A_missing_address_maps_to_the_unknown_bucket() =>
        Assert.Equal("unknown", ApiSecurityExtensions.RateLimitKey(null));
}

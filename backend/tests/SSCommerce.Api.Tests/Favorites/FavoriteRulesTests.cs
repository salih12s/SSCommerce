using SSCommerce.Application.Favorites;
using SSCommerce.Domain.Customers;

namespace SSCommerce.Api.Tests.Favorites;

/// <summary>
/// UNIT: favori kurallarının saf kısmı (DB yok, mock yok). Girdi -> çıktı olduğundan milisaniyede koşar; sınır değerleri (99/100/101),
/// idempotent karar ve "satışta mı" tablosu burada kesinleşir. Gerçek DB ister olanlar (race, benzersizlik, izolasyon) API testlerindedir.
/// </summary>
public class FavoriteRulesTests
{
    [Fact]
    public void The_per_user_limit_is_one_hundred()
    {
        Assert.Equal(100, CustomerFavorite.MaxPerUser);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(98)]
    [InlineData(99)] // son boş yer
    public void A_new_product_below_the_limit_is_added(int count)
    {
        Assert.Equal(FavoriteAddDecision.Add, FavoriteRules.DecideAdd(alreadyFavorite: false, count));
    }

    [Theory]
    [InlineData(100)] // tam sınır
    [InlineData(101)] // tutarsız veri bile eklemeye izin vermez
    [InlineData(int.MaxValue)]
    public void A_new_product_at_or_above_the_limit_is_rejected(int count)
    {
        Assert.Equal(FavoriteAddDecision.LimitReached, FavoriteRules.DecideAdd(alreadyFavorite: false, count));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)] // sınırda mevcut ürünün PUT'u yine başarı (idempotent)
    [InlineData(101)]
    public void An_existing_favorite_is_never_rejected_and_never_rewritten(int count)
    {
        Assert.Equal(FavoriteAddDecision.AlreadyFavorite, FavoriteRules.DecideAdd(alreadyFavorite: true, count));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]   // ürün pasif
    [InlineData(true, false, true, false)]   // kategori pasif
    [InlineData(true, true, false, false)]   // aktif varyant kalmadı (katalog bu ürünü gizler)
    [InlineData(false, false, false, false)]
    public void A_product_is_available_only_when_product_category_and_a_variant_are_active(bool product, bool category, bool variant, bool expected)
    {
        Assert.Equal(expected, FavoriteRules.IsAvailable(product, category, variant));
    }

    [Theory]
    [InlineData(10000L, 9000L, 10000L)]  // aktif varyant varsa pasif ucuz varyant sayılmaz (katalogla aynı)
    [InlineData(null, 9000L, 9000L)]     // aktif kalmadı: satıştan kalkan ürünün son bilinen fiyatı
    [InlineData(null, null, 0L)]         // hiç varyant yok
    public void The_starting_price_prefers_active_variants_and_falls_back_without_throwing(long? active, long? any, long expected)
    {
        Assert.Equal(expected, FavoriteRules.StartingPriceMinor(active, any));
    }
}

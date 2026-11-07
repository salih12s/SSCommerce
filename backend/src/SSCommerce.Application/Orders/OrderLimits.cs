namespace SSCommerce.Application.Orders;

/// <summary>
/// Ödenmemiş sipariş sınırları (Faz 8, F8.5, docs/CONTRACT_PHASE_8.md). Yapılandırma bölümü <c>Ordering</c>. Her ikisi de açılışta 1..1000
/// aralığında doğrulanır (geçersizse uygulama AÇILMAZ). Rolden bağımsız: sipariş veren HER hesaba uygulanır.
/// Üretim eşikleri sahip onaylayana kadar <c>PRODUCTION_ORDERING_POLICY_PENDING</c>'dir (D-072).
/// </summary>
public sealed class OrderLimitOptions
{
    public const string SectionName = "Ordering";
    public const int Minimum = 1;
    public const int Maximum = 1000;
    public const int DefaultMaxOpenOrdersPerUser = 3;
    public const int DefaultMaxOrdersPerRollingDay = 10;

    /// <summary>Aynı anda açık (placed + processing) olabilecek en çok sipariş. Aşılırsa 409 <c>open_order_limit</c>.</summary>
    public int MaxOpenOrdersPerUser { get; set; } = DefaultMaxOpenOrdersPerUser;

    /// <summary>Son 24 saatte (durumdan bağımsız, iptaller dahil) verilebilecek en çok sipariş. Aşılırsa 429 <c>order_daily_limit</c>.</summary>
    public int MaxOrdersPerRollingDay { get; set; } = DefaultMaxOrdersPerRollingDay;

    /// <summary>Geçerliyse null, değilse hata iletisi (sınır değerleri ya da anahtar adı içerir; sır içermez).</summary>
    public string? Validate()
    {
        if (MaxOpenOrdersPerUser is < Minimum or > Maximum)
        {
            return $"Ordering:MaxOpenOrdersPerUser {Minimum} ile {Maximum} arasında olmalıdır.";
        }

        if (MaxOrdersPerRollingDay is < Minimum or > Maximum)
        {
            return $"Ordering:MaxOrdersPerRollingDay {Minimum} ile {Maximum} arasında olmalıdır.";
        }

        return null;
    }
}

/// <summary>Sınır hesabının SAF kısmı (zaman girdi olarak verilir; saat okunmaz).</summary>
public static class OrderLimitMath
{
    /// <summary>Kayan pencere: 24 saat.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public const int MinRetryAfterSeconds = 1;
    public const int MaxRetryAfterSeconds = 86_400;

    /// <summary>
    /// <c>Retry-After</c> saniyesi. <paramref name="windowOrdersAscending"/>: penceredeki (<c>created_at &gt; now - 24h</c>) siparişlerin oluşturulma zamanları, en eski önce.
    /// n = sayı; yeni sipariş ancak n &lt; <paramref name="max"/> olduğunda geçer, yani (n − max + 1). en eski sipariş pencereden çıkmalıdır
    /// (n = max ise en eskisi; sınır sonradan düşürüldüyse n &gt; max ise daha fazlası). Beklenen süre o siparişin <c>created_at + 24 saat − now</c> değeridir,
    /// YUKARI yuvarlanır ve 1..86400'e sıkıştırılır. Pencere dışı girdi (n &lt; max) için de güvenli bir değer (1) döner.
    /// </summary>
    public static int RetryAfterSeconds(IReadOnlyList<DateTimeOffset> windowOrdersAscending, int max, DateTimeOffset now)
    {
        var n = windowOrdersAscending.Count;
        var index = n - max; // (n - max + 1). sıra, 0 tabanlı
        if (index < 0 || index >= n)
        {
            return MinRetryAfterSeconds;
        }

        var leavesAt = windowOrdersAscending[index] + Window;
        var seconds = (long)Math.Ceiling((leavesAt - now).TotalSeconds);
        return (int)Math.Clamp(seconds, MinRetryAfterSeconds, MaxRetryAfterSeconds);
    }
}

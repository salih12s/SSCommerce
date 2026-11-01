namespace SSCommerce.Api.Security;

/// <summary>
/// <c>Security</c> yapılandırma bölümü. Sır içermez; ortam değişkeniyle ezilebilir
/// (ör. <c>Security__AllowedOrigins__0=https://magaza.example</c>).
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Değiştiren isteklerde (<c>POST/PUT/PATCH/DELETE</c>) kabul edilen <c>Origin</c> değerleri, ör. <c>http://localhost:3000</c>.
    /// Başlık yoksa kontrol uygulanmaz (CSRF token yine şart); başlık VARSA listede olmalıdır. Liste boşsa Origin başlıklı
    /// her değiştiren istek reddedilir (güvenli varsayılan: üretimde izinli origin açıkça verilmelidir).
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// X-Forwarded-For'a güvenilecek EK proxy adresleri. Loopback (127.0.0.1, ::1) her zaman güvenilir (Next.js proxy'si aynı makinede).
    /// Bu olmadan IP başına rate limit, proxy arkasında tüm kullanıcıları tek IP sayardı.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>
    /// API'nin, TLS'i sonlandıran bir proxy'nin (Next.js, D-006) ARKASINDA, düz HTTP ile dinlediğini varsayıp isteğin şemasını
    /// "https" yapar. Gerekçe: Secure + <c>__Host-</c> cookie'ler ve antiforgery (SecurePolicy=Always) HTTPS olmayan istekte
    /// istisna fırlatır. Boş bırakılırsa: Development'ta kapalı, diğer tüm ortamlarda AÇIK. API'yi doğrudan internete açma.
    /// </summary>
    public bool? AssumeHttps { get; set; }

    public RateLimitSettings RateLimit { get; set; } = new();
}

public sealed class RateLimitSettings
{
    /// <summary>login/register: pencere başına IP başına izin verilen istek.</summary>
    public int CredentialsPermitLimit { get; set; } = 10;

    /// <summary>csrf/logout gibi diğer auth uçları.</summary>
    public int GeneralPermitLimit { get; set; } = 60;

    /// <summary>Stok/sepet/adres yazmaları (<see cref="RateLimitPolicies.Mutation"/>): pencere başına IP başına izin verilen istek (D-053).</summary>
    public int MutationPermitLimit { get; set; } = 120;

    public int WindowSeconds { get; set; } = 60;
}

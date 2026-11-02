namespace SSCommerce.Api.Security;

/// <summary>Yetkilendirme politika adları (D-032). Roller <c>AppRoles</c>'tadır.</summary>
public static class AuthPolicies
{
    /// <summary>Yalnız Admin rolü.</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>
    /// Warehouse ya da Admin rolü. Faz 5 stok uçları yalnız Admin'dir (D-045); bu politikayı Faz 7 personel sipariş uçları kullanır
    /// (<c>/api/v1/admin/orders</c>, ayrı grup: AdminOnly grubunun içinde olamaz). Depo kapsamlı yetki Faz 8'dedir.
    /// </summary>
    public const string WarehouseOrAdmin = "WarehouseOrAdmin";
}

public static class RateLimitPolicies
{
    /// <summary>login ve register: sıkı sınır.</summary>
    public const string Credentials = "auth-credentials";

    /// <summary>csrf, logout: daha geniş sınır.</summary>
    public const string General = "auth-general";

    /// <summary>
    /// Stok, (ileride) sepet/adres yazmaları: IP başına, varsayılan 120/dk (<c>Security:RateLimit:MutationPermitLimit</c>).
    /// Limiter kimlik doğrulamadan ÖNCE çalıştığı için kova kullanıcıya değil IP'yedir; sıra bilerek değiştirilmez (D-053).
    /// </summary>
    public const string Mutation = "mutation";
}

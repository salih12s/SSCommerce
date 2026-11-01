namespace SSCommerce.Api.Security;

/// <summary>Yetkilendirme politika adları (D-032). Roller <c>AppRoles</c>'tadır.</summary>
public static class AuthPolicies
{
    /// <summary>Yalnız Admin rolü.</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Warehouse ya da Admin rolü (şimdilik KULLANILMAZ: Faz 5 stok uçları yalnız Admin'dir, D-045; depo kapsamlı yetki Faz 8'de).</summary>
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

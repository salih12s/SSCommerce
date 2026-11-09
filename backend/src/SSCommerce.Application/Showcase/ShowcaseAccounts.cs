namespace SSCommerce.Application.Showcase;

/// <summary>
/// Vitrin yapılandırması (VT-2, D-092). Bölüm <c>Showcase</c>. <see cref="Enabled"/> varsayılanı <c>false</c>: kapalıyken
/// <c>/api/v1/auth/demo</c> uçları HİÇ kaydedilmez (404). Production/Staging ortamında <c>true</c> açılışı durdurur
/// (<c>ProductionConfigurationValidator</c>); dağıtım dosyaları bu anahtarı hiç açmaz.
/// </summary>
public sealed class ShowcaseOptions
{
    public const string SectionName = "Showcase";

    public bool Enabled { get; set; }
}

/// <summary>
/// Üç SABİT vitrin hesabı: demo girişin hedefleyebileceği TEK hesap kümesi. E-postalar koddadır ve istekten gelmez; istek yalnız
/// <see cref="Customer"/>, <see cref="Admin"/> ya da <see cref="Warehouse"/> anahtarını seçer. Başka hiçbir kullanıcı bu yoldan hedeflenemez.
/// Parolaları kimse bilmez (tohumlama rastgele üretir ve hiçbir yere yazmaz); giriş parolasız <c>SignInAsync</c> ile olur.
/// </summary>
public static class ShowcaseAccounts
{
    public const string Customer = "customer";
    public const string Admin = "admin";
    public const string Warehouse = "warehouse";

    public const string CustomerEmail = "demo-musteri@sscommerce.example";
    public const string AdminEmail = "demo-yonetici@sscommerce.example";
    public const string WarehouseEmail = "demo-depo@sscommerce.example";

    /// <summary><c>GET /auth/demo</c> yanıtındaki sıra.</summary>
    public static IReadOnlyList<string> Keys { get; } = [Customer, Admin, Warehouse];

    /// <summary>
    /// E-posta üç sabit vitrin adresinden biri mi? E-posta büyük/küçük harfe duyarsız karşılaştırılır (Identity de öyle normalleştirir). Rol yönetimi bu hesaplara
    /// dokunamaz (<c>showcase_account_protected</c>): vitrin Showcase:Enabled kapalıyken de bu üç adres ayrılmış sayılır.
    /// </summary>
    public static bool IsShowcaseEmail(string? email) =>
        email is not null
        && (string.Equals(email, CustomerEmail, StringComparison.OrdinalIgnoreCase)
            || string.Equals(email, AdminEmail, StringComparison.OrdinalIgnoreCase)
            || string.Equals(email, WarehouseEmail, StringComparison.OrdinalIgnoreCase));

    /// <summary>Anahtar → sabit e-posta. Büyük/küçük harf duyarlıdır (tam eşleşme); bilinmeyen anahtar false.</summary>
    public static bool TryGetEmail(string? account, out string email)
    {
        switch (account)
        {
            case Customer:
                email = CustomerEmail;
                return true;
            case Admin:
                email = AdminEmail;
                return true;
            case Warehouse:
                email = WarehouseEmail;
                return true;
            default:
                email = string.Empty;
                return false;
        }
    }

    /// <summary>Hesabın sahip olması gereken TEK rol adı (<c>AppRoles</c> değerleriyle aynı: Customer, Admin, Warehouse).</summary>
    public static string RoleName(string account) => account switch
    {
        Customer => "Customer",
        Admin => "Admin",
        Warehouse => "Warehouse",
        _ => throw new ArgumentOutOfRangeException(nameof(account)),
    };
}

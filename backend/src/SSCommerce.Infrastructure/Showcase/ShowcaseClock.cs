namespace SSCommerce.Infrastructure.Showcase;

/// <summary>
/// Yalnız <c>--seed-showcase</c> komutu için saat. Varsayılan gerçek saattir; tohumlama sırasında <see cref="Set"/> ile "geçmiş bir an" verilir ve
/// zaman damgalarını yazan HER servis (stok, sepet, sipariş, durum geçmişi) onu görür. Böylece sipariş ve hareket tarihleri UPDATE ile
/// değiştirilmez (siparişler, durum geçmişi ve stok hareketleri değişmezdir: trigger'lar UPDATE'i reddeder); satırlar baştan o anla yazılır.
/// <c>Program.cs</c> bu sınıfı yalnız komut çalışırken <see cref="TimeProvider"/> olarak kaydeder; sunucu modunda gerçek saat kullanılır.
/// </summary>
public sealed class ShowcaseClock : TimeProvider
{
    private DateTimeOffset? _pinned;

    public override DateTimeOffset GetUtcNow() => _pinned ?? base.GetUtcNow();

    /// <summary>Saati verilen ana sabitler (UTC).</summary>
    public void Set(DateTimeOffset utc) => _pinned = utc.ToUniversalTime();

    /// <summary>Gerçek saate döner.</summary>
    public void Release() => _pinned = null;
}

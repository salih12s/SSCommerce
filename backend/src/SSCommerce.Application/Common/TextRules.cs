using SSCommerce.Domain.Customers;

namespace SSCommerce.Application.Common;

/// <summary>
/// Serbest metin alanları için ortak, SAF kurallar (stok nedeni/referansı, depo adı, adres alanları).
/// Reddedilenler:
/// <list type="bullet">
/// <item>Kontrol karakterleri (Unicode Cc): NUL PostgreSQL <c>text</c> sütununa yazılamaz (Npgsql 22021 =&gt; 500); satır sonu, sekme, DEL, C1
/// tek satırlık alanlarda anlamsızdır ve log/arayüz enjeksiyonuna kapı açar.</item>
/// <item>Biçim karakterleri (Cf: U+202E sağdan-sola geçersiz kılma, U+200B sıfır genişlikli boşluk, U+FEFF, yumuşak tire...) ve satır/paragraf
/// ayırıcıları (Zl U+2028, Zp U+2029): metni gizlemek, yönünü değiştirmek ya da denetim kaydında görünmez ayrım üretmek için kullanılır.</item>
/// </list>
/// Sıradan Türkçe metin (harfler, rakamlar, noktalama, bitişik işaretler Mn, NBSP, emoji) geçerli kalır. Kural HAM girdiye uygulanır:
/// <c>Trim()</c> U+2028/U+2029'u boşluk sayıp kenardan atardı. Tek kaynak Domain'deki <see cref="AddressRules.ContainsUnsafeCharacter"/>'dır
/// (Domain aynı kuralı kendi içinde de uygular); bir test iki tarafın tüm UTF-16 kod birimlerinde aynı kararı verdiğini kanıtlar.
/// </summary>
public static class TextRules
{
    public static bool ContainsUnsafeCharacter(string? value) => AddressRules.ContainsUnsafeCharacter(value);
}

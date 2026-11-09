using System.Text;

namespace SSCommerce.Infrastructure.Catalog;

/// <summary>
/// PostgreSQL <c>LIKE</c>/<c>ILIKE</c> desenleri için kaçış. Kullanıcı metni parametre olarak gönderilir (SQL enjeksiyonu yok),
/// ama <c>%</c> ve <c>_</c> desende joker sayılır; kaçışlanmazsa <c>q=%</c> her şeyi eşler. Kaçış karakteri <c>\</c>'dir.
/// </summary>
public static class LikePattern
{
    public const string EscapeCharacter = "\\";

    /// <summary>Metindeki <c>\</c>, <c>%</c> ve <c>_</c> karakterlerini kaçışlar (önce <c>\</c> gelmek zorunda değil: tek geçişte işlenir).</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 4);
        foreach (var ch in text)
        {
            if (ch is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>"İçerir" araması için <c>%metin%</c> deseni (metin kaçışlı).</summary>
    public static string Contains(string text) => "%" + Escape(text) + "%";
}

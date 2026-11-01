using System.Text.Json;

namespace SSCommerce.Api.Http;

/// <summary>
/// JSON gövdesini ELLE okur. Minimal API'nin varsayılan gövde bağlaması hatada (özellikle Development'ta) istisna fırlatır ya da
/// gövdesiz 400 döner; burada hata HER ZAMAN alan bazlı <c>validation_failed</c> sözlüğü olur (Development ve Production aynı).
/// Gövde <c>maxBytes</c>'ı aşarsa okuma durur (413 yok; 400, bkz. docs/CONTRACT_PHASES_5_6.md). Bilinmeyen alanlar yok sayılır.
/// </summary>
internal static class JsonRequestBody
{
    /// <summary>Faz 5-6 JSON uçlarının üst sınırı (16 KB).</summary>
    public const int DefaultMaxBytes = 16 * 1024;

    // Web varsayılanları sayıyı METİN olarak da okur ("3" -> 3). Sözleşme yanlış tipi 400 sayar, bu yüzden sayı işleme Strict'tir.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    public static async Task<(T? Body, Dictionary<string, string[]>? Error)> ReadAsync<T>(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!request.HasJsonContentType())
        {
            return (null, Body("İstek gövdesi application/json olmalıdır."));
        }

        // Başlık bildirimi sınırı aşıyorsa hiç okumadan reddet; bildirim yoksa/yalansa aşağıdaki sayaç korur.
        if (request.ContentLength > maxBytes)
        {
            return (null, Body("İstek gövdesi çok büyük."));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return (null, Body("İstek gövdesi çok büyük."));
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            var body = JsonSerializer.Deserialize<T>(buffer.ToArray(), Options);
            return body is null ? (null, Body("İstek gövdesi boş.")) : (body, null);
        }
        catch (JsonException)
        {
            // Bozuk JSON, yanlış tip ("quantity": "abc"), nesne yerine dizi: hepsi aynı güvenli ileti; serileştirici iletisi sızmaz.
            return (null, Body("İstek gövdesi geçerli bir JSON değil ya da alan tipleri yanlış."));
        }

        static Dictionary<string, string[]> Body(string message) => new() { ["body"] = [message] };
    }
}

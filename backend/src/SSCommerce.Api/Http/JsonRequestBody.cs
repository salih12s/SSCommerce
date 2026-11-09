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

    /// <summary>Faz 8: ürün oluşturma/güncelleme gövdesi (açıklama + teknik özellikler) için üst sınır (64 KB).</summary>
    public const int ProductMaxBytes = 64 * 1024;

    // Web varsayılanları sayıyı METİN olarak da okur ("3" -> 3). Sözleşme yanlış tipi 400 sayar, bu yüzden sayı işleme Strict'tir.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    public static async Task<(T? Body, Dictionary<string, string[]>? Error)> ReadAsync<T>(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        var (bytes, error) = await ReadBytesAsync(request, maxBytes, cancellationToken);
        if (bytes is null)
        {
            return (null, error);
        }

        try
        {
            var body = JsonSerializer.Deserialize<T>(bytes, Options);
            return body is null ? (null, Body("İstek gövdesi boş.")) : (body, null);
        }
        catch (JsonException)
        {
            // Bozuk JSON, yanlış tip ("quantity": "abc"), nesne yerine dizi: hepsi aynı güvenli ileti; serileştirici iletisi sızmaz.
            return (null, Body("İstek gövdesi geçerli bir JSON değil ya da alan tipleri yanlış."));
        }
    }

    /// <summary>
    /// Gövdeyi AYRIŞTIRILMIŞ JSON öğesi olarak verir (Faz 8 PATCH uçları: "alan yok" ile "alan null" ayrımı için). Kök türü burada denetlenmez;
    /// doğrulayıcı nesne olmasını ister. Bozuk JSON ve aşırı derin iç içelik (64) güvenli ileti ile 400 olur.
    /// </summary>
    public static async Task<(JsonElement Body, Dictionary<string, string[]>? Error)> ReadElementAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var (bytes, error) = await ReadBytesAsync(request, maxBytes, cancellationToken);
        if (bytes is null)
        {
            return (default, error);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (default, Body("İstek gövdesi geçerli bir JSON değil."));
        }
    }

    private static async Task<(byte[]? Bytes, Dictionary<string, string[]>? Error)> ReadBytesAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
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

        return (buffer.ToArray(), null);
    }

    private static Dictionary<string, string[]> Body(string message) => new() { ["body"] = [message] };
}

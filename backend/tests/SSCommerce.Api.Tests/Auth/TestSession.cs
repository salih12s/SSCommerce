using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>Yanıttaki tek bir <c>Set-Cookie</c> başlığı, ayrıştırılmış.</summary>
internal sealed record SetCookie(string Name, string Value, IReadOnlyDictionary<string, string?> Attributes, string Raw)
{
    public bool Has(string attribute) => Attributes.ContainsKey(attribute.ToLowerInvariant());

    public string? Get(string attribute) => Attributes.GetValueOrDefault(attribute.ToLowerInvariant());

    /// <summary>Silme: boş değer ya da geçmiş tarihli expires.</summary>
    public bool IsDeletion =>
        Value.Length == 0 ||
        (Get("expires") is { } expires && DateTimeOffset.TryParse(expires, out var when) && when < DateTimeOffset.UtcNow);
}

/// <summary>
/// Tarayıcıyı taklit eden küçük cookie kavanozu. HttpClient'ın kendi CookieContainer'ı KAPALI: Secure cookie'ler http://localhost
/// üzerinde gönderilmezdi ve eski cookie'yi elle "tekrar oynatmak" (replay) mümkün olmazdı. Burada her şey açıkça görünür:
/// Set-Cookie'leri biz ayrıştırır, Cookie başlığını biz kurarız.
/// </summary>
internal sealed class TestSession(HttpClient client)
{
    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    public string? Csrf { get; private set; }

    /// <summary>Aynı cookie'lerle yeni bir "tarayıcı" (replay ve çoklu cihaz testleri için).</summary>
    public TestSession CloneWithCookies()
    {
        var clone = new TestSession(client);
        foreach (var (name, value) in Cookies)
        {
            clone.Cookies[name] = value;
        }

        return clone;
    }

    public static List<SetCookie> ParseSetCookies(HttpResponseMessage response)
    {
        var result = new List<SetCookie>();
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return result;
        }

        foreach (var raw in values)
        {
            var parts = raw.Split(';', StringSplitOptions.TrimEntries);
            var first = parts[0];
            var eq = first.IndexOf('=');
            var name = eq < 0 ? first : first[..eq];
            var value = eq < 0 ? string.Empty : first[(eq + 1)..];
            var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var part in parts.Skip(1).Where(p => p.Length > 0))
            {
                var i = part.IndexOf('=');
                attributes[(i < 0 ? part : part[..i]).ToLowerInvariant()] = i < 0 ? null : part[(i + 1)..];
            }

            result.Add(new SetCookie(name, value, attributes, raw));
        }

        return result;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string? rawBody = null,
        string contentType = "application/json",
        string? csrf = null,
        string? origin = null,
        bool sendCookies = true,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (rawBody is not null)
        {
            request.Content = new StringContent(rawBody, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (sendCookies && Cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (csrf is not null)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        foreach (var (name, value) in extraHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        if (sendCookies)
        {
            Absorb(response);
        }

        return response;
    }

    private void Absorb(HttpResponseMessage response)
    {
        foreach (var cookie in ParseSetCookies(response))
        {
            if (cookie.IsDeletion)
            {
                Cookies.Remove(cookie.Name);
            }
            else
            {
                Cookies[cookie.Name] = cookie.Value;
            }
        }
    }

    public Task<HttpResponseMessage> GetAsync(string url, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Get, url, extraHeaders: headers);

    /// <summary>Token alır ve saklar (bu kullanıcının/oturumun durumuna bağlıdır; login/logout sonrası yeniden çağrılmalı).</summary>
    public async Task<string> FetchCsrfAsync()
    {
        var response = await GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Csrf = json.RootElement.GetProperty("token").GetString()!;
        return Csrf;
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string url, object? body, string? csrf = null, string? origin = null, bool useStoredCsrf = false)
    {
        csrf ??= useStoredCsrf ? Csrf : null;
        return await SendAsync(HttpMethod.Post, url, body is null ? null : JsonSerializer.Serialize(body), csrf: csrf, origin: origin);
    }

    /// <summary>CSRF'li kısayol: token alır, gönderir. Sonrasında (oturum değiştiyse) token'ı yeniler.</summary>
    public async Task<HttpResponseMessage> RegisterAsync(string email, string password, object? extra = null)
    {
        var token = await FetchCsrfAsync();
        var body = extra is null
            ? JsonSerializer.Serialize(new { email, password })
            : JsonSerializer.Serialize(Merge(new Dictionary<string, object?> { ["email"] = email, ["password"] = password }, extra));
        var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/register", body, csrf: token);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            await FetchCsrfAsync();
        }

        return response;
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        var token = await FetchCsrfAsync();
        var response = await PostJsonAsync("/api/v1/auth/login", new { email, password }, token);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            await FetchCsrfAsync();
        }

        return response;
    }

    public async Task<HttpResponseMessage> LogoutAsync() =>
        await PostJsonAsync("/api/v1/auth/logout", null, Csrf ?? await FetchCsrfAsync());

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> target, object extra)
    {
        foreach (var property in extra.GetType().GetProperties())
        {
            target[property.Name] = property.GetValue(extra);
        }

        return target;
    }
}

internal static class SessionJson
{
    public static async Task<JsonDocument> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    public static async Task<string> TextAsync(this HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <summary>traceId hariç gövde: "bayt düzeyinde aynı" karşılaştırması için.</summary>
    public static async Task<string> BodyWithoutTraceIdAsync(this HttpResponseMessage response)
    {
        using var json = await response.JsonAsync();
        var members = json.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}");
        return "{" + string.Join(",", members) + "}";
    }
}

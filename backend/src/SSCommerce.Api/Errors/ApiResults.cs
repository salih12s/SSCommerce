namespace SSCommerce.Api.Errors;

/// <summary>RFC 9457 yanıtları için ortak yardımcılar; her biri makine okunur <c>code</c> taşır (bkz. <see cref="ApiErrorCodes"/>).</summary>
public static class ApiResults
{
    public static IResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors, string title = "Geçersiz istek") =>
        Results.ValidationProblem(
            errors.ToDictionary(kv => kv.Key, kv => kv.Value),
            title: title,
            extensions: Code(ApiErrorCodes.ValidationFailed));

    public static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: Code(code));

    /// <summary>Makine okunur ek alanlarla (ör. <c>availableQuantity</c>). <c>code</c> her zaman korunur.</summary>
    public static IResult Problem(int status, string title, string code, IReadOnlyDictionary<string, object?> extensions)
    {
        var all = Code(code);
        foreach (var (key, value) in extensions)
        {
            all[key] = value;
        }

        return Results.Problem(statusCode: status, title: title, extensions: all);
    }

    private static Dictionary<string, object?> Code(string code) => new() { [ApiProblemDetails.CodeKey] = code };
}

using System.Globalization;
using System.Text.Json;
using SSCommerce.Application.Common;

namespace SSCommerce.Application.Administration;

/// <summary>
/// PATCH alanı: <see cref="IsSet"/> false ise gövdede YOKTU ("değişmedi"); true ise gönderildi (değer null olabilir: "temizle").
/// Sözleşme (CONTRACT_PHASE_8 "Response shapes"): yok = değişmez, açık <c>null</c> = nullable alanı temizler.
/// </summary>
public readonly record struct Patch<T>(bool IsSet, T? Value)
{
    public static Patch<T> Unset => default;

    public static Patch<T> Of(T? value) => new(true, value);
}

/// <summary>
/// JSON gövdesinden alan okuyucuları (SAF; ağ/veritabanı yok). Yanlış tip HER ZAMAN alan bazlı hata olur (ham değer iletiye yansıtılmaz).
/// Bilinmeyen alanlar okunmaz, yani yok sayılır. Sayılar katıdır: "3" (metin) sayı sayılmaz, 3.5 tam sayı değildir.
/// </summary>
public static class JsonFields
{
    /// <summary>Kök bir JSON nesnesi olmalıdır (dizi/sayı/metin 400).</summary>
    public static bool RequireObject(JsonElement root, Dictionary<string, string[]> errors)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        errors["body"] = ["İstek gövdesi bir JSON nesnesi olmalıdır."];
        return false;
    }

    /// <summary>
    /// <c>version</c>: zorunlu tam sayı ≥ 1 (CONTRACT_PHASE_8: eksik, tam sayı olmayan ya da &lt; 1 sürüm 400'dür, kontrolü ASLA atlamaz).
    /// Dönüş null ise <paramref name="errors"/> doludur.
    /// </summary>
    public static int? RequireVersion(JsonElement root, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty("version", out var element))
        {
            errors["version"] = ["version zorunludur."];
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var version) || version < 1)
        {
            errors["version"] = ["version 1 ya da daha büyük bir tam sayı olmalıdır."];
            return null;
        }

        return version;
    }

    public static Patch<string?> String(JsonElement root, string name, bool allowNull, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return Patch<string?>.Unset;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (TextRules.ContainsUnsafeCharacter(text))
                {
                    errors[name] = [$"{name} kontrol, biçim ya da satır ayırıcı karakter (satır sonu, sekme, NUL, U+202E, U+200B...) içeremez."];
                    return Patch<string?>.Unset;
                }

                return Patch<string?>.Of(text);
            case JsonValueKind.Null when allowNull:
                return Patch<string?>.Of(null);
            default:
                errors[name] = [allowNull ? $"{name} metin ya da null olmalıdır." : $"{name} metin olmalıdır."];
                return Patch<string?>.Unset;
        }
    }

    public static Patch<bool> Bool(JsonElement root, string name, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return Patch<bool>.Unset;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return Patch<bool>.Of(element.GetBoolean());
        }

        errors[name] = [$"{name} true ya da false olmalıdır."];
        return Patch<bool>.Unset;
    }

    public static Patch<long> Integer(JsonElement root, string name, long min, long max, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return Patch<long>.Unset;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            errors[name] = [$"{name} tam sayı olmalıdır."];
            return Patch<long>.Unset;
        }

        if (value < min || value > max)
        {
            errors[name] = [string.Create(CultureInfo.InvariantCulture, $"{name} {min} ile {max} arasında olmalıdır.")];
            return Patch<long>.Unset;
        }

        return Patch<long>.Of(value);
    }

    public static Patch<Guid?> Guid(JsonElement root, string name, bool allowNull, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return Patch<Guid?>.Unset;
        }

        if (element.ValueKind == JsonValueKind.Null && allowNull)
        {
            return Patch<Guid?>.Of(null);
        }

        if (element.ValueKind == JsonValueKind.String && System.Guid.TryParse(element.GetString(), out var id) && id != System.Guid.Empty)
        {
            return Patch<Guid?>.Of(id);
        }

        errors[name] = [$"{name} geçerli bir kimlik olmalıdır."];
        return Patch<Guid?>.Unset;
    }

    /// <summary>Metin → metin sözlüğü (özellikler, seçenekler). <c>null</c> izinli ise temizler. Değerler metin olmalı.</summary>
    public static Patch<Dictionary<string, string>?> StringMap(JsonElement root, string name, bool allowNull, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return Patch<Dictionary<string, string>?>.Unset;
        }

        if (element.ValueKind == JsonValueKind.Null && allowNull)
        {
            return Patch<Dictionary<string, string>?>.Of(null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors[name] = [$"{name} metin değerli bir nesne olmalıdır."];
            return Patch<Dictionary<string, string>?>.Unset;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                errors[name] = [$"{name} değerleri metin olmalıdır."];
                return Patch<Dictionary<string, string>?>.Unset;
            }

            var value = property.Value.GetString()!;
            if (TextRules.ContainsUnsafeCharacter(property.Name) || TextRules.ContainsUnsafeCharacter(value))
            {
                errors[name] = [$"{name} kontrol, biçim ya da satır ayırıcı karakter içeremez."];
                return Patch<Dictionary<string, string>?>.Unset;
            }

            map[property.Name] = value;
        }

        return Patch<Dictionary<string, string>?>.Of(map);
    }
}

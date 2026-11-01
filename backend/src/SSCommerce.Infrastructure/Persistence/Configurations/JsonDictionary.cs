using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>IReadOnlyDictionary&lt;string,string&gt;</c> alanını PostgreSQL <c>jsonb</c> sütununa eşler.
/// Karşılaştırıcı sözlük içeriğine bakar (referansa değil), böylece değişmeyen sözlük için gereksiz UPDATE üretilmez.
/// </summary>
internal static class JsonDictionary
{
    private static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> Converter = new(
        value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, (JsonSerializerOptions?)null)
                ?? new Dictionary<string, string>());

    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> Comparer = new(
        (a, b) => ReferenceEquals(a, b) || (a != null && b != null && AreEqual(a, b)),
        value => HashOf(value),
        value => new Dictionary<string, string>(value));

    public static PropertyBuilder AsJsonbDictionary(this PropertyBuilder property)
    {
        property.HasColumnType("jsonb");
        property.HasConversion(Converter, Comparer);
        return property;
    }

    private static bool AreEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other == kv.Value);

    private static int HashOf(IReadOnlyDictionary<string, string> value)
    {
        // Sıradan bağımsız karma: eşit sözlükler farklı sırada gelse de aynı kodu üretir.
        var hash = 0;
        foreach (var (key, val) in value)
        {
            hash ^= HashCode.Combine(key, val);
        }

        return hash;
    }
}

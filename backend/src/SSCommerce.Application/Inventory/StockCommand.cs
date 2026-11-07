using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SSCommerce.Domain.Inventory;

namespace SSCommerce.Application.Inventory;

/// <summary>Stok yazma uçlarının mantıksal işlem adı (parmak izine girer: aynı gövde başka uca gönderilirse farklı istek sayılır).</summary>
public static class StockOperations
{
    public const string Receive = "receive";
    public const string Adjust = "adjust";
}

/// <summary>
/// Doğrulanmış bir stok işlemi. <paramref name="ActorUserId"/> yalnız kimlik doğrulanmış principal'den gelir (gövdeden asla).
/// </summary>
public sealed record StockCommand(
    string Operation,
    Guid WarehouseId,
    Guid VariantId,
    int Delta,
    StockOperationType Type,
    string Reason,
    string? Reference,
    string OperationKey,
    Guid ActorUserId)
{
    /// <summary>
    /// İsteğin ANLAMININ SHA-256 özeti (64 küçük harfli hex): işlem, depo, varyant, delta, tür, kırpılmış neden, referans.
    /// Her alan uzunluk önekli yazılır (<c>3:abc;</c>), böylece alan sınırları kaydırılarak iki farklı istek aynı metne çıkarılamaz.
    ///
    /// Aktör ve zaman BİLEREK dışarıdadır: parmak izi "bu anahtar hangi stok işlemini temsil ediyor" sorusunu yanıtlar, "kim yaptı"
    /// sorusunu değil (o denetim kaydındaki <c>actor_user_id</c>'dir). Aynı anahtar + aynı içerik başka bir oturumdan/yöneticiden
    /// gelirse de tekrar sayılır ve yalnız İLK sonucu döner; hiçbir şey yazılmaz, yani ikinci kişiye aktör olarak sahiplik ya da
    /// ek yetki doğmaz. (Tüm yöneticiler zaten bütün hareketleri okuyabilir.)
    /// </summary>
    public string Fingerprint()
    {
        var canonical = new StringBuilder("v1;");
        Field(canonical, Operation);
        Field(canonical, WarehouseId.ToString("D"));
        Field(canonical, VariantId.ToString("D"));
        Field(canonical, Delta.ToString(CultureInfo.InvariantCulture));
        Field(canonical, StockOperationTypes.ToWire(Type));
        Field(canonical, Reason);
        Field(canonical, Reference);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Field(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-;");
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}

public enum StockOperationStatus
{
    /// <summary>Yeni uygulandı (201).</summary>
    Applied,

    /// <summary>Aynı anahtar + aynı içerik: ilk sonuç döndü, bakiye değişmedi (200).</summary>
    Replayed,

    /// <summary>Aynı anahtar, farklı içerik (409 idempotency_key_reused).</summary>
    KeyReused,

    WarehouseNotFound,
    VariantNotFound,
    WarehouseInactive,
    VariantInactive,

    /// <summary>Bakiye sıfırın altına düşerdi (409).</summary>
    InsufficientStock,

    /// <summary>Bakiye üst sınırı aşardı (409).</summary>
    StockLimitExceeded,

    /// <summary>Kilit zaman aşımı (503 stock_busy). Hiçbir şey yazılmadı; aynı anahtarla tekrar denenebilir.</summary>
    Busy,
}

/// <param name="Status">Sonuç durumu.</param>
/// <param name="Movement">Yalnız <see cref="StockOperationStatus.Applied"/> ve <see cref="StockOperationStatus.Replayed"/> durumlarında dolu.</param>
public sealed record StockOperationResult(StockOperationStatus Status, StockMovementDto? Movement);

public enum WarehouseWriteStatus
{
    Created,
    Updated,
    NotFound,
    CodeTaken,

    /// <summary>Kilit zaman aşımı ya da deadlock: hiçbir şey yazılmadı (503 busy).</summary>
    Busy,
}

public sealed record WarehouseWriteResult(WarehouseWriteStatus Status, WarehouseDto? Warehouse);

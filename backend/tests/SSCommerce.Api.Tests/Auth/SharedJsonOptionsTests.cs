using System.Reflection;
using System.Text.Json;
using SSCommerce.Api.Auth;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>
/// Regresyon (Faz 9 yük testi bulgusu): kayıt yanıtı paylaşılan <c>JsonSerializerOptions</c> ile <c>Results.Json</c> çağırıyordu. Dondurulmamış seçenek ilk
/// kullanımda değiştirilir; süreç yeni başlamışken eşzamanlı ilk istekler yarışınca biri HTTP 500 alıyordu. Yarış olasılıksal olduğundan düzeltme, durumun
/// kendisini (salt okunur + çözümleyici hazır) deterministik olarak sınar; ek olarak eşzamanlı ilk kullanım dayanıklılık testi vardır.
/// Saf testlerdir (veritabanı gerekmez, hızlıdır): bu yüzden unit düzeyindedir.
/// </summary>
public sealed class SharedJsonOptionsTests
{
    [Fact]
    public void The_shared_register_options_are_frozen_with_a_resolver_before_first_use()
    {
        var field = typeof(AuthEndpoints).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var options = Assert.IsType<JsonSerializerOptions>(field.GetValue(null));

        Assert.True(options.IsReadOnly);
        Assert.NotNull(options.TypeInfoResolver);
    }

    [Fact]
    public void Created_options_serialize_concurrently_from_the_first_use_without_throwing()
    {
        for (var round = 0; round < 50; round++)
        {
            var options = AuthEndpoints.CreateJsonOptions();
            var failures = new List<Exception>();
            using var gate = new ManualResetEventSlim();

            var threads = Enumerable.Range(0, 16).Select(worker => new Thread(() =>
            {
                gate.Wait();
                try
                {
                    // Results.Json, seçenekleri tam ilk kullanımda ele alan çağrıdır (üretim yolu).
                    _ = Microsoft.AspNetCore.Http.Results.Json(new { id = Guid.NewGuid(), email = "x@example.test" }, options);
                    _ = JsonSerializer.Serialize(new { a = 1 }, options);
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }
                }
            })).ToList();

            threads.ForEach(t => t.Start());
            gate.Set();
            threads.ForEach(t => t.Join());

            Assert.Empty(failures);
        }
    }
}

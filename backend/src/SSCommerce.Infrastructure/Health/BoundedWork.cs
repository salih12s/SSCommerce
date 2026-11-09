namespace SSCommerce.Infrastructure.Health;

/// <summary>
/// Sağlık kontrolleri için KESİN duvar saati sınırı. İptal belirteci kooperatiftir: .NET'in DNS çözümlemesi (durdurulmuş Docker konteynerinin adı) ve
/// bazı soket işlemleri belirteci beklemez, bu yüzden yalnız <c>CancelAfter</c> ile "3 sn" sözü tutulamaz (gözlenen: 5-7,7 sn).
/// Burada asıl iş ayrı bir Task olarak başlar; <see cref="Task.WhenAny(Task, Task)"/> ile sınır dolunca iş bitmese bile çağıran HEMEN döner.
/// Kopan işin istisnası gözlemlenir (UnobservedTaskException yok); iş kendi kaynaklarını (bağlantı, DbContext kapsamı) bittiğinde kendisi bırakır.
/// Gecikme <see cref="TimeProvider"/> ile kurulur: sahte saatli testlerde sınır belirlenimli kalır.
/// </summary>
public static class BoundedWork
{
    /// <summary>
    /// <paramref name="work"/> sınır içinde biterse sonucu (ya da istisnasını) verir; dolarsa <c>(false, default)</c> döner ve işi iptal etmeyi dener.
    /// Çağıranın kendi belirteci iptal edilirse <see cref="OperationCanceledException"/> fırlatır.
    /// </summary>
    public static async Task<(bool Completed, T? Result)> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan limit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Task.Run: iş çağıran iş parçacığını (ör. eşzamanlı DNS çözümlemesi) hiçbir koşulda tutamasın.
        var workToken = workCts.Token;
        var workTask = Task.Run(() => work(workToken), CancellationToken.None);
        var delayTask = Task.Delay(limit, timeProvider, delayCts.Token);

        var first = await Task.WhenAny(workTask, delayTask).ConfigureAwait(false);
        if (first == workTask)
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
            Observe(delayTask);
            return (true, await workTask.ConfigureAwait(false));
        }

        // Sınır doldu ya da çağıranın belirteci iptal edildi: işi bırak, sonucunu hiç beklemeden istisnasını gözlemle.
        await workCts.CancelAsync().ConfigureAwait(false);
        Observe(workTask);
        cancellationToken.ThrowIfCancellationRequested();
        return (false, default);
    }

    /// <summary>Beklenmeyen/sonradan gelen istisnayı tüketir; GC'de UnobservedTaskException üretmesin.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

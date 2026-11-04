using ZetPDF.Core.Localization;

namespace ZetPDF.Editing.Interop;

/// <summary>
/// Praca z COM na osobnym wątku STA, poza UI, z limitem czasu i anulowaniem. Wywołania COM nie da się przerwać, więc przy
/// przekroczeniu limitu lub anulowaniu wątek (w tle) zostaje porzucony, a <c>onAbandon</c> może np. zakończyć uruchomiony proces.
/// </summary>
internal static class StaThread
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, T> work, TimeSpan timeout, Action? onAbandon, string timeoutKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abandoned = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(work(abandoned.Token)); }
            catch (OperationCanceledException ex) { tcs.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "ZetPDF COM" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(timeout);
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.InfiniteTimeSpan, stop.Token)).ConfigureAwait(false);
        if (finished == tcs.Task) return await tcs.Task.ConfigureAwait(false);

        // Wynik porzuconego wątku nikogo już nie obchodzi; jego wyjątek nie może zostać nieobserwowany.
        _ = tcs.Task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try { abandoned.Cancel(); }
        catch (ObjectDisposedException) { }
        onAbandon?.Invoke();
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException("COM operation timed out.").WithMessageKey(timeoutKey);
    }
}

using System.Collections.Concurrent;
using PDFiumCore;

namespace ZetPDF.Rendering;

/// <summary>
/// Jedyny wątek, na którym wolno wołać PDFium (biblioteka nie jest wątkowo bezpieczna).
/// Zadania trafiają do kolejki FIFO; każde sprawdza token anulowania PRZED startem
/// (zadanie już rozpoczęte kończy się normalnie – dzięki temu uchwyty utworzone w zadaniu
/// są zawsze zwalniane w tym samym zadaniu, a wynik nie „wisi” bez właściciela).
/// Zadania wołane z samego wątku PDFium wykonują się od razu (brak zakleszczeń przy zagnieżdżeniu).
/// </summary>
internal sealed class PdfiumThread
{
    private static readonly Lazy<PdfiumThread> SharedInstance = new(() => new PdfiumThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;

    private PdfiumThread()
    {
        var ready = new ManualResetEventSlim();
        Exception? initError = null;
        _thread = new Thread(() =>
        {
            try { fpdfview.FPDF_InitLibrary(); }
            catch (Exception ex) { initError = ex; }
            ready.Set();
            if (initError is null) Loop();
        })
        {
            IsBackground = true,
            Name = "ZetPDF PDFium",
        };
        _thread.Start();
        ready.Wait();
        if (initError is not null) throw new InvalidOperationException("PDFium initialization failed.", initError);
    }

    /// <summary>Wspólny wątek PDFium procesu (biblioteka inicjowana raz, zwalniana przy końcu procesu).</summary>
    public static PdfiumThread Shared => SharedInstance.Value;

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    /// <summary>Liczba zadań oczekujących w kolejce (diagnostyka, testy).</summary>
    public int PendingCount => _queue.Count;

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        if (IsCurrentThread)
        {
            try { return Task.FromResult(work()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try { tcs.TrySetResult(work()); }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken.IsCancellationRequested ? oce.CancellationToken : ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken ct = default) =>
        InvokeAsync<bool>(() => { work(); return true; }, ct);

    /// <summary>Zadanie „wyślij i zapomnij” (np. zwolnienie uchwytów z Dispose/finalizatora). Wyjątki są połykane.</summary>
    public void Post(Action work)
    {
        if (IsCurrentThread)
        {
            try { work(); } catch { /* zwalnianie nie może wysadzić wątku */ }
            return;
        }
        _queue.Add(() =>
        {
            try { work(); } catch { /* jw. */ }
        });
    }

    private void Loop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item();
        }
    }
}

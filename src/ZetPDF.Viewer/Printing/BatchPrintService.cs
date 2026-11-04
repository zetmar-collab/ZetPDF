using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.Printing;

/// <summary>Wynik pliku w druku wsadowym.</summary>
public enum BatchPrintStatus { Pending, Printed, SkippedEncrypted, SkippedCorrupt, SkippedNoPrintPermission, SkippedUnreadable, Failed, Cancelled }

/// <summary>Plik po sprawdzeniu. Hasło żyje tylko w pamięci na czas tego druku.</summary>
public sealed class BatchPrintEntry
{
    public BatchPrintEntry(string path) => Path = path;

    public string Path { get; }
    public BatchPrintStatus Status { get; internal set; } = BatchPrintStatus.Pending;
    public string? Detail { get; internal set; }
    public bool NeedsPassword { get; internal set; }
    internal string? Password { get; set; }
}

/// <summary>Druk jednego otwartego dokumentu: do kolejki drukarki albo w testach do pliku XPS.</summary>
public delegate Task BatchPrintTarget(IRenderDocument document, PrintOptions options, string filePath, IProgress<OperationProgress>? progress, CancellationToken ct);

/// <summary>
/// Druk wsadowy: sprawdzenie plików (uszkodzone, zaszyfrowane, bez uprawnienia), hasła, potem druk po kolei. Błąd pliku nie przerywa reszty.
/// Pliki są wczytywane po jednym i zwalniane po wydruku.
/// </summary>
public sealed class BatchPrintService
{
    private readonly IPdfRenderer _renderer;

    public BatchPrintService(IPdfRenderer renderer) => _renderer = renderer;

    /// <summary>Etap 1 (w tle): ustala stan każdego pliku bez hasła.</summary>
    public async Task<IReadOnlyList<BatchPrintEntry>> ProbeAsync(IReadOnlyList<string> files, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var entries = new List<BatchPrintEntry>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new BatchPrintEntry(files[i]);
            entries.Add(entry);
            progress?.Report(OperationProgress.Of(i, files.Count, Path.GetFileName(files[i])));
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(files[i], ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Status = BatchPrintStatus.SkippedUnreadable;
                entry.Detail = ex.Message;
                continue;
            }
            Apply(entry, await _renderer.ProbeAsync(bytes, null, ct).ConfigureAwait(false));
        }
        return entries;
    }

    private static void Apply(BatchPrintEntry entry, PdfProbeResult probe)
    {
        switch (probe.Status)
        {
            case PdfProbeStatus.Corrupt:
                entry.Status = BatchPrintStatus.SkippedCorrupt;
                entry.Detail = probe.ErrorMessage;
                entry.NeedsPassword = false;
                break;
            case PdfProbeStatus.PasswordRequired:
            case PdfProbeStatus.InvalidPassword:
                entry.NeedsPassword = true;
                break;
            default:
                entry.NeedsPassword = false;
                if ((probe.Permissions & DocumentPermissions.Print) == 0) entry.Status = BatchPrintStatus.SkippedNoPrintPermission;
                break;
        }
    }

    /// <summary>
    /// Etap 2 (na wątku okna, poza oknem postępu): pytanie o hasła. Funkcja dostaje nazwę pliku i informację, czy to kolejna próba;
    /// null to pominięcie pliku. Po haśle sprawdzane jest też uprawnienie do druku.
    /// </summary>
    public async Task ResolvePasswordsAsync(IEnumerable<BatchPrintEntry> entries, Func<string, bool, string?> askPassword, CancellationToken ct = default)
    {
        foreach (var entry in entries.Where(e => e.NeedsPassword && e.Status == BatchPrintStatus.Pending))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(entry.Path, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Status = BatchPrintStatus.SkippedUnreadable;
                entry.Detail = ex.Message;
                continue;
            }
            bool retry = false;
            while (entry.NeedsPassword && entry.Status == BatchPrintStatus.Pending)
            {
                ct.ThrowIfCancellationRequested();
                var password = askPassword(Path.GetFileName(entry.Path), retry);
                if (password is null)
                {
                    entry.Status = BatchPrintStatus.SkippedEncrypted;
                    break;
                }
                Apply(entry, await _renderer.ProbeAsync(bytes, password, ct));
                if (!entry.NeedsPassword) entry.Password = password;
                retry = true;
            }
        }
    }

    /// <summary>Etap 3 (w tle): druk plików w stanie Pending. Po anulowaniu reszta plików dostaje stan Cancelled.</summary>
    public async Task PrintAsync(IReadOnlyList<BatchPrintEntry> entries, PrintOptions options, BatchPrintTarget target, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var toPrint = entries.Where(e => e.Status == BatchPrintStatus.Pending).ToList();
        for (int i = 0; i < toPrint.Count; i++)
        {
            var entry = toPrint[i];
            if (ct.IsCancellationRequested)
            {
                foreach (var rest in toPrint.Skip(i)) rest.Status = BatchPrintStatus.Cancelled;
                break;
            }
            int index = i;
            string name = Path.GetFileName(entry.Path);
            progress?.Report(OperationProgress.Of(index, toPrint.Count, name));
            var inner = progress is null ? null : new InlineProgress(p =>
                progress.Report(new OperationProgress((index + (p.Fraction ?? 0)) / toPrint.Count, name, index, toPrint.Count)));
            try
            {
                var bytes = await File.ReadAllBytesAsync(entry.Path, ct).ConfigureAwait(false);
                using var doc = await _renderer.OpenAsync(bytes, entry.Password, ct).ConfigureAwait(false);
                await target(doc, options, entry.Path, inner, ct).ConfigureAwait(false);
                entry.Status = BatchPrintStatus.Printed;
            }
            catch (OperationCanceledException)
            {
                foreach (var rest in toPrint.Skip(i)) rest.Status = BatchPrintStatus.Cancelled;
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                entry.Status = BatchPrintStatus.Failed;
                entry.Detail = ex.Message;
            }
            finally
            {
                entry.Password = null;
            }
        }
        progress?.Report(OperationProgress.Of(toPrint.Count, toPrint.Count));
    }

    /// <summary>Po anulowaniu okna postępu przed startem pętli: pliki, które nie doszły do druku, nie mogą wyglądać na niewykonane bez powodu.</summary>
    public void MarkUnfinishedCancelled(IEnumerable<BatchPrintEntry> entries)
    {
        foreach (var entry in entries.Where(e => e.Status == BatchPrintStatus.Pending)) entry.Status = BatchPrintStatus.Cancelled;
    }

    private sealed class InlineProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}

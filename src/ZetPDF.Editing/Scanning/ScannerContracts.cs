using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Editing.Interop;

namespace ZetPDF.Editing.Scanning;

public enum ScanColorMode { Color, Gray, Mono }

public sealed record ScannerInfo(string Id, string Name, bool HasFeeder);

public sealed record ScanSettings(string DeviceId, int Dpi, ScanColorMode Mode, bool UseFeeder);

/// <summary>
/// Dostęp do skanera. Metody blokują i mają działać na wątku STA (robi to <see cref="ScanWorkflow"/>).
/// Implementacja produkcyjna: <see cref="WiaScannerBackend"/>; w testach atrapa.
/// </summary>
public interface IScannerBackend
{
    /// <summary>Czy w systemie jest obsługa skanowania (WIA 2.0).</summary>
    bool IsAvailable { get; }
    IReadOnlyList<ScannerInfo> ListDevices();
    /// <summary>Podajnik: wszystkie strony z tacy. Szyba: jedna strona. Wynik: obrazy w formacie pliku (BMP, PNG…).</summary>
    IReadOnlyList<byte[]> Scan(ScanSettings settings, CancellationToken ct);
}

/// <summary>Przebieg skanowania bez UI: urządzenia, kolejne przebiegi, PDF przez istniejącą konwersję obrazów, opcjonalnie OCR.</summary>
public sealed class ScanWorkflow
{
    /// <summary>Limit jednego przebiegu; pełny podajnik w wysokiej rozdzielczości trwa długo.</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

    private readonly IScannerBackend _backend;

    public ScanWorkflow(IScannerBackend backend) => _backend = backend;

    public Task<IReadOnlyList<ScannerInfo>> ListDevicesAsync(CancellationToken ct = default) =>
        StaThread.RunAsync(_ => _backend.ListDevices(), ListTimeout, null, "Edit.Scan.Error.Timeout", ct);

    /// <summary>Jeden przebieg na wątku STA.</summary>
    public Task<IReadOnlyList<byte[]>> ScanOnceAsync(ScanSettings settings, CancellationToken ct) =>
        StaThread.RunAsync(token => _backend.Scan(settings, token), ScanTimeout, null, "Edit.Scan.Error.Timeout", ct);

    /// <summary>
    /// Zbiera strony. Podajnik: jeden przebieg. Szyba: po każdej stronie <paramref name="askNext"/> (liczba zebranych) decyduje, czy skanować dalej.
    /// <paramref name="run"/> wykonuje przebieg w oknie postępu i zwraca false po anulowaniu; wtedy strony już zebrane zostają.
    /// </summary>
    public async Task<List<byte[]>> CollectAsync(ScanSettings settings, Func<Func<CancellationToken, Task>, Task<bool>> run, Func<int, bool> askNext)
    {
        var pages = new List<byte[]>();
        while (true)
        {
            IReadOnlyList<byte[]> batch = Array.Empty<byte[]>();
            bool done = await run(async ct => batch = await ScanOnceAsync(settings, ct).ConfigureAwait(false)).ConfigureAwait(true);
            if (!done) return pages;
            pages.AddRange(batch);
            if (settings.UseFeeder || batch.Count == 0 || !askNext(pages.Count)) return pages;
        }
    }

    /// <summary>Strony → PDF przez <see cref="IConversionService.ImagesToPdfAsync"/>, potem opcjonalnie warstwa OCR. Pliki pośrednie są usuwane.</summary>
    public static async Task<byte[]> BuildPdfAsync(IReadOnlyList<byte[]> pages, IConversionService conversion, string tempDirectory, IOcrService? ocr, OcrOptions? ocrOptions, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (pages.Count == 0) throw new InvalidOperationException("No pages.").WithMessageKey("Edit.Error.NoImages");
        var dir = Path.Combine(tempDirectory, "scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var files = new List<string>();
            for (int i = 0; i < pages.Count; i++)
            {
                var file = Path.Combine(dir, i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".img");
                await File.WriteAllBytesAsync(file, pages[i], ct).ConfigureAwait(false);
                files.Add(file);
            }
            var pdf = await conversion.ImagesToPdfAsync(files, new ImageToPdfOptions { PageSize = ImagePageSize.FitImage }, progress, ct).ConfigureAwait(false);
            if (ocr is not null && ocrOptions is not null) pdf = await ocr.OcrDocumentAsync(pdf, ocrOptions, progress, ct).ConfigureAwait(false);
            return pdf;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

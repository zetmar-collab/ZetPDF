using System.Runtime.CompilerServices;
using System.Windows;
using Tesseract;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Ocr.Pdf;

namespace ZetPDF.Ocr;

/// <summary>
/// OCR na Tesseract 5.2.0 (dane pol + eng). Strona renderowana przez PDFium (<see cref="IPdfRenderer"/>, domyślnie 300 DPI,
/// bez adnotacji) → 8-bitowy BMP w skali szarości → Tesseract → słowa z prostokątami i pewnością → ukryta warstwa tekstu
/// (tryb renderowania 3, czcionka Arial osadzona jako Identity-H z ToUnicode) zapisana jako osobny strumień treści
/// ze znacznikiem "ocr" – ponowne OCR strony zastępuje poprzednią warstwę ZetPDF zamiast ją dublować.
/// Silnik tworzony osobno dla każdego zadania (TesseractEngine nie jest wątkowo bezpieczny).
/// </summary>
public sealed class TesseractOcrService : IOcrService
{
    /// <summary>Znacznik strumienia warstwy OCR całej strony.</summary>
    public const string LayerMarker = "ocr";
    /// <summary>Znacznik strumienia warstwy OCR obszaru (nie zastępuje warstwy strony).</summary>
    public const string RegionLayerMarker = "ocr-region";

    private static readonly ConditionalWeakTable<OcrResult, object> RegionResults = new();
    private static readonly string[] KnownLanguages = { "pol", "eng" };

    private readonly TesseractBootstrap _boot;
    private readonly IPdfRenderer _renderer;
    private readonly ILocalizationService? _loc;

    public TesseractOcrService(TesseractBootstrap boot, IPdfRenderer renderer, ILocalizationService? loc = null)
    {
        _boot = boot;
        _renderer = renderer;
        _loc = loc;
    }

    /// <summary>Oznacza wynik jako OCR obszaru (warstwa dopisywana bez usuwania istniejącej warstwy strony).</summary>
    public static OcrResult AsRegionResult(OcrResult result)
    {
        RegionResults.AddOrUpdate(result, new object());
        return result;
    }

    public static bool IsRegionResult(OcrResult result) => RegionResults.TryGetValue(result, out _);

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var engine = _boot.CreateEngine("pol+eng");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }, ct);

    public Task<OcrResult> RecognizeAsync(byte[] pdf, OcrOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(options);
        var languages = NormalizeLanguages(options.Languages);
        return Task.Run(async () =>
        {
            using var doc = await _renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false);
            var pages = (options.Pages ?? Enumerable.Range(0, doc.PageCount).ToList())
                .Where(p => p >= 0 && p < doc.PageCount).Distinct().ToList();
            var results = new List<OcrPageResult>(pages.Count);
            using var engine = _boot.CreateEngine(languages);
            int dpi = Math.Clamp(options.Dpi <= 0 ? 300 : options.Dpi, 72, 600);
            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int pageIndex = pages[i];
                progress?.Report(OperationProgress.Of(i, pages.Count, Text("Ocr.Progress.Page", pageIndex + 1, pages.Count)));
                if (options.SkipPagesWithText && options.Region is null)
                {
                    var text = await doc.GetPageTextAsync(pageIndex, ct).ConfigureAwait(false);
                    if (HasText(text))
                    {
                        results.Add(new OcrPageResult(pageIndex, Array.Empty<OcrWord>(), 0, Skipped: true));
                        continue;
                    }
                }
                var bitmap = await doc.RenderPageAsync(pageIndex, new RenderRequest(dpi / 72.0, 0, null, RenderFlags.None), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var info = doc.Pages[pageIndex];
                var transform = new PageTransform(info.Size, info.CropOrigin, info.Rotation, bitmap.Scale, new Point(0, 0));
                var (words, mean) = Recognize(engine, bitmap, options.Region, transform, options.SuspectThreshold, (px, py) => transform.ToPage(new Point(px, py)));
                results.Add(new OcrPageResult(pageIndex, words, mean, Skipped: false));
            }
            progress?.Report(OperationProgress.Of(pages.Count, pages.Count, Text("Ocr.Progress.Done")));
            var result = new OcrResult(results);
            if (options.Region is not null) AsRegionResult(result);
            return result;
        }, ct);
    }

    public Task<byte[]> ApplyTextLayerAsync(byte[] pdf, OcrResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool region = IsRegionResult(result);
        return PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            foreach (var pr in result.Pages)
            {
                ct.ThrowIfCancellationRequested();
                if (pr.Skipped || pr.PageIndex < 0 || pr.PageIndex >= doc.PageCount) continue;
                var page = doc.Pages[pr.PageIndex];
                if (!region) PageContent.RemoveMarked(page, LayerMarker);
                var words = BuildLayerWords(page, pr.Words);
                TextLayerWriter.Write(page, words, LayerStyle.HiddenText(region ? RegionLayerMarker : LayerMarker));
            }
        }, ct);
    }

    public async Task<byte[]> OcrDocumentAsync(byte[] pdf, OcrOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var result = await RecognizeAsync(pdf, options, progress, ct).ConfigureAwait(false);
        if (!result.Pages.Any(p => !p.Skipped && p.Words.Count > 0)) return pdf;
        return await ApplyTextLayerAsync(pdf, result, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<OcrWord>> RecognizeBitmapAsync(RenderedBitmap bitmap, string languages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var langs = NormalizeLanguages(languages);
        return Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            using var engine = _boot.CreateEngine(langs);
            double s = bitmap.Scale > 0 ? bitmap.Scale : 1;
            // Punkty względem bitmapy: początek w lewym dolnym rogu, oś Y w górę (jak w PDF).
            var (words, _) = Recognize(engine, bitmap, null, null, 70, (px, py) => new PdfPoint(px / s, (bitmap.Height - py) / s));
            return words;
        }, ct);
    }

    // ------------------------------------------------------------------ wewnętrzne

    /// <summary>Słowa OCR → słowa warstwy (linia bazowa i rozmiar z wysokości linii, skala pozioma do szerokości słowa).</summary>
    internal static List<LayerWord> BuildLayerWords(PdfSharp.Pdf.PdfPage page, IReadOnlyList<OcrWord> words)
    {
        var t = PageGeometry.Transform(page);
        var list = new List<LayerWord>(words.Count);
        foreach (var line in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).GroupBy(w => w.LineIndex))
        {
            var rects = line.Select(w => (Word: w, View: t.ToView(w.Box))).ToList();
            double top = rects.Min(r => r.View.Top), bottom = rects.Max(r => r.View.Bottom);
            double h = Math.Max(1, bottom - top);
            double fontSize = h * 0.92;
            double baseline = bottom - h * 0.17;
            foreach (var (w, v) in rects)
            {
                var origin = t.ToPage(new Point(v.Left, baseline));
                var end = t.ToPage(new Point(v.Left + 1, baseline));
                list.Add(new LayerWord(w.Text.Trim(), origin, fontSize, new PdfPoint(end.X - origin.X, end.Y - origin.Y), v.Width));
            }
        }
        return list;
    }

    private static (IReadOnlyList<OcrWord> Words, float Mean) Recognize(TesseractEngine engine, RenderedBitmap bitmap, PdfRect? region,
        PageTransform? transform, int threshold, Func<double, double, PdfPoint> toPage)
    {
        int offX = 0, offY = 0;
        var source = bitmap;
        if (region is { } reg && transform is not null)
        {
            // Obszar wycinany do osobnej bitmapy z białym marginesem (Tesseract na wąskim pasku w trybie Auto daje śmieci).
            var r = transform.ToView(reg);
            int x = (int)Math.Floor(Math.Max(0, r.Left)), y = (int)Math.Floor(Math.Max(0, r.Top));
            int x2 = (int)Math.Ceiling(Math.Min(bitmap.Width, r.Right)), y2 = (int)Math.Ceiling(Math.Min(bitmap.Height, r.Bottom));
            if (x2 - x < 4 || y2 - y < 4) return (Array.Empty<OcrWord>(), 0);
            const int pad = 32;
            source = Crop(bitmap, x, y, x2 - x, y2 - y, pad);
            offX = x - pad;
            offY = y - pad;
        }
        var bmp = GrayBitmap.ToBmp8(source);
        using var pix = Pix.LoadFromMemory(bmp);
        var page = engine.Process(pix, region is null ? PageSegMode.Auto : PageSegMode.SingleBlock);
        using (page)
        {
            var words = new List<OcrWord>();
            int line = -1;
            using var it = page.GetIterator();
            it.Begin();
            do
            {
                if (it.IsAtBeginningOf(PageIteratorLevel.TextLine)) line++;
                if (!it.TryGetBoundingBox(PageIteratorLevel.Word, out var rc)) continue;
                var text = it.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                float conf = it.GetConfidence(PageIteratorLevel.Word);
                var a = toPage(rc.X1 + offX, rc.Y1 + offY);
                var b = toPage(rc.X2 + offX, rc.Y2 + offY);
                words.Add(new OcrWord(text, PdfRect.FromPoints(a, b), conf, Math.Max(0, line), conf < threshold));
            } while (it.Next(PageIteratorLevel.Word));
            return (words, page.GetMeanConfidence() * 100f);
        }
    }

    private static RenderedBitmap Crop(RenderedBitmap b, int x, int y, int w, int h, int pad)
    {
        int nw = w + 2 * pad, nh = h + 2 * pad, stride = nw * 4;
        var data = new byte[stride * nh];
        Array.Fill(data, (byte)255);
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(b.Bgra, (y + row) * b.Stride + x * 4, data, (row + pad) * stride + pad * 4, w * 4);
        return new RenderedBitmap(nw, nh, stride, data, b.Scale);
    }

    private static bool HasText(PageText text)
    {
        int n = 0;
        foreach (var c in text.Chars)
        {
            if (!c.IsGenerated && char.IsLetterOrDigit(c.Char) && ++n >= 3) return true;
        }
        return false;
    }

    private static string NormalizeLanguages(string? languages)
    {
        var parts = (languages ?? "pol+eng").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => KnownLanguages.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(p => p.ToLowerInvariant()).Distinct().ToList();
        return parts.Count == 0 ? "pol+eng" : string.Join('+', parts);
    }

    private string? Text(string key, params object?[] args) => _loc is null ? null : (args.Length == 0 ? _loc[key] : _loc.Format(key, args));
}

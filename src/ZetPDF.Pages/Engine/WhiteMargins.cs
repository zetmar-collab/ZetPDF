using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Engine;

/// <summary>
/// Wykrywanie białych marginesów: render strony (PDFium przez IPdfRenderer) i ramka pikseli niebiałych.
/// Wynik w ułamkach rozmiaru wyświetlanego strony (0..1, Y w górę) – niezależny od skali renderu.
/// </summary>
internal static class WhiteMargins
{
    /// <summary>Próg: piksel jest „treścią”, gdy którykolwiek kanał &lt; 245.</summary>
    private const byte Threshold = 245;

    public static async Task<Dictionary<int, PdfRect?>> DetectAsync(IPdfRenderer renderer, byte[] pdf, IReadOnlyList<int> pages, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var result = new Dictionary<int, PdfRect?>();
        using var doc = await renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false);
        var list = pages.Distinct().Where(p => p >= 0 && p < doc.PageCount).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(i, list.Count));
            int p = list[i];
            var size = doc.Pages[p].DisplaySize;
            // ok. 100 DPI, ale nie więcej niż ~2500 px na bok
            double scale = Math.Min(100.0 / 72.0, 2500.0 / Math.Max(1, Math.Max(size.Width, size.Height)));
            var bmp = await doc.RenderPageAsync(p, new RenderRequest(scale, Flags: RenderFlags.None), ct).ConfigureAwait(false);
            result[p] = Bounds(bmp);
        }
        progress?.Report(OperationProgress.Of(list.Count, list.Count));
        return result;
    }

    /// <summary>Ramka treści w ułamkach (null = strona pusta).</summary>
    internal static PdfRect? Bounds(RenderedBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var px = bmp.Bgra;
        for (int y = 0; y < bmp.Height; y++)
        {
            int row = y * bmp.Stride;
            for (int x = 0; x < bmp.Width; x++)
            {
                int o = row + x * 4;
                if (px[o] < Threshold || px[o + 1] < Threshold || px[o + 2] < Threshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < 0) return null;
        double w = bmp.Width, h = bmp.Height;
        return new PdfRect(minX / w, 1 - (maxY + 1) / h, (maxX + 1) / w, 1 - minY / h);
    }
}

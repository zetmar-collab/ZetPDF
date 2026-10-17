using ZetPDF.Core.Contracts;
using ZetPDF.Rendering;

namespace ZetPDF.Annotations.Tests;

/// <summary>Render PDFium (jak w aplikacji: adnotacje + pola formularza) i liczenie pikseli niebiałych w obszarze strony.</summary>
internal static class RenderCheck
{
    private static readonly PdfiumRenderer Renderer = new();

    public static async Task<int> NonWhitePixels(byte[] pdf, int page, PdfRect area, RenderFlags flags = RenderFlags.Default)
    {
        using var doc = await Renderer.OpenAsync(pdf);
        var info = doc.Pages[page];
        var bmp = await doc.RenderPageAsync(page, new RenderRequest(1.0, Flags: flags));
        int x0 = (int)Math.Floor(area.Left - info.CropOrigin.X), x1 = (int)Math.Ceiling(area.Right - info.CropOrigin.X);
        int y0 = (int)Math.Floor(info.Size.Height - (area.Top - info.CropOrigin.Y)), y1 = (int)Math.Ceiling(info.Size.Height - (area.Bottom - info.CropOrigin.Y));
        int count = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(bmp.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(bmp.Width, x1); x++)
            {
                int o = y * bmp.Stride + x * 4;
                if (bmp.Bgra[o] < 230 || bmp.Bgra[o + 1] < 230 || bmp.Bgra[o + 2] < 230) count++;
            }
        return count;
    }
}

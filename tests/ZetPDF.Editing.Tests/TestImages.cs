using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZetPDF.Editing.Tests;

/// <summary>Obrazy testowe generowane w kodzie (WIC).</summary>
internal static class TestImages
{
    private static byte[] Pixels(int w, int h, bool alpha, byte r = 200, byte g = 60, byte b = 20)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r;
            px[i * 4 + 3] = (byte)(alpha ? (i % w) * 255 / Math.Max(1, w - 1) : 255);
        }
        return px;
    }

    private static byte[] Encode(BitmapEncoder enc, params BitmapSource[] frames)
    {
        foreach (var f in frames) enc.Frames.Add(BitmapFrame.Create(f));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource Source(int w, int h, bool alpha, double dpi = 96, byte r = 200) =>
        BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgra32, null, Pixels(w, h, alpha, r), w * 4);

    public static byte[] Png(int w, int h, bool alpha) => Encode(new PngBitmapEncoder(), Source(w, h, alpha));
    public static byte[] Jpeg(int w, int h) => Encode(new JpegBitmapEncoder { QualityLevel = 90 }, Source(w, h, false));
    public static byte[] Bmp(int w, int h) => Encode(new BmpBitmapEncoder(), Source(w, h, false));
    public static byte[] Tiff(int frames, int w, int h, double dpi = 72) =>
        Encode(new TiffBitmapEncoder(), Enumerable.Range(0, frames).Select(i => Source(w, h, false, dpi, (byte)(50 * i))).ToArray());
}

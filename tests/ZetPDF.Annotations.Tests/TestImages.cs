using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZetPDF.Annotations.Tests;

internal static class TestImages
{
    /// <summary>PNG w×h: czerwony prostokąt z przezroczystym marginesem.</summary>
    public static byte[] RedPng(int w, int h)
    {
        byte[]? result = null;
        var t = new Thread(() =>
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool inner = x > 2 && y > 2 && x < w - 3 && y < h - 3;
                    px[o] = 0; px[o + 1] = 0; px[o + 2] = inner ? (byte)255 : (byte)0; px[o + 3] = inner ? (byte)255 : (byte)0;
                }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            result = ms.ToArray();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result!;
    }
}

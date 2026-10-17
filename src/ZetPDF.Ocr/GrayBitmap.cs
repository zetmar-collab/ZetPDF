using ZetPDF.Core.Contracts;

namespace ZetPDF.Ocr;

/// <summary>Konwersja bitmapy BGRA z renderera do 8-bitowego BMP w skali szarości (wejście Tesseract/Leptonica).</summary>
internal static class GrayBitmap
{
    public static byte[] ToBmp8(RenderedBitmap b)
    {
        int row = (b.Width + 3) & ~3;
        int dataSize = row * b.Height;
        int offset = 14 + 40 + 256 * 4;
        var bytes = new byte[offset + dataSize];
        using (var ms = new MemoryStream(bytes))
        using (var w = new BinaryWriter(ms))
        {
            w.Write((byte)'B'); w.Write((byte)'M');
            w.Write(offset + dataSize); w.Write(0); w.Write(offset);
            w.Write(40); w.Write(b.Width); w.Write(-b.Height); // wiersze od góry
            w.Write((short)1); w.Write((short)8); w.Write(0); w.Write(dataSize);
            int ppm = (int)Math.Round(b.Scale * 72 / 0.0254);
            w.Write(ppm); w.Write(ppm); w.Write(256); w.Write(256);
            for (int i = 0; i < 256; i++) { w.Write((byte)i); w.Write((byte)i); w.Write((byte)i); w.Write((byte)0); }
        }
        var src = b.Bgra;
        Parallel.For(0, b.Height, y =>
        {
            int s = y * b.Stride;
            int d = offset + y * row;
            for (int x = 0; x < b.Width; x++, s += 4)
            {
                // premultiplied BGRA na białym tle: dołóż biel tam, gdzie alfa < 255
                int a = src[s + 3];
                int bl = src[s] + (255 - a), g = src[s + 1] + (255 - a), r = src[s + 2] + (255 - a);
                int v = (r * 299 + g * 587 + bl * 114) / 1000;
                bytes[d + x] = (byte)Math.Clamp(v, 0, 255);
            }
        });
        return bytes;
    }
}

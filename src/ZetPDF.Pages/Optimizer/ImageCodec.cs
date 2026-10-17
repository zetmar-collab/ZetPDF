using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using ZetPDF.Pages.Engine;

namespace ZetPDF.Pages.Optimizer;

/// <summary>Piksele obrazu 8 bitów na składową, 1 (szarość) lub 3 (RGB) kanały, wiersze od góry.</summary>
internal sealed record RawImage(int Width, int Height, int Channels, byte[] Pixels, bool WasJpeg);

/// <summary>
/// Dekodowanie/kodowanie obrazów XObject: DCTDecode (WPF/WIC), FlateDecode (z predyktorami PNG), bez filtra.
/// Obsługiwane przestrzenie: DeviceGray/DeviceRGB/CalGray/CalRGB/ICCBased N=1|3, 8 bitów. Inne (CMYK, JPX, CCITT,
/// maski, /Decode) → null (obraz pozostaje bez zmian).
/// </summary>
internal static class ImageCodec
{
    public static int? Channels(PdfItem? colorSpace)
    {
        switch (PdfIo.Resolve(colorSpace))
        {
            case PdfName n:
                return n.Value switch { "/DeviceGray" or "/G" => 1, "/DeviceRGB" or "/RGB" => 3, _ => null };
            case PdfArray a when a.Elements.Count >= 1 && PdfIo.Resolve(a.Elements[0]) is PdfName kind:
                switch (kind.Value)
                {
                    case "/CalGray": return 1;
                    case "/CalRGB": return 3;
                    case "/ICCBased" when a.Elements.Count >= 2 && PdfIo.Resolve(a.Elements[1]) is PdfDictionary icc:
                        var nComp = (int)(PdfIo.GetNumber(icc.Elements["/N"]) ?? 0);
                        return nComp is 1 or 3 ? nComp : null;
                }
                break;
        }
        return null;
    }

    public static string[] Filters(PdfDictionary d) => PdfIo.Resolve(d.Elements["/Filter"]) switch
    {
        PdfName n => new[] { n.Value },
        PdfArray a => a.Elements.Select(e => (PdfIo.Resolve(e) as PdfName)?.Value ?? "?").ToArray(),
        _ => Array.Empty<string>(),
    };

    public static RawImage? Decode(PdfDictionary img, int? channelsOverride = null)
    {
        if (img.Stream is null) return null;
        if (PdfIo.Resolve(img.Elements["/ImageMask"]) is PdfBoolean { Value: true }) return null;
        if (img.Elements.ContainsKey("/Decode")) return null;
        if (PdfIo.Resolve(img.Elements["/Mask"]) is PdfArray) return null; // maska kolorem – stratna kompresja by ją zepsuła
        int w = (int)(PdfIo.GetNumber(img.Elements["/Width"]) ?? 0), h = (int)(PdfIo.GetNumber(img.Elements["/Height"]) ?? 0);
        int bpc = (int)(PdfIo.GetNumber(img.Elements["/BitsPerComponent"]) ?? 8);
        if (w <= 0 || h <= 0 || bpc != 8 || (long)w * h > 60_000_000) return null;
        int? ch = channelsOverride ?? Channels(img.Elements["/ColorSpace"]);
        var filters = Filters(img);
        var data = img.Stream.Value;
        try
        {
            if (filters.Length == 1 && filters[0] is "/DCTDecode" or "/DCT")
            {
                var dec = new JpegBitmapDecoder(new MemoryStream(data, false), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = dec.Frames[0];
                if (frame.Format == PixelFormats.Cmyk32) return null;
                int c = ch ?? (frame.Format == PixelFormats.Gray8 ? 1 : 3);
                if (c is not (1 or 3)) return null;
                BitmapSource src = new FormatConvertedBitmap(frame, c == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24, null, 0);
                if (src.PixelWidth != w || src.PixelHeight != h) return null;
                int stride = w * c;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                return new RawImage(w, h, c, px, true);
            }
            if (ch is null) return null;
            byte[] raw;
            if (filters.Length == 0) raw = data;
            else if (filters.Length == 1 && filters[0] is "/FlateDecode" or "/Fl")
            {
                raw = Inflate(data);
                var parms = PdfIo.Resolve(img.Elements["/DecodeParms"]) as PdfDictionary;
                int predictor = (int)(PdfIo.GetNumber(parms?.Elements["/Predictor"]) ?? 1);
                if (predictor >= 10) raw = UnPng(raw, w, h, ch.Value);
                else if (predictor != 1) return null;
            }
            else return null;
            if (raw.Length < w * h * ch.Value) return null;
            if (raw.Length > w * h * ch.Value) raw = raw.AsSpan(0, w * h * ch.Value).ToArray();
            return new RawImage(w, h, ch.Value, raw, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data, false), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>Odwrócenie predyktorów PNG (każdy wiersz z bajtem typu filtra).</summary>
    private static byte[] UnPng(byte[] data, int w, int h, int bpp)
    {
        int stride = w * bpp;
        var output = new byte[stride * h];
        var prev = new byte[stride];
        int pos = 0;
        for (int y = 0; y < h && pos < data.Length; y++)
        {
            int type = data[pos++];
            var row = output.AsSpan(y * stride, stride);
            int len = Math.Min(stride, data.Length - pos);
            data.AsSpan(pos, len).CopyTo(row);
            pos += stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                row[x] = type switch
                {
                    1 => (byte)(row[x] + a),
                    2 => (byte)(row[x] + b),
                    3 => (byte)(row[x] + (a + b) / 2),
                    4 => (byte)(row[x] + Paeth(a, b, c)),
                    _ => row[x],
                };
            }
            row.CopyTo(prev);
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static byte[] EncodeJpeg(RawImage img, int quality)
    {
        var fmt = img.Channels == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
        var src = BitmapSource.Create(img.Width, img.Height, 72, 72, fmt, null, img.Pixels, img.Width * img.Channels);
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Zmniejszenie uśrednianiem obszarów (filtr pudełkowy).</summary>
    public static RawImage Downsample(RawImage img, int newW, int newH)
    {
        newW = Math.Clamp(newW, 1, img.Width);
        newH = Math.Clamp(newH, 1, img.Height);
        if (newW == img.Width && newH == img.Height) return img;
        int c = img.Channels;
        var outPx = new byte[newW * newH * c];
        var sum = new long[c];
        for (int y = 0; y < newH; y++)
        {
            int y0 = (int)((long)y * img.Height / newH), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * img.Height / newH));
            for (int x = 0; x < newW; x++)
            {
                int x0 = (int)((long)x * img.Width / newW), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * img.Width / newW));
                Array.Clear(sum);
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * img.Width * c;
                    for (int xx = x0; xx < x1; xx++)
                        for (int k = 0; k < c; k++) sum[k] += img.Pixels[row + xx * c + k];
                }
                long n = (long)(y1 - y0) * (x1 - x0);
                for (int k = 0; k < c; k++) outPx[(y * newW + x) * c + k] = (byte)(sum[k] / n);
            }
        }
        return img with { Width = newW, Height = newH, Pixels = outPx };
    }

    /// <summary>RGB → szarość (luminancja Rec. 601).</summary>
    public static RawImage ToGray(RawImage img)
    {
        if (img.Channels == 1) return img;
        var g = new byte[img.Width * img.Height];
        for (int i = 0, j = 0; i < g.Length; i++, j += 3)
            g[i] = Luma(img.Pixels[j], img.Pixels[j + 1], img.Pixels[j + 2]);
        return img with { Channels = 1, Pixels = g };
    }

    public static byte Luma(int r, int g, int b) => (byte)Math.Clamp((int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);

    /// <summary>Zapisuje piksele do słownika obrazu (JPEG lub Flate) i ustawia wymiary.</summary>
    public static void Write(PdfDictionary img, RawImage raw, bool jpeg, int quality, bool setGray)
    {
        byte[] data = jpeg ? EncodeJpeg(raw, quality) : Deflate(raw.Pixels);
        img.Stream.Value = data;
        img.Elements["/Filter"] = new PdfName(jpeg ? "/DCTDecode" : "/FlateDecode");
        img.Elements.Remove("/DecodeParms");
        img.Elements.SetInteger("/Width", raw.Width);
        img.Elements.SetInteger("/Height", raw.Height);
        img.Elements.SetInteger("/BitsPerComponent", 8);
        img.Elements.SetInteger("/Length", data.Length);
        if (setGray) img.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
    }
}

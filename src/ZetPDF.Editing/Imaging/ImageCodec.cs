using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Imaging;

/// <summary>Obraz przygotowany dla PDFsharp: bajty w formacie, który PDFsharp czyta (JPEG baseline lub PNG), rozmiar i DPI.</summary>
internal sealed record PreparedImage(byte[] Data, int Width, int Height, double DpiX, double DpiY);

/// <summary>
/// Dekodowanie i kodowanie obrazów przez WIC (WPF – JPG/PNG/TIFF wielostronicowy/BMP/GIF). PDFsharp 6 (Core) czyta tylko
/// część wariantów JPEG/PNG, więc wszystko poza JPEG-iem, który PDFsharp przyjmuje bez zmian, jest przekodowywane do PNG
/// (z kanałem alfa). Obiekty WPF tworzone i zamrażane lokalnie – działa na wątkach puli.
/// </summary>
internal static class ImageCodec
{
    /// <summary>Wszystkie klatki obrazu (TIFF wielostronicowy → wiele).</summary>
    public static List<PreparedImage> PrepareFrames(byte[] data)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        bool isJpeg = decoder is JpegBitmapDecoder;
        var list = new List<PreparedImage>();
        foreach (var frame in decoder.Frames)
        {
            double dx = frame.DpiX > 1 ? frame.DpiX : 96, dy = frame.DpiY > 1 ? frame.DpiY : 96;
            if (isJpeg && decoder.Frames.Count == 1 && PdfSharpAccepts(data))
            {
                list.Add(new PreparedImage(data, frame.PixelWidth, frame.PixelHeight, dx, dy));
                continue;
            }
            list.Add(new PreparedImage(EncodePng(frame), frame.PixelWidth, frame.PixelHeight, dx, dy));
        }
        return list;
    }

    public static PreparedImage PrepareForPdf(byte[] data) => PrepareFrames(data)[0];

    public static byte[] EncodePng(BitmapSource source)
    {
        BitmapSource src = source;
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Bgr24 && src.Format != PixelFormats.Bgr32)
        {
            src = new FormatConvertedBitmap(src, HasAlpha(src.Format) ? PixelFormats.Bgra32 : PixelFormats.Bgr24, null, 0);
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static bool HasAlpha(PixelFormat f) =>
        f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Prgba64 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba128Float || f == PixelFormats.Rgba128Float || f == PixelFormats.Indexed8 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed1;

    private static bool PdfSharpAccepts(byte[] data)
    {
        try
        {
            XImage.FromStream(new MemoryStream(data));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Bitmapa z renderera → BitmapSource (zamrożony).</summary>
    public static BitmapSource ToBitmapSource(RenderedBitmap b, double dpi)
    {
        var src = BitmapSource.Create(b.Width, b.Height, dpi, dpi, PixelFormats.Pbgra32, null, b.Bgra, b.Stride);
        src.Freeze();
        return src;
    }

    /// <summary>Koduje bitmapę do formatu eksportu.</summary>
    public static BitmapEncoder CreateEncoder(ImageFormat format, int jpegQuality) => format switch
    {
        ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
        ImageFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        ImageFormat.Bmp => new BmpBitmapEncoder(),
        _ => new PngBitmapEncoder(),
    };

    /// <summary>Klatka do zapisu: dla formatów bez alfy (JPEG, BMP) – konwersja do Bgr24 (tło białe z renderu).</summary>
    public static BitmapFrame Frame(RenderedBitmap b, double dpi, ImageFormat format)
    {
        BitmapSource src = ToBitmapSource(b, dpi);
        if (format is ImageFormat.Jpeg or ImageFormat.Bmp or ImageFormat.Tiff)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
            conv.Freeze();
            src = conv;
        }
        return BitmapFrame.Create(src);
    }

    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Tiff => ".tif",
        ImageFormat.Bmp => ".bmp",
        _ => ".png",
    };
}

using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ZetPDF.Core.Documents;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Pages.Tests;

/// <summary>Weryfikacja wyników w pliku: PdfPig (tekst, liczba stron), PDFsharp (słowniki), surowe bajty.</summary>
internal static class Pdf
{
    public static int PageCount(byte[] pdf)
    {
        using var d = Pig.Open(pdf);
        return d.NumberOfPages;
    }

    /// <summary>Tekst strony (1-based jak PdfPig → tu 0-based).</summary>
    public static string Text(byte[] pdf, int pageIndex)
    {
        using var d = Pig.Open(pdf);
        return d.GetPage(pageIndex + 1).Text;
    }

    public static string AllText(byte[] pdf)
    {
        using var d = Pig.Open(pdf);
        return string.Join("\n", d.GetPages().Select(p => p.Text));
    }

    /// <summary>Numery „Strona N” w kolejności stron.</summary>
    public static int[] PageNumbers(byte[] pdf)
    {
        using var d = Pig.Open(pdf);
        return d.GetPages().Select(p =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(p.Text, @"Strona\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }).ToArray();
    }

    public static T Read<T>(byte[] pdf, Func<PdfSharp.Pdf.PdfDocument, T> f) => PdfSharpEdit.Read(pdf, f);

    public static string Raw(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    /// <summary>Wszystkie strumienie rozpakowane + słowniki jako tekst (do szukania usuniętych treści).</summary>
    public static string Deep(byte[] pdf) => PdfSharpEdit.Read(pdf, doc =>
    {
        var sb = new StringBuilder();
        foreach (var o in doc.Internals.GetAllObjects())
        {
            sb.AppendLine(o.ToString());
            if (o is PdfDictionary d && d.Stream is not null)
            {
                try { sb.AppendLine(Encoding.Latin1.GetString(d.Stream.UnfilteredValue)); } catch (Exception) { }
            }
        }
        return sb.ToString();
    });

    public static string File(byte[] pdf, string name = "zażółć gęślą.pdf") => ZetPDF.TestData.TestPaths.WriteSample(pdf, name);

    /// <summary>PNG z gradientem (do znaków wodnych/teł i optymalizacji).</summary>
    public static byte[] Png(int w, int h, bool noise = false)
    {
        var px = new byte[w * h * 3];
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[o + 1] = (byte)(noise ? rnd.Next(256) : y * 255 / Math.Max(1, h - 1));
                px[o + 2] = 200;
            }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, px, w * 3);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static string PngFile(int w, int h, bool noise = false)
    {
        var dir = ZetPDF.TestData.TestPaths.NewDirectory();
        var path = Path.Combine(dir, "obraz ż.png");
        System.IO.File.WriteAllBytes(path, Png(w, h, noise));
        return path;
    }

    /// <summary>Dokument z dużym obrazem RGB narysowanym małym (wysokie efektywne DPI) i czerwonym tekstem.</summary>
    public static byte[] WithImage(int w = 1600, int h = 1600, bool noise = true)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            using var ms = new MemoryStream(Png(w, h, noise));
            var img = XImage.FromStream(ms);
            g.DrawImage(img, 50, 200, 200, 200);
            g.DrawString("Czerwony tekst", new XFont("Arial", 20, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)), XBrushes.Red, 50, 100);
        }
        return PdfSharpEdit.Save(doc);
    }
}

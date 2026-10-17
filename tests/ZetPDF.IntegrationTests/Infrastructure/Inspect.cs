using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using UglyToad.PdfPig;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.IntegrationTests.Infrastructure;

/// <summary>Niezależna weryfikacja wyniku w pliku: PdfPig, PDFium, PDFsharp (słowniki), surowe bajty po dekompresji.</summary>
internal static class Inspect
{
    private static readonly Lazy<IPdfRenderer> Pdfium = new(() => new PdfiumRenderer());

    public static IPdfRenderer Renderer => Pdfium.Value;

    public static int PageCount(byte[] pdf, string? password = null)
    {
        using var d = PigDocument.Open(pdf, password is null ? null : new ParsingOptions { Password = password });
        return d.NumberOfPages;
    }

    /// <summary>Tekst wszystkich stron wg PdfPig (Text + słowa).</summary>
    public static string PigText(byte[] pdf, string? password = null)
    {
        using var d = PigDocument.Open(pdf, password is null ? null : new ParsingOptions { Password = password });
        var sb = new StringBuilder();
        foreach (var p in d.GetPages())
        {
            sb.AppendLine(p.Text);
            sb.AppendLine(string.Join(" ", p.GetWords().Select(w => w.Text)));
        }
        return sb.ToString();
    }

    public static string PigPageText(byte[] pdf, int pageIndex)
    {
        using var d = PigDocument.Open(pdf);
        return d.GetPage(pageIndex + 1).Text;
    }

    /// <summary>Tekst wszystkich stron wg PDFium (FPDFText).</summary>
    public static async Task<string> PdfiumText(byte[] pdf, string? password = null)
    {
        using var doc = await Renderer.OpenAsync(pdf, password);
        var sb = new StringBuilder();
        for (int i = 0; i < doc.PageCount; i++) sb.AppendLine((await doc.GetPageTextAsync(i)).Text);
        return sb.ToString();
    }

    public static async Task<int> PdfiumPageCount(byte[] pdf, string? password = null)
    {
        using var doc = await Renderer.OpenAsync(pdf, password);
        return doc.PageCount;
    }

    /// <summary>Bez białych znaków (wyodrębnianie bywa różne co do spacji).</summary>
    public static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// Wszystko, co jest w pliku jako tekst: słowniki, zdekodowane strumienie (Latin1), wartości wszystkich ciągów
    /// PdfString (zdekodowane – także UTF-16) i surowe bajty pliku.
    /// </summary>
    public static string Deep(byte[] pdf)
    {
        var sb = new StringBuilder();
        sb.Append(Encoding.Latin1.GetString(pdf));
        sb.AppendLine();
        PdfSharpEdit.Read(pdf, doc =>
        {
            foreach (var o in doc.Internals.GetAllObjects())
            {
                try { sb.AppendLine(o.ToString()); } catch (Exception) { }
                Strings(o, sb, 0);
                if (o is PdfDictionary d && d.Stream is not null)
                {
                    try { sb.AppendLine(Encoding.Latin1.GetString(d.Stream.UnfilteredValue)); }
                    catch (Exception) { sb.AppendLine(Encoding.Latin1.GetString(d.Stream.Value)); }
                    try
                    {
                        var raw = d.Stream.UnfilteredValue;
                        sb.AppendLine(Encoding.BigEndianUnicode.GetString(raw));
                        sb.AppendLine(Encoding.UTF8.GetString(raw));
                    }
                    catch (Exception) { }
                }
            }
            foreach (var kv in doc.Info.Elements) sb.AppendLine(kv.Key + "=" + doc.Info.Elements.GetString(kv.Key));
            return 0;
        });
        return sb.ToString();
    }

    private static void Strings(PdfItem? item, StringBuilder sb, int depth)
    {
        if (item is null || depth > 6) return;
        switch (item)
        {
            case PdfString s: sb.AppendLine(s.Value); break;
            case PdfArray a: foreach (var e in a.Elements) if (e is not PdfReference) Strings(e, sb, depth + 1); break;
            case PdfDictionary d: foreach (var kv in d.Elements) if (kv.Value is not PdfReference) Strings(kv.Value, sb, depth + 1); break;
        }
    }

    /// <summary>Czy fraza występuje gdziekolwiek w pliku (tekst, ciągi, strumienie), w tym w formie bez spacji.</summary>
    public static bool ContainsAnywhere(byte[] pdf, string phrase)
    {
        var deep = Deep(pdf);
        return deep.Contains(phrase, StringComparison.Ordinal) || Squash(deep).Contains(Squash(phrase), StringComparison.Ordinal);
    }

    public static T Read<T>(byte[] pdf, Func<PdfSharp.Pdf.PdfDocument, T> f) => PdfSharpEdit.Read(pdf, f);

    /// <summary>Podtypy adnotacji na stronach (np. "/Highlight").</summary>
    public static List<(int Page, string Subtype)> Annotations(byte[] pdf) => PdfSharpEdit.Read(pdf, doc =>
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var annots = doc.Pages[i].Elements.GetArray("/Annots");
            if (annots is null) continue;
            foreach (var item in annots.Elements)
            {
                var d = (item as PdfReference)?.Value as PdfDictionary ?? item as PdfDictionary;
                if (d is null) continue;
                list.Add((i, d.Elements.GetName("/Subtype")));
            }
        }
        return list;
    });

    /// <summary>Średnia jasność i udział "kolorowych" pikseli w renderze strony (PDFium).</summary>
    public static async Task<(double MeanLuma, double ColorFraction, double DarkFraction)> RenderStats(byte[] pdf, int page, double dpi = 50, string? password = null, RenderFlags flags = RenderFlags.Default)
    {
        using var doc = await Renderer.OpenAsync(pdf, password);
        var bmp = await doc.RenderPageAsync(page, RenderRequest.ForDpi(dpi, flags));
        return Stats(bmp);
    }

    public static (double MeanLuma, double ColorFraction, double DarkFraction) Stats(RenderedBitmap bmp)
    {
        long n = 0, colored = 0, dark = 0;
        double sum = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                int o = y * bmp.Stride + x * 4;
                int b = bmp.Bgra[o], g = bmp.Bgra[o + 1], r = bmp.Bgra[o + 2];
                double l = 0.299 * r + 0.587 * g + 0.114 * b;
                sum += l;
                n++;
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 40) colored++;
                if (l < 100) dark++;
            }
        return (sum / n, (double)colored / n, (double)dark / n);
    }

    public static string Latin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);
}

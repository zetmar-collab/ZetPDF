using System.Text;
using UglyToad.PdfPig;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.Ocr;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

/// <summary>Wspólne narzędzia testów: prawdziwy renderer PDFium, OCR z tymczasowym katalogiem, wyodrębnianie tekstu.</summary>
internal static class TestEnv
{
    private static readonly Lazy<TesseractBootstrap> Boot = new(() => new TesseractBootstrap(new AppPaths(TestPaths.NewDirectory())));

    public static IPdfRenderer Renderer { get; } = new PdfiumRenderer();

    public static TesseractOcrService Ocr() => new(Boot.Value, Renderer);

    /// <summary>Tekst całego dokumentu wg PdfPig (litery w kolejności strumienia).</summary>
    public static string PdfPigText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var sb = new StringBuilder();
        foreach (var p in doc.GetPages())
        {
            sb.AppendLine(p.Text);
            sb.AppendLine(string.Join(" ", p.GetWords().Select(w => w.Text)));
        }
        return sb.ToString();
    }

    /// <summary>Tekst całego dokumentu wg PDFium (FPDFText).</summary>
    public static async Task<string> PdfiumText(byte[] pdf)
    {
        using var doc = await Renderer.OpenAsync(pdf);
        var sb = new StringBuilder();
        for (int i = 0; i < doc.PageCount; i++) sb.AppendLine((await doc.GetPageTextAsync(i)).Text);
        return sb.ToString();
    }

    /// <summary>Czy PDFium znajduje frazę wyszukiwaniem.</summary>
    public static async Task<int> PdfiumSearchCount(byte[] pdf, string phrase)
    {
        using var doc = await Renderer.OpenAsync(pdf);
        int n = 0;
        await foreach (var _ in doc.SearchAsync(phrase, TextSearchOptions.None)) n++;
        return n;
    }

    /// <summary>Normalizacja do porównań (bez spacji – wyodrębnianie bywa różne).</summary>
    public static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>Wszystkie zdekodowane strumienie pliku jako tekst Latin1 (szukanie śladów surowych danych).</summary>
    public static string RawStreams(byte[] pdf)
    {
        using var doc = Core.Documents.PdfSharpEdit.OpenForImport(pdf);
        var sb = new StringBuilder();
        for (int i = 0; i < doc.Internals.GetAllObjects().Length; i++)
        {
            var o = doc.Internals.GetAllObjects()[i];
            if (o is PdfSharp.Pdf.PdfDictionary d)
            {
                sb.Append(d.ToString());
                if (d.Stream is not null)
                {
                    try { sb.Append(Encoding.Latin1.GetString(d.Stream.UnfilteredValue)); }
                    catch (Exception) { sb.Append(Encoding.Latin1.GetString(d.Stream.Value)); }
                }
            }
            else sb.Append(o?.ToString());
        }
        return sb.ToString();
    }
}

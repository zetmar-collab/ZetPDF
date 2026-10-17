using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Documents;
using ZetPDF.Ocr.Pdf;

namespace ZetPDF.Editing.Tests;

/// <summary>Próbki specjalne modułu (TestData edytuje tylko tester).</summary>
internal static class Samples
{
    /// <summary>
    /// Strona A4 przerobiona na poziomą MediaBox z /Rotate 90 i treścią obróconą o 90° w lewo –
    /// na ekranie wygląda jak oryginał (pionowo), ale współrzędne użytkownika są obrócone.
    /// </summary>
    public static byte[] RotatedUpright(byte[] portraitA4) => PdfSharpEdit.Modify(portraitA4, doc =>
    {
        foreach (var page in doc.Pages)
        {
            var old = PageContent.ReadAll(page);
            var content = Encoding.ASCII.GetBytes("q 0 1 -1 0 842 0 cm\n").Concat(old).Concat(Encoding.ASCII.GetBytes("\nQ\n")).ToArray();
            PageContent.Replace(page, content);
            page.Elements["/MediaBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(842), new PdfInteger(595));
            page.Elements.Remove("/CropBox");
            page.Elements.SetInteger("/Rotate", 90);
        }
    });

    private static XFont Font(double size, bool bold = false) =>
        new("Arial", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    /// <summary>Dokument z nagłówkami (do zakładek z nagłówków), łączami w tekście i obrazem.</summary>
    public static byte[] Structured()
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        for (int p = 0; p < 2; p++)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            double y = 70;
            g.DrawString($"Rozdział {p + 1}. Wstęp", Font(20, true), XBrushes.Black, 56, y); y += 36;
            g.DrawString("Zwykły tekst akapitu z polskimi znakami: zażółć gęślą jaźń.", Font(11), XBrushes.Black, 56, y); y += 18;
            g.DrawString("Więcej na www.example.com oraz https://zetpdf.example.org/strona i kontakt@example.com.", Font(11), XBrushes.Black, 56, y); y += 30;
            g.DrawString($"{p + 1}.1 Podrozdział pierwszy", Font(15, true), XBrushes.Black, 56, y); y += 26;
            g.DrawString("Treść podrozdziału, kolejna linia tekstu podstawowego.", Font(11), XBrushes.Black, 56, y); y += 18;
            g.DrawString("Jeszcze jedna linia tekstu, aby rozmiar podstawowy dominował.", Font(11), XBrushes.Black, 56, y); y += 30;
            g.DrawString($"{p + 1}.2 Podrozdział drugi", Font(15, true), XBrushes.Black, 56, y); y += 26;
            g.DrawString("Ostatnia linia tekstu na stronie, bez nagłówka.", Font(11), XBrushes.Black, 56, y); y += 18;
            g.DrawRectangle(XPens.Black, XBrushes.LightBlue, 300, 500, 120, 80);
        }
        return PdfSharpEdit.Save(doc);
    }
}

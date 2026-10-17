using System.Reflection;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Wygląd z tekstem/obrazem rysowany przez XGraphics PDFsharp na XForm (czcionka TrueType osadzana jako Unicode/Identity-H –
/// polskie znaki działają w każdym czytniku). Uchwyt PdfFormXObject pobieramy przez właściwość wewnętrzną
/// <c>XForm.PdfForm</c> (PDFsharp 6.2.4 przypięty – sprawdzone w scratch\probe; patrz docs\ograniczenia\annotations-forms.md).
/// Współrzędne XGraphics: początek w lewym GÓRNYM rogu formy, Y w dół.
/// </summary>
internal static class XFormHelper
{
    private static readonly PropertyInfo? PdfFormProperty =
        typeof(XForm).GetProperty("PdfForm", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>Tworzy formę w×h, rysuje i zwraca słownik formy (już pośredni w dokumencie).</summary>
    public static PdfDictionary Draw(PdfDocument doc, double width, double height, Action<XGraphics> draw)
    {
        PdfSharpEdit.EnsureInitialized();
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var form = new XForm(doc, new XRect(0, 0, width, height));
        using (var g = XGraphics.FromForm(form)) draw(g);
        if (PdfFormProperty?.GetValue(form) is not PdfFormXObject pdfForm)
            throw new InvalidOperationException("PDFsharp XForm.PdfForm is not available (unsupported PDFsharp version).");
        if (pdfForm.Reference is null) doc.Internals.AddObject(pdfForm);
        return pdfForm;
    }

    public static XFont Font(string family, double size, bool bold = false, bool italic = false)
    {
        PdfSharpEdit.EnsureInitialized();
        var style = (bold, italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };
        size = Math.Clamp(size, 1, 400);
        var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
        try { return new XFont(string.IsNullOrWhiteSpace(family) ? "Arial" : family, size, style, options); }
        catch (Exception) { return new XFont("Arial", size, style, options); }
    }

    public static XColor X(PdfColor c, double opacity = 1) =>
        XColor.FromArgb((int)Math.Clamp(Math.Round(c.A * opacity), 0, 255), c.R, c.G, c.B);

    private static XGraphics? _measure;
    private static readonly object MeasureGate = new();

    /// <summary>Rozmiar tekstu (punkty) – do automatycznego doboru prostokąta.</summary>
    public static XSize Measure(string text, XFont font)
    {
        lock (MeasureGate)
        {
            _measure ??= XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
            return _measure.MeasureString(string.IsNullOrEmpty(text) ? " " : text, font);
        }
    }

    /// <summary>Łamie tekst na wiersze mieszczące się w szerokości (słowa; twarde \n).</summary>
    public static List<string> Wrap(string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var words = para.Split(' ');
            var current = string.Empty;
            foreach (var w in words)
            {
                var candidate = current.Length == 0 ? w : current + " " + w;
                if (current.Length > 0 && Measure(candidate, font).Width > maxWidth)
                {
                    lines.Add(current);
                    current = w;
                }
                else current = candidate;
                // bardzo długie słowo – tniemy po znakach
                while (current.Length > 1 && Measure(current, font).Width > maxWidth)
                {
                    int cut = current.Length - 1;
                    while (cut > 1 && Measure(current[..cut], font).Width > maxWidth) cut--;
                    lines.Add(current[..cut]);
                    current = current[cut..];
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>Rysuje wiersze tekstu od góry prostokąta (XGraphics, Y w dół).</summary>
    public static void DrawLines(XGraphics g, IReadOnlyList<string> lines, XFont font, XBrush brush, XRect box, XStringAlignment align = XStringAlignment.Near)
    {
        double lineHeight = font.GetHeight();
        double y = box.Top;
        var fmt = new XStringFormat { Alignment = align, LineAlignment = XLineAlignment.Near };
        foreach (var line in lines)
        {
            if (y + lineHeight > box.Bottom + lineHeight * 0.4) break;
            g.DrawString(line, font, brush, new XRect(box.Left, y, box.Width, lineHeight), fmt);
            y += lineHeight;
        }
    }
}

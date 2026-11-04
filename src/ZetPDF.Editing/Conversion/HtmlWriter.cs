using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Content;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Utilities;
using ZetPDF.Editing.Text;

namespace ZetPDF.Editing.Conversion;

/// <summary>
/// PDF → jeden plik HTML: akapity z rozmiarem i pogrubieniem, obrazy jako data: URI. Wszystko, co pochodzi z dokumentu,
/// jest escapowane; kolor i rodzina czcionki to wartości wyliczone, a nie tekst z pliku. Plik nie ma skryptów ani zasobów
/// zewnętrznych, a nagłówek Content-Security-Policy blokuje je także wtedy, gdyby coś się przedostało.
/// </summary>
internal static class HtmlWriter
{
    private const string Csp = "default-src 'none'; img-src data:; style-src 'unsafe-inline'";

    public static string Write(byte[] pdf, HtmlExportOptions options, string fallbackTitle, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var pages = string.IsNullOrWhiteSpace(options.PageRange) ? Enumerable.Range(0, pig.NumberOfPages).ToList() : PageRange.Parse(options.PageRange, pig.NumberOfPages).ToList();
        var title = pig.Information.Title;
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(Csp).Append("\">\n");
        sb.Append("<meta name=\"referrer\" content=\"no-referrer\">\n");
        sb.Append("<title>").Append(Escape(string.IsNullOrWhiteSpace(title) ? fallbackTitle : title)).Append("</title>\n");
        sb.Append("<style>body{margin:2em auto;max-width:48em;font-family:Arial,sans-serif}section{margin-bottom:2em}p{margin:0 0 .6em}img{max-width:100%;height:auto;display:block;margin:0 0 .6em}</style>\n");
        sb.Append("</head>\n<body>\n");
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(i, pages.Count));
            sb.Append("<section>\n");
            AppendPage(sb, pig.GetPage(pages[i] + 1), pages[i], options);
            sb.Append("</section>\n");
        }
        sb.Append("</body>\n</html>\n");
        progress?.Report(OperationProgress.Of(pages.Count, pages.Count));
        return sb.ToString();
    }

    private static void AppendPage(StringBuilder sb, Page page, int pageIndex, HtmlExportOptions options)
    {
        var lines = TextLines.Build(page, pageIndex).OrderByDescending(l => l.Baseline.Y).ThenBy(l => l.Bounds.Left).ToList();
        var items = new List<(double Top, string Html)>();
        foreach (var para in DocxWriter.Paragraphs(lines)) items.Add((para[0].Bounds.Top, Paragraph(para, options.TextOnly)));
        if (!options.TextOnly)
        {
            foreach (var image in DocxWriter.SafeImages(page))
                if (Image(image) is { } html) items.Add((image.BoundingBox.Top, html));
        }
        foreach (var (_, html) in items.OrderByDescending(x => x.Top)) sb.Append(html).Append('\n');
    }

    private static string Paragraph(List<TextLine> lines, bool textOnly)
    {
        var text = Escape(string.Join(" ", lines.Select(l => l.Text.Trim())));
        if (textOnly) return "<p>" + text + "</p>";
        var first = lines[0];
        var c = first.Color;
        var style = new StringBuilder();
        style.Append(CultureInfo.InvariantCulture, $"font-size:{Math.Round(first.FontSize, 1)}pt");
        // ToFamily zwraca wartość ze stałej listy, a nie nazwę z pliku.
        style.Append(";font-family:'").Append(FontNames.ToFamily(first.FontName)).Append("',sans-serif");
        if (c.R != 0 || c.G != 0 || c.B != 0) style.Append(CultureInfo.InvariantCulture, $";color:#{c.R:X2}{c.G:X2}{c.B:X2}");
        var inner = first.Italic ? "<em>" + text + "</em>" : text;
        if (first.Bold) inner = "<strong>" + inner + "</strong>";
        return "<p style=\"" + style + "\">" + inner + "</p>";
    }

    private static string? Image(IPdfImage image)
    {
        var data = DocxWriter.ImageBytes(image, out _);
        if (data is null || MimeOf(data) is not { } mime) return null;
        double w = Math.Max(4, image.BoundingBox.Width), h = Math.Max(4, image.BoundingBox.Height);
        return string.Create(CultureInfo.InvariantCulture, $"<img alt=\"\" width=\"{Math.Round(w * 96 / 72)}\" height=\"{Math.Round(h * 96 / 72)}\" src=\"data:{mime};base64,{Convert.ToBase64String(data)}\">");
    }

    /// <summary>Typ wyłącznie z sygnatury bajtów; inne dane nie trafiają do pliku.</summary>
    internal static string? MimeOf(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        return null;
    }

    /// <summary>Escapowanie tekstu z dokumentu; znaki sterujące i samotne surogaty są usuwane.</summary>
    internal static string Escape(string? text) =>
        XlsxWriter.CleanText(text ?? string.Empty).Replace('	', ' ')
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
}

using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Ocr.Pdf;

/// <summary>Słowo/napis do zapisania w warstwie tekstu. Origin = lewy koniec linii bazowej w przestrzeni użytkownika strony.</summary>
/// <param name="Direction">Kierunek pisania (wektor jednostkowy w przestrzeni strony); null = (1, 0).</param>
/// <param name="TargetWidth">Docelowa szerokość (punkty) – napis jest skalowany poziomo (Tz), żeby ją osiągnąć; null = naturalna.</param>
public sealed record LayerWord(string Text, PdfPoint Origin, double FontSize, PdfPoint? Direction = null, double? TargetWidth = null);

/// <summary>Styl warstwy: czcionka systemowa (osadzana jako TrueType/Identity-H z ToUnicode – polskie znaki), kolor, tryb.</summary>
public sealed record LayerStyle
{
    public string FontFamily { get; init; } = "Arial";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public PdfColor Color { get; init; } = PdfColor.Black;
    /// <summary>Tryb renderowania 3 (niewidoczny) – warstwa OCR / tekst pod obrazem redakcji.</summary>
    public bool Hidden { get; init; }
    /// <summary>Znacznik strumienia (np. "ocr") – pozwala później zastąpić warstwę (<see cref="PageContent.RemoveMarked"/>).</summary>
    public string? Marker { get; init; }

    public static LayerStyle HiddenText(string? marker = null) => new() { Hidden = true, Marker = marker };
}

/// <summary>
/// Zapis tekstu na stronie z pełną kontrolą pozycji i trybu renderowania.
/// Sposób: PDFsharp (XGraphics) rysuje napisy, dzięki czemu tworzy czcionkę Type0/Identity-H z podzbiorem glifów
/// i mapą ToUnicode (tekst daje się wyszukać i skopiować); następnie wygenerowany strumień jest parsowany,
/// a ciągi glifów przepisywane do własnego strumienia z macierzą Tm (pozycja, rozmiar, kierunek, skala pozioma)
/// i trybem 0 (widoczny) lub 3 (niewidoczny). Pozycje nie zależą od tego, jak PDFsharp liczy współrzędne strony.
/// </summary>
public static class TextLayerWriter
{
    private const double MeasureSize = 10;

    /// <summary>Szerokość napisu dla czcionki o rozmiarze 1 (punkty).</summary>
    public static double MeasureUnitWidth(string text, LayerStyle style)
    {
        PdfSharpEdit.EnsureInitialized();
        var font = CreateFont(style, MeasureSize);
        using var g = XGraphics.CreateMeasureContext(new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards);
        return g.MeasureString(text, font).Width / MeasureSize;
    }

    /// <summary>Dopisuje słowa do strony. Zwraca liczbę zapisanych słów.</summary>
    public static int Write(PdfPage page, IReadOnlyList<LayerWord> words, LayerStyle style)
    {
        PdfSharpEdit.EnsureInitialized();
        var valid = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.FontSize > 0.1).ToList();
        if (valid.Count == 0) return 0;

        var font = CreateFont(style, MeasureSize);
        var before = PageContent.Streams(page).Select(s => (PdfItem?)s.Reference ?? s).ToList();
        var widths = new double[valid.Count];
        using (var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
        {
            for (int i = 0; i < valid.Count; i++)
            {
                var text = Clean(valid[i].Text);
                widths[i] = g.MeasureString(text, font).Width / MeasureSize;
                g.DrawString(text, font, XBrushes.Black, 10, 100);
            }
        }

        // Strumień dodany przez XGraphics – zbieramy nazwy czcionek i ciągi glifów, potem przywracamy poprzednią treść.
        var after = PageContent.Streams(page);
        var added = after.Where(s => !before.Contains((PdfItem?)s.Reference ?? s)).ToList();
        // Każde DrawString zaczyna się operatorem pozycji (Td/Tm); w grupie może być kilka ciągów (np. różne czcionki).
        var glyphs = new List<List<(string Font, string Codes)>>();
        foreach (var s in added)
        {
            var seq = ContentReader.ReadContent(PageContent.Decoded(s));
            string fontName = "/F0";
            List<(string, string)>? group = null;
            foreach (var op in seq.OfType<COperator>())
            {
                switch (op.OpCode.Name)
                {
                    case "Tf" when op.Operands.Count >= 1 && op.Operands[0] is CName n:
                        fontName = n.Name;
                        break;
                    case "Td" or "TD" or "Tm" or "BT":
                        group = null;
                        break;
                    case "Tj" when op.Operands.Count >= 1 && op.Operands[0] is CString cs:
                        if (group is null) glyphs.Add(group = new List<(string, string)>());
                        group.Add((fontName, cs.Value));
                        break;
                    case "TJ" when op.Operands.Count >= 1 && op.Operands[0] is CArray ca:
                        if (group is null) glyphs.Add(group = new List<(string, string)>());
                        group.Add((fontName, string.Concat(ca.OfType<CString>().Select(x => x.Value))));
                        break;
                }
            }
        }
        var restore = new PdfArray(page.Owner);
        foreach (var item in before) restore.Elements.Add(item!);
        page.Elements["/Contents"] = restore;
        if (glyphs.Count != valid.Count)
            throw new InvalidOperationException($"Text layer: expected {valid.Count} glyph runs, got {glyphs.Count}.");

        var sb = new StringBuilder();
        sb.Append("q\nBT\n");
        sb.Append(style.Hidden ? "3 Tr\n" : "0 Tr\n");
        if (!style.Hidden)
            sb.Append($"{PageContent.Num(style.Color.R / 255.0)} {PageContent.Num(style.Color.G / 255.0)} {PageContent.Num(style.Color.B / 255.0)} rg\n");
        sb.Append("0 Tc 0 Tw 100 Tz 0 Ts\n");
        string? currentFont = null;
        for (int i = 0; i < valid.Count; i++)
        {
            var w = valid[i];
            var dir = w.Direction ?? new PdfPoint(1, 0);
            var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (len < 1e-9) dir = new PdfPoint(1, 0);
            else dir = new PdfPoint(dir.X / len, dir.Y / len);
            var up = new PdfPoint(-dir.Y, dir.X);
            double fs = w.FontSize;
            double sx = 1;
            if (w.TargetWidth is { } tw && tw > 0 && widths[i] > 1e-6)
                sx = Math.Clamp(tw / (widths[i] * fs), 0.05, 20);
            sb.Append(PageContent.Matrix(dir.X * fs * sx, dir.Y * fs * sx, up.X * fs, up.Y * fs, w.Origin.X, w.Origin.Y)).Append(" Tm\n");
            foreach (var (fontName, codes) in glyphs[i])
            {
                if (fontName != currentFont)
                {
                    sb.Append(fontName).Append(" 1 Tf\n");
                    currentFont = fontName;
                }
                sb.Append(PageContent.Hex(codes)).Append(" Tj\n");
            }
        }
        sb.Append("ET\nQ\n");
        PageContent.Append(page, Encoding.ASCII.GetBytes(sb.ToString()), style.Marker);
        return valid.Count;
    }

    private static XFont CreateFont(LayerStyle style, double size)
    {
        var st = (style.Bold, style.Italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };
        try
        {
            return new XFont(string.IsNullOrWhiteSpace(style.FontFamily) ? "Arial" : style.FontFamily, size, st, new XPdfFontOptions(PdfFontEncoding.Unicode));
        }
        catch (Exception)
        {
            return new XFont("Arial", size, st, new XPdfFontOptions(PdfFontEncoding.Unicode));
        }
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }
}

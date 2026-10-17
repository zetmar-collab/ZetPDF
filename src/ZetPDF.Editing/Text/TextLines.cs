using UglyToad.PdfPig.Content;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Text;

/// <summary>Linia tekstu z PdfPig (słowa o wspólnej linii bazowej i zbliżonym rozmiarze).</summary>
public sealed class TextLine
{
    public required int PageIndex { get; init; }
    public required int Index { get; init; }
    public required List<Word> Words { get; init; }
    public string Text => string.Join(" ", Words.Select(w => w.Text));
    public PdfRect Bounds => Words.Select(w => R(w.BoundingBox)).Aggregate((a, b) => a.Union(b));
    public PdfPoint Baseline => new(Words[0].Letters[0].StartBaseLine.X, Words[0].Letters[0].StartBaseLine.Y);
    public Letter First => Words[0].Letters[0];
    public double FontSize => Words.SelectMany(w => w.Letters).GroupBy(l => Math.Round(l.PointSize, 1)).OrderByDescending(g => g.Count()).First().Key;
    public string FontName => FontNames.StripSubset(First.FontName ?? string.Empty);
    public bool Bold => Words.SelectMany(w => w.Letters).Count(l => FontNames.IsBold(l.FontName)) * 2 > Words.Sum(w => w.Letters.Count);
    public bool Italic => FontNames.IsItalic(First.FontName);

    public PdfColor Color
    {
        get
        {
            try
            {
                var (r, g, b) = First.Color.ToRGBValues();
                return new PdfColor(255, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
            }
            catch (Exception) { return PdfColor.Black; }
        }
    }

    public static PdfRect R(UglyToad.PdfPig.Core.PdfRectangle r) =>
        new(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
}

/// <summary>Budowa linii z PdfPig (poziomy tekst; słowa z tekstu widocznego – ukryta warstwa OCR też jest uwzględniana).</summary>
public static class TextLines
{
    public static List<TextLine> Build(Page page, int pageIndex)
    {
        var words = page.GetWords().Where(w => w.Letters.Count > 0 && !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => Math.Round(w.Letters[0].StartBaseLine.Y, 1)).ThenBy(w => w.BoundingBox.Left))
        {
            var bl = w.Letters[0].StartBaseLine.Y;
            var size = Math.Max(1, w.Letters[0].PointSize);
            var line = lines.LastOrDefault(l =>
            {
                var last = l[^1];
                return Math.Abs(last.Letters[0].StartBaseLine.Y - bl) < size * 0.3
                    && Math.Abs(last.Letters[0].PointSize - size) < size * 0.35
                    && w.BoundingBox.Left - last.BoundingBox.Right < size * 2.5
                    && w.BoundingBox.Left >= last.BoundingBox.Left;
            });
            if (line is null) lines.Add(new List<Word> { w });
            else line.Add(w);
        }
        return lines.Select((l, i) => new TextLine { PageIndex = pageIndex, Index = i, Words = l.OrderBy(w => w.BoundingBox.Left).ToList() }).ToList();
    }
}

/// <summary>Nazwy czcionek PDF → rodziny systemowe.</summary>
public static class FontNames
{
    public static string StripSubset(string name)
    {
        int plus = name.IndexOf('+');
        return plus == 6 ? name[(plus + 1)..] : name;
    }

    public static bool IsBold(string? name) => name is not null && (name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase) || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || name.Contains("Semibold", StringComparison.OrdinalIgnoreCase));

    public static bool IsItalic(string? name) => name is not null && (name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase));

    private static readonly (string Prefix, string Family)[] Known =
    {
        ("ArialNarrow", "Arial Narrow"), ("Arial", "Arial"), ("Helvetica", "Arial"), ("TimesNewRoman", "Times New Roman"), ("Times", "Times New Roman"),
        ("CourierNew", "Courier New"), ("Courier", "Courier New"), ("Calibri", "Calibri"), ("Cambria", "Cambria"), ("Verdana", "Verdana"),
        ("Tahoma", "Tahoma"), ("Georgia", "Georgia"), ("SegoeUI", "Segoe UI"), ("Segoe UI", "Segoe UI"), ("TrebuchetMS", "Trebuchet MS"),
        ("Consolas", "Consolas"), ("Garamond", "Garamond"), ("BookAntiqua", "Book Antiqua"), ("CenturyGothic", "Century Gothic"),
    };

    /// <summary>Rodzina systemowa dla nazwy czcionki PDF (np. "ABCDEF+Arial-BoldMT" → "Arial"); domyślnie Arial.</summary>
    public static string ToFamily(string? pdfName)
    {
        var n = StripSubset(pdfName ?? string.Empty).Replace(" ", string.Empty);
        foreach (var (prefix, family) in Known)
            if (n.StartsWith(prefix.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase)) return family;
        return "Arial";
    }
}

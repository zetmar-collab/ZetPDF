using System.Text;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.Text;

/// <summary>Pozycja kursora tekstowego: strona + indeks „między znakami” (0..Text.Length).</summary>
public readonly record struct TextCaret(int PageIndex, int Position) : IComparable<TextCaret>
{
    public int CompareTo(TextCaret other) => PageIndex != other.PageIndex ? PageIndex.CompareTo(other.PageIndex) : Position.CompareTo(other.Position);
}

/// <summary>
/// Geometria tekstu strony dla zaznaczania i wyszukiwania (czysta logika – testy jednostkowe).
/// Działa na <see cref="PageText"/> z PDFium (prostokąty znaków w punktach strony, Y w górę).
/// </summary>
public static class TextGeometry
{
    /// <summary>
    /// Pozycja kursora najbliższa punktowi: znak zawierający punkt (lewa/prawa połowa decyduje o stronie kursora),
    /// w przeciwnym razie najbliższy znak w tym samym wierszu, a w ostateczności najbliższy znak na stronie.
    /// Zwraca -1, gdy strona nie ma znaków z prostokątami.
    /// </summary>
    public static int HitCaret(PageText text, PdfPoint p)
    {
        int best = -1;
        double bestScore = double.MaxValue;
        var chars = text.Chars;
        for (int i = 0; i < chars.Count; i++)
        {
            var c = chars[i];
            if (c.IsGenerated || c.Box.IsEmpty) continue;
            var b = c.Box.Inflate(0.5);
            double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
            double dy = p.Y < b.Bottom ? b.Bottom - p.Y : p.Y > b.Top ? p.Y - b.Top : 0;
            // odległość w pionie ważniejsza (wybór wiersza), potem w poziomie
            double score = dy * 10 + dx;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                if (score == 0) break;
            }
        }
        if (best < 0) return -1;
        var box = chars[best].Box;
        return p.X > (box.Left + box.Right) / 2 ? best + 1 : best;
    }

    /// <summary>Indeks znaku (nie kursora) pod punktem lub -1 (do dwukliku = zaznacz słowo).</summary>
    public static int HitChar(PageText text, PdfPoint p)
    {
        for (int i = 0; i < text.Chars.Count; i++)
        {
            var c = text.Chars[i];
            if (!c.IsGenerated && !c.Box.IsEmpty && c.Box.Inflate(0.5).Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>Zakres słowa (litery/cyfry) wokół znaku.</summary>
    public static (int Start, int End) WordAt(string text, int index)
    {
        if ((uint)index >= (uint)text.Length) return (index, index);
        if (!char.IsLetterOrDigit(text[index])) return (index, index + 1);
        int s = index, e = index;
        while (s > 0 && char.IsLetterOrDigit(text[s - 1])) s--;
        while (e < text.Length && char.IsLetterOrDigit(text[e])) e++;
        return (s, e);
    }

    /// <summary>
    /// Czworokąty wierszy dla zakresu znaków [start, start+count): kolejne znaki o nachodzących się pionowo
    /// prostokątach łączymy w jeden prostokąt wiersza.
    /// </summary>
    public static IReadOnlyList<PdfQuad> LineQuads(PageText text, int start, int count)
    {
        var result = new List<PdfQuad>();
        PdfRect? line = null;
        int end = Math.Min(text.Chars.Count, start + count);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            var c = text.Chars[i];
            if (c.IsGenerated || c.Box.IsEmpty) continue;
            var b = c.Box;
            if (line is { } l)
            {
                double overlap = Math.Min(l.Top, b.Top) - Math.Max(l.Bottom, b.Bottom);
                double minH = Math.Min(l.Height, b.Height);
                bool sameLine = minH > 0 && overlap >= minH * 0.5 && b.Left >= l.Left - b.Width * 2;
                if (sameLine) { line = l.Union(b); continue; }
                result.Add(PdfQuad.FromRect(l));
            }
            line = b;
        }
        if (line is { } last) result.Add(PdfQuad.FromRect(last));
        return result;
    }

    /// <summary>Buduje zaznaczenie między dwoma kursorami (kolejność dowolna). Zwraca null dla pustego zakresu.</summary>
    public static PdfTextSelection? BuildSelection(TextCaret a, TextCaret b, Func<int, PageText?> getText)
    {
        var (from, to) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        var spans = new List<TextSelectionSpan>();
        var sb = new StringBuilder();
        for (int page = from.PageIndex; page <= to.PageIndex; page++)
        {
            var text = getText(page);
            if (text is null || text.Text.Length == 0) continue;
            int s = page == from.PageIndex ? Math.Clamp(from.Position, 0, text.Text.Length) : 0;
            int e = page == to.PageIndex ? Math.Clamp(to.Position, 0, text.Text.Length) : text.Text.Length;
            if (e <= s) continue;
            if (sb.Length > 0 && !EndsWithNewLine(sb)) sb.Append("\r\n");
            sb.Append(text.Text, s, e - s);
            spans.Add(new TextSelectionSpan(page, s, e - s, LineQuads(text, s, e - s)));
        }
        if (spans.Count == 0) return null;
        return new PdfTextSelection(spans, sb.ToString());
    }

    private static bool EndsWithNewLine(StringBuilder sb) => sb.Length > 0 && sb[^1] == '\n';
}

using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Text;

/// <summary>Pomocniki dla tekstu PDFium (<see cref="PageText"/>): prostokąty zakresów, słowa.</summary>
public static class PageTextTools
{
    /// <summary>Prostokąty (po jednym na linię) dla znaków [start, start+count).</summary>
    public static List<PdfRect> RangeRects(PageText text, int start, int count)
    {
        var rects = new List<PdfRect>();
        PdfRect? cur = null;
        for (int i = start; i < Math.Min(text.Chars.Count, start + count); i++)
        {
            var c = text.Chars[i];
            if (c.IsGenerated || c.Box.Width <= 0 && c.Box.Height <= 0)
            {
                continue;
            }
            if (cur is { } r && !SameLine(r, c.Box))
            {
                rects.Add(r);
                cur = null;
            }
            cur = cur is { } rr ? rr.Union(c.Box) : c.Box;
        }
        if (cur is { } last) rects.Add(last);
        return rects;
    }

    public static bool SameLine(PdfRect a, PdfRect b)
    {
        double ca = (a.Bottom + a.Top) / 2, cb = (b.Bottom + b.Top) / 2;
        double h = Math.Max(Math.Max(a.Height, b.Height), 1);
        return Math.Abs(ca - cb) < h * 0.6 && b.Left >= a.Left - h * 2;
    }

    /// <summary>Słowo z PDFium: tekst, prostokąt, rozmiar czcionki, indeksy znaków.</summary>
    public sealed record Word(string Text, PdfRect Box, double FontSize, int Start, int Count);

    /// <summary>Dzieli tekst strony na słowa (białe znaki/znaki generowane rozdzielają). Opcjonalnie pomija znaki odrzucone przez filtr.</summary>
    public static List<Word> Words(PageText text, Func<TextChar, bool>? keep = null)
    {
        var words = new List<Word>();
        int start = -1;
        var sb = new System.Text.StringBuilder();
        PdfRect box = default;
        double fs = 0;
        void Flush(int end)
        {
            if (start >= 0 && sb.Length > 0) words.Add(new Word(sb.ToString(), box, fs, start, end - start));
            start = -1;
            sb.Clear();
        }
        for (int i = 0; i < text.Chars.Count; i++)
        {
            var c = text.Chars[i];
            bool usable = !c.IsGenerated && !char.IsWhiteSpace(c.Char) && c.Box.Width >= 0 && (keep?.Invoke(c) ?? true);
            if (!usable || (start >= 0 && !SameLine(box, c.Box)))
            {
                Flush(i);
                if (!usable) continue;
            }
            if (start < 0) { start = i; box = c.Box; fs = c.FontSize; }
            else box = box.Union(c.Box);
            sb.Append(c.Char);
        }
        Flush(text.Chars.Count);
        return words;
    }

    /// <summary>Indeks znaku najbliższego punktowi (w obrębie strony) lub -1.</summary>
    public static int NearestChar(PageText text, PdfPoint p, double maxDistance = 20)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < text.Chars.Count; i++)
        {
            var c = text.Chars[i];
            if (c.IsGenerated) continue;
            var b = c.Box;
            double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
            double dy = p.Y < b.Bottom ? b.Bottom - p.Y : p.Y > b.Top ? p.Y - b.Top : 0;
            double d = dx * dx + dy * dy * 4;
            if (d < bestD) { bestD = d; best = i; }
        }
        return bestD <= maxDistance * maxDistance ? best : -1;
    }
}

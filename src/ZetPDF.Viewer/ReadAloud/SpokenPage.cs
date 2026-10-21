using System.Globalization;
using System.Text;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.ReadAloud;

/// <summary>
/// Tekst strony do przeczytania: linie z renderera (PDFium, kolejność strumienia treści = kolejność odczytu
/// w poprawnie zbudowanych dokumentach), białe znaki zwinięte, przeniesienia z dywizem na końcu linii łączone.
/// <see cref="SourceIndex"/>[i] = indeks znaku w <see cref="PageText.Chars"/> dla znaku i tekstu mówionego (−1 = wstawiony).
/// </summary>
public sealed class SpokenPage
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;
    private const char SoftHyphen = (char)0x00AD;
    private const char NonChar1 = (char)0xFFFE;
    private const char NonChar2 = (char)0xFFFF;
    private readonly PageText _source;

    private SpokenPage(PageText source, string text, int[] map)
    {
        _source = source;
        Text = text;
        SourceIndex = map;
    }

    public int PageIndex => _source.PageIndex;
    public string Text { get; }
    public IReadOnlyList<int> SourceIndex { get; }
    public bool IsEmpty => Text.All(c => !char.IsLetterOrDigit(c));

    public static SpokenPage Build(PageText page)
    {
        var sb = new StringBuilder(page.Text.Length);
        var map = new List<int>(page.Text.Length);
        string src = page.Text;
        bool pendingSpace = false;
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            bool lineBreak = c is '\r' or '\n' or LineSeparator or ParagraphSeparator;
            if (lineBreak || char.IsWhiteSpace(c) || char.IsControl(c) || c == NonChar1 || c == NonChar2)
            {
                // dywiz na końcu linii + mała litera w następnej: „przeno-\r\nszone” → „przenoszone”
                if (lineBreak && sb.Length > 1 && sb[^1] is '-' or SoftHyphen && char.IsLetter(sb[^2]) && NextLetterIsLower(src, i))
                {
                    sb.Length--;
                    map.RemoveAt(map.Count - 1);
                    while (i + 1 < src.Length && src[i + 1] is '\r' or '\n') i++;
                    pendingSpace = false;
                    continue;
                }
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }
            if (c == SoftHyphen) continue; // miękki dywiz w środku linii
            if (pendingSpace)
            {
                sb.Append(' ');
                map.Add(-1);
                pendingSpace = false;
            }
            sb.Append(c);
            map.Add(i < page.Chars.Count && !page.Chars[i].IsGenerated ? i : -1);
        }
        return new SpokenPage(page, sb.ToString(), map.ToArray());
    }

    private static bool NextLetterIsLower(string s, int i)
    {
        for (int k = i + 1; k < s.Length; k++)
        {
            char c = s[k];
            if (c is '\r' or '\n' || char.IsWhiteSpace(c)) continue;
            return char.IsLetter(c) && char.GetUnicodeCategory(c) == UnicodeCategory.LowercaseLetter;
        }
        return false;
    }

    /// <summary>Prostokąty (w punktach strony) fragmentu tekstu mówionego – jeden na wiersz.</summary>
    public IReadOnlyList<PdfRect> RectsFor(int position, int length)
    {
        var result = new List<PdfRect>();
        int end = Math.Min(Text.Length, position + Math.Max(1, length));
        PdfRect? current = null;
        for (int i = Math.Max(0, position); i < end; i++)
        {
            int s = SourceIndex[i];
            if (s < 0 || s >= _source.Chars.Count) continue;
            var box = _source.Chars[s].Box;
            if (box.IsEmpty) continue;
            if (current is { } r && Math.Abs(r.Bottom - box.Bottom) < Math.Max(2, box.Height * 0.5))
                current = r.Union(box);
            else
            {
                if (current is { } done) result.Add(done);
                current = box;
            }
        }
        if (current is { } last) result.Add(last);
        return result;
    }
}

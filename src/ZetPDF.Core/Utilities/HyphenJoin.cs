namespace ZetPDF.Core.Utilities;

/// <summary>
/// Wyrazy przeniesione z dywizem do następnego wiersza („przeno-\r\nszone”) łączone na potrzeby wyszukiwania. Dywiz jest
/// usuwany, gdy stoi po literze, za nim jest koniec wiersza (z ewentualnymi spacjami), a potem litera. Miękki dywiz (U+00AD)
/// i znak 0x02, którym PDFium oznacza dywiz na końcu wiersza, łączą także bez końca wiersza. Zwykły dywiz w środku wiersza
/// („biało-czerwony”) zostaje.
/// </summary>
public static class HyphenJoin
{
    private const char SoftHyphen = (char)0x00AD, Hyphen = (char)0x2010, NonBreakingHyphen = (char)0x2011, PdfiumLineEndHyphen = (char)0x0002, LineSeparator = (char)0x2028;

    /// <summary>Tekst po połączeniu; map[i] = indeks znaku w oryginale (do przeliczania pozycji trafień).</summary>
    public static string Join(string text, out int[] map)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var m = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsHyphen(c) && sb.Length > 0 && char.IsLetter(sb[^1]) && TryLineBreakAfter(text, i, c, out int next))
            {
                i = next - 1; // następna iteracja zaczyna od litery w nowym wierszu
                continue;
            }
            sb.Append(c);
            m.Add(i);
        }
        map = m.ToArray();
        return sb.ToString();
    }

    private static bool IsHyphen(char c) => c is '-' or SoftHyphen or Hyphen or NonBreakingHyphen or PdfiumLineEndHyphen;

    private static bool TryLineBreakAfter(string text, int hyphen, char kind, out int next)
    {
        bool lineBreak = kind is SoftHyphen or PdfiumLineEndHyphen;
        int j = hyphen + 1;
        while (j < text.Length && (char.IsWhiteSpace(text[j]) || text[j] == '\0'))
        {
            if (text[j] is '\r' or '\n' or LineSeparator) lineBreak = true;
            j++;
        }
        next = j;
        return lineBreak && j < text.Length && char.IsLetter(text[j]);
    }
}

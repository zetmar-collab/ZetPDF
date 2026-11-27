using System.Text;

namespace ZetPDF.Rendering;

/// <summary>
/// Dopasowanie frazy w tekście strony (bez PDFium – czysta logika, testowana osobno).
/// Wielkość liter: porównanie porządkowe bez rozróżniania wielkości (mapowanie Unicode – działa dla ą/Ą, ł/Ł…).
/// Całe słowa: znak przed i po trafieniu nie może być literą ani cyfrą (char.IsLetterOrDigit – uwzględnia diakrytyki).
/// Białe znaki w tekście i w zapytaniu zwijane są do jednej spacji, wyrazy przeniesione z dywizem są łączone;
/// wynik to indeksy w ORYGINALNYM tekście (trafienie przez koniec wiersza obejmuje dywiz).
/// </summary>
internal static class TextMatcher
{
    public static IReadOnlyList<(int Start, int Count)> FindAll(string text, string query, bool matchCase, bool wholeWord)
    {
        var q = Collapse(query.Trim(), out _);
        if (q.Length == 0 || string.IsNullOrEmpty(text)) return Array.Empty<(int, int)>();
        // Wyrazy przeniesione z dywizem do następnego wiersza są łączone („przeno-szone” znajduje się jako „przenoszone”).
        var joined = ZetPDF.Core.Utilities.HyphenJoin.Join(text, out var joinMap);
        var hay = Collapse(joined, out var collapseMap);
        var map = new int[collapseMap.Length];
        for (int k = 0; k < map.Length; k++) map[k] = joinMap[collapseMap[k]];
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var result = new List<(int, int)>();
        int from = 0;
        while (from <= hay.Length - q.Length)
        {
            int i = hay.IndexOf(q, from, cmp);
            if (i < 0) break;
            int end = i + q.Length; // wyłącznie
            bool ok = !wholeWord || ((i == 0 || !char.IsLetterOrDigit(hay[i - 1])) && (end >= hay.Length || !char.IsLetterOrDigit(hay[end])));
            if (ok)
            {
                int start = map[i];
                int last = map[end - 1];
                result.Add((start, last - start + 1));
                from = end;
            }
            else
            {
                from = i + 1;
            }
        }
        return result;
    }

    /// <summary>Zwija ciągi białych znaków do jednej spacji; map[i] = indeks znaku w oryginale.</summary>
    private static string Collapse(string s, out int[] map)
    {
        var sb = new StringBuilder(s.Length);
        var m = new List<int>(s.Length);
        bool prevSpace = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == '\0')
            {
                if (prevSpace) continue;
                sb.Append(' ');
                m.Add(i);
                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                m.Add(i);
                prevSpace = false;
            }
        }
        map = m.ToArray();
        return sb.ToString();
    }
}

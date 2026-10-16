using System.Globalization;
using System.Text;

namespace ZetPDF.Core.Utilities;

/// <summary>
/// Zakres stron w notacji użytkownika (1-based): "1-3,5,8-" (8- = do końca). Pusty / "*" = wszystkie.
/// Wynik: posortowane, unikalne indeksy 0-based.
/// </summary>
public static class PageRange
{
    public static IReadOnlyList<int> Parse(string? text, int pageCount)
    {
        if (pageCount < 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*") return Enumerable.Range(0, pageCount).ToArray();

        var set = new SortedSet<int>();
        foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim().Replace('–', '-').Replace('—', '-');
            if (part.Length == 0) continue;
            int dash = part.IndexOf('-');
            int from, to;
            if (dash < 0)
            {
                from = to = ParseNumber(part, text);
            }
            else
            {
                var left = part[..dash].Trim();
                var right = part[(dash + 1)..].Trim();
                from = left.Length == 0 ? 1 : ParseNumber(left, text);
                to = right.Length == 0 ? pageCount : ParseNumber(right, text);
            }
            if (from > to) (from, to) = (to, from);
            if (from < 1 || to > pageCount) throw new FormatException($"Page range '{part}' is outside 1–{pageCount}.");
            for (int i = from; i <= to; i++) set.Add(i - 1);
        }
        return set.ToArray();
    }

    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> pages)
    {
        try { pages = Parse(text, pageCount); return true; }
        catch (FormatException) { pages = Array.Empty<int>(); return false; }
    }

    /// <summary>Indeksy 0-based → "1-3,5".</summary>
    public static string Format(IEnumerable<int> pageIndices)
    {
        var list = pageIndices.Distinct().OrderBy(i => i).ToList();
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            int start = list[i];
            while (i + 1 < list.Count && list[i + 1] == list[i] + 1) i++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append((start + 1).ToString(CultureInfo.InvariantCulture));
            if (list[i] != start) sb.Append('-').Append((list[i] + 1).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static int ParseNumber(string s, string whole) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"Invalid page range '{whole}'.");
}

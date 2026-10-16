using System.Globalization;
using System.Text;

namespace ZetPDF.App.Shell;

/// <summary>
/// Wyszukiwanie poleceń po nazwie w bieżącym języku (Alt+Q). Dopasowanie bez wielkości liter i bez polskich
/// znaków diakrytycznych ("zolc" znajduje "żółć"), kolejność: początek nazwy, początek słowa, dowolne miejsce,
/// wszystkie słowa zapytania. Bez interfejsu – testowane jednostkowo.
/// </summary>
public static class CommandSearch
{
    public sealed record Entry(string CommandId, string Label, string? Tip = null);

    public sealed record Match(Entry Entry, int Score);

    public static IReadOnlyList<Match> Find(string? query, IEnumerable<Entry> entries, int max = 12)
    {
        var q = Normalize(query);
        if (q.Length == 0) return Array.Empty<Match>();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<Match>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!seen.Add(e.CommandId)) continue;
            var label = Normalize(e.Label);
            int score;
            if (label.StartsWith(q, StringComparison.Ordinal)) score = 100;
            else if (label.Contains(' ' + q, StringComparison.Ordinal)) score = 80;
            else if (label.Contains(q, StringComparison.Ordinal)) score = 60;
            else if (words.Length > 1 && words.All(w => label.Contains(w, StringComparison.Ordinal))) score = 50;
            else if (e.Tip is not null && Normalize(e.Tip).Contains(q, StringComparison.Ordinal)) score = 20;
            else continue;
            results.Add(new Match(e, score - Math.Min(label.Length, 40) / 10));
        }
        return results.OrderByDescending(m => m.Score).ThenBy(m => m.Entry.Label, StringComparer.CurrentCultureIgnoreCase).Take(max).ToArray();
    }

    /// <summary>Małe litery, bez diakrytyków (ł→l), pojedyncze spacje, bez wielokropka.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text.Trim().ToLowerInvariant().Replace('ł', 'l').Replace("…", string.Empty, StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = false;
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            lastSpace = false;
            sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

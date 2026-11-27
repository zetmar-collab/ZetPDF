using System.Globalization;
using System.Text;

namespace ZetPDF.App.Shell;

/// <summary>
/// Litery KeyTips wstążki (Alt, potem litera): przydzielane z etykiet w bieżącym języku, bez polskich znaków diakrytycznych,
/// bez powtórzeń w jednym poziomie. Kolejno: pierwsza litera, kolejne litery etykiety, dwie litery (pierwsza + kolejna),
/// na końcu cyfry. Tak samo jak w pakietach biurowych, tylko automatycznie – po zmianie języka litery liczą się od nowa.
/// </summary>
public static class KeyTipAssigner
{
    public static string[] Assign(IReadOnlyList<string> labels, IEnumerable<string>? reserved = null)
    {
        var taken = new HashSet<string>(reserved ?? Array.Empty<string>(), StringComparer.Ordinal);
        var result = new string[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            var letters = Letters(labels[i]);
            string? pick = letters.Select(c => c.ToString()).FirstOrDefault(k => !taken.Contains(k));
            if (pick is null && letters.Length > 0)
                pick = letters.Skip(1).Select(c => string.Concat(letters[0], c)).FirstOrDefault(k => !taken.Contains(k));
            if (pick is null)
                for (int n = 1; n < 100 && pick is null; n++)
                {
                    var k = n.ToString(CultureInfo.InvariantCulture);
                    if (!taken.Contains(k)) pick = k;
                }
            result[i] = pick ?? string.Empty;
            if (pick is not null) taken.Add(pick);
        }
        return result;
    }

    /// <summary>Litery A–Z i cyfry etykiety, wielkie, bez diakrytyków (ł → L), bez powtórzeń.</summary>
    internal static string Letters(string label)
    {
        var sb = new StringBuilder();
        var normalized = (label ?? string.Empty).Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToUpperInvariant(ch);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') && sb.ToString().IndexOf(c) < 0) sb.Append(c);
        }
        return sb.ToString();
    }
}

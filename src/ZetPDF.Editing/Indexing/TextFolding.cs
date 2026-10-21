using System.Globalization;
using System.Text;

namespace ZetPDF.Editing.Indexing;

/// <summary>
/// Normalizacja tekstu do wyszukiwania znak po znaku (długość zachowana – pozycje trafień odpowiadają oryginałowi):
/// małe litery (niezależnie od kultury) i opcjonalnie usunięcie znaków diakrytycznych (ą→a, ł→l, ż→z…).
/// </summary>
public static class TextFolding
{
    private static readonly Dictionary<char, char> Special = new()
    {
        ['ł'] = 'l', ['Ł'] = 'l', ['đ'] = 'd', ['Đ'] = 'd', ['ø'] = 'o', ['Ø'] = 'o', ['ħ'] = 'h', ['ı'] = 'i',
    };

    public static char Fold(char c, bool ignoreCase, bool ignoreDiacritics)
    {
        if (ignoreDiacritics && c > 127)
        {
            if (Special.TryGetValue(c, out var s)) return ignoreCase ? s : char.IsUpper(c) ? char.ToUpperInvariant(s) : s;
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            if (d.Length > 1 && d.Skip(1).All(x => CharUnicodeInfo.GetUnicodeCategory(x) == UnicodeCategory.NonSpacingMark)) c = d[0];
        }
        if (char.IsWhiteSpace(c)) return ' ';
        return ignoreCase ? char.ToLowerInvariant(c) : c;
    }

    public static string Fold(string text, bool ignoreCase, bool ignoreDiacritics)
    {
        var chars = new char[text.Length];
        for (int i = 0; i < text.Length; i++) chars[i] = Fold(text[i], ignoreCase, ignoreDiacritics);
        return new string(chars);
    }
}

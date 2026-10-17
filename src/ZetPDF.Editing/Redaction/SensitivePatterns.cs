using System.Numerics;
using System.Text.RegularExpressions;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Redaction;

/// <summary>Wzorce danych wrażliwych (wyrażenia regularne + walidacja sum kontrolnych).</summary>
public static class SensitivePatterns
{
    // Kolejność = priorytet przy nakładaniu się trafień (wyższy wypiera niższy).
    public static readonly (SensitiveDataPattern Pattern, Regex Regex)[] All =
    {
        (SensitiveDataPattern.IbanPl, new Regex(@"(?<![A-Za-z0-9])PL ?\d{2}(?:[ -]?\d{4}){6}(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (SensitiveDataPattern.CardNumber, new Regex(@"(?<!\d)\d(?:[ -]?\d){12,18}(?!\d)", RegexOptions.Compiled)),
        (SensitiveDataPattern.Email, new Regex(@"(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?![\w-])", RegexOptions.Compiled)),
        (SensitiveDataPattern.Pesel, new Regex(@"(?<!\d)\d{11}(?!\d)", RegexOptions.Compiled)),
        (SensitiveDataPattern.Nip, new Regex(@"(?<!\d)(?:\d{10}|\d{3}-\d{3}-\d{2}-\d{2}|\d{3}-\d{2}-\d{2}-\d{3}|\d{3} \d{3} \d{2} \d{2})(?!\d)", RegexOptions.Compiled)),
        (SensitiveDataPattern.PhonePl, new Regex(@"(?<![\d+])(?:(?:\+|00)48[ -]?)?(?:\d{3}[ -]?\d{3}[ -]?\d{3}|\(?\d{2}\)?[ -]?\d{3}[ -]?\d{2}[ -]?\d{2})(?!\d)", RegexOptions.Compiled)),
    };

    public static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    public static bool Validate(SensitiveDataPattern pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (pattern)
        {
            case SensitiveDataPattern.Pesel:
            {
                var d = Digits(value);
                if (d.Length != 11 || d.Length != value.Trim().Length) return false;
                int[] w = { 1, 3, 7, 9, 1, 3, 7, 9, 1, 3 };
                int sum = 0;
                for (int i = 0; i < 10; i++) sum += (d[i] - '0') * w[i];
                if ((10 - sum % 10) % 10 != d[10] - '0') return false;
                int month = (d[2] - '0') * 10 + (d[3] - '0'), day = (d[4] - '0') * 10 + (d[5] - '0');
                return month % 20 is >= 1 and <= 12 && day is >= 1 and <= 31;
            }
            case SensitiveDataPattern.Nip:
            {
                var d = Digits(value);
                if (d.Length != 10) return false;
                int[] w = { 6, 5, 7, 2, 3, 4, 5, 6, 7 };
                int sum = 0;
                for (int i = 0; i < 9; i++) sum += (d[i] - '0') * w[i];
                int c = sum % 11;
                return c != 10 && c == d[9] - '0';
            }
            case SensitiveDataPattern.CardNumber:
            {
                var d = Digits(value);
                if (d.Length is < 13 or > 19) return false;
                int sum = 0;
                bool dbl = false;
                for (int i = d.Length - 1; i >= 0; i--)
                {
                    int x = d[i] - '0';
                    if (dbl) { x *= 2; if (x > 9) x -= 9; }
                    sum += x;
                    dbl = !dbl;
                }
                return sum % 10 == 0;
            }
            case SensitiveDataPattern.IbanPl:
            {
                var s = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                if (s.Length != 28 || !s.StartsWith("PL", StringComparison.Ordinal) || !s.Skip(2).All(char.IsDigit)) return false;
                var rearranged = s[4..] + "2521" + s[2..4];
                return BigInteger.Parse(rearranged, System.Globalization.CultureInfo.InvariantCulture) % 97 == 1;
            }
            case SensitiveDataPattern.Email:
                return All.First(p => p.Pattern == SensitiveDataPattern.Email).Regex.IsMatch(value);
            case SensitiveDataPattern.PhonePl:
            {
                var d = Digits(value);
                if (d.StartsWith("0048", StringComparison.Ordinal)) d = d[4..];
                else if (d.Length == 11 && d.StartsWith("48", StringComparison.Ordinal)) d = d[2..];
                return d.Length == 9 && d[0] != '0';
            }
            default:
                return false;
        }
    }

    /// <summary>Trafienia wzorców w tekście (indeks, długość, wzorzec) – po walidacji, bez nakładania się.</summary>
    public static List<(int Index, int Length, SensitiveDataPattern Pattern)> Find(string text, SensitiveDataPattern patterns)
    {
        var taken = new List<(int Index, int Length, SensitiveDataPattern Pattern)>();
        foreach (var (pattern, regex) in All)
        {
            if ((patterns & pattern) == 0) continue;
            foreach (Match m in regex.Matches(text))
            {
                var value = m.Value.Trim();
                if (!Validate(pattern, pattern == SensitiveDataPattern.Pesel ? value : m.Value)) continue;
                if (taken.Any(t => m.Index < t.Index + t.Length && t.Index < m.Index + m.Length)) continue;
                taken.Add((m.Index, m.Length, pattern));
            }
        }
        return taken.OrderBy(t => t.Index).ToList();
    }
}

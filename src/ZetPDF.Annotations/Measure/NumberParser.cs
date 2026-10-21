using System.Globalization;
using System.Text.RegularExpressions;

namespace ZetPDF.Annotations.Measure;

/// <summary>
/// Liczby z tekstu dokumentu dla kalkulatora księgowego: format polski („1 234,56 zł”, „-12,5”, „(1 234,00)” = ujemna),
/// angielski („1,234.56”), separatory tysięcy: spacja, spacja nierozdzielająca, kropka/przecinek, apostrof.
/// Przy niejednoznacznym zapisie („1,234” / „1.234”) decyduje kultura interfejsu.
/// </summary>
public static partial class NumberParser
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}.,])(?<open>\()?[ \t]*(?<sign>[-−–])?(?:[$€£][  ]?)?(?<int>\d{1,3}(?:[   ']\d{3})+(?!\d)|\d+)(?<rest>(?:[.,]\d+)*)(?:[  ]?(?:zł|PLN|EUR|USD|GBP|€|\$|£|%))?[ \t]*(?<close>\))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    public static IReadOnlyList<decimal> Parse(string? text, CultureInfo culture)
    {
        var result = new List<decimal>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (Match m in NumberRegex().Matches(text))
            if (TryNormalize(m, culture, out var v)) result.Add(v);
        return result;
    }

    /// <summary>Jedna liczba (pole edycji kalkulatora); null, gdy tekst nie jest liczbą.</summary>
    public static decimal? ParseSingle(string? text, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = NumberRegex().Match(text.Trim());
        if (!m.Success || m.Index != 0 || m.Length != text.Trim().Length) return null;
        return TryNormalize(m, culture, out var v) ? v : null;
    }

    private static bool TryNormalize(Match m, CultureInfo culture, out decimal value)
    {
        value = 0;
        var intRaw = m.Groups["int"].Value;
        bool grouped = intRaw.Any(c => c is ' ' or ' ' or ' ' or '\'');
        var digits = new string(intRaw.Where(char.IsDigit).ToArray());
        var rest = m.Groups["rest"].Value;
        string number;
        if (rest.Length == 0) number = digits;
        else
        {
            var groups = new List<(char Sep, string Digits)>();
            for (int i = 0; i < rest.Length;)
            {
                char sep = rest[i++];
                int start = i;
                while (i < rest.Length && char.IsDigit(rest[i])) i++;
                groups.Add((sep, rest[start..i]));
            }
            bool hasDot = groups.Any(g => g.Sep == '.'), hasComma = groups.Any(g => g.Sep == ',');
            char? dec;
            if (hasDot && hasComma) dec = groups[^1].Sep;
            else if (groups.Count > 1)
                dec = groups.Take(groups.Count - 1).All(g => g.Digits.Length == 3) && groups[^1].Digits.Length == 3 ? null : groups[^1].Sep;
            else if (grouped || groups[0].Digits.Length != 3 || digits.StartsWith('0') && digits.Length == 1) dec = groups[0].Sep;
            else
            {
                // „1,234” / „1.234”: separator dziesiętny kultury wygrywa
                var cultureDec = culture.NumberFormat.NumberDecimalSeparator;
                dec = cultureDec.Contains(groups[0].Sep) ? groups[0].Sep : null;
            }
            var sb = new System.Text.StringBuilder(digits);
            for (int i = 0; i < groups.Count; i++)
            {
                bool isDec = dec is not null && i == groups.Count - 1 && groups[i].Sep == dec;
                if (isDec) sb.Append('.');
                else if (dec is not null && groups[i].Sep == dec) return false; // dwa separatory dziesiętne
                sb.Append(groups[i].Digits);
            }
            number = sb.ToString();
        }
        if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        bool negative = m.Groups["sign"].Success || m.Groups["open"].Success && m.Groups["close"].Success;
        if (negative) value = -value;
        return true;
    }
}

public enum TapeOp { Add, Subtract, Multiply, Divide }

/// <summary>Wpis taśmy: operacja, liczba i źródło (wpisana / z dokumentu).</summary>
public sealed record TapeEntry(TapeOp Op, decimal Value, bool FromDocument);

/// <summary>Taśma kalkulatora (jak sumator): wynik = kolejne operacje od zera; historia wyników po wyczyszczeniu.</summary>
public sealed class CalculatorTape
{
    private readonly List<TapeEntry> _entries = new();
    private readonly List<decimal> _history = new();

    public IReadOnlyList<TapeEntry> Entries => _entries;
    public IReadOnlyList<decimal> History => _history;

    public event EventHandler? Changed;

    public decimal Total
    {
        get
        {
            decimal t = 0;
            foreach (var e in _entries)
                t = e.Op switch
                {
                    TapeOp.Add => t + e.Value,
                    TapeOp.Subtract => t - e.Value,
                    TapeOp.Multiply => t * e.Value,
                    _ => e.Value == 0 ? t : t / e.Value,
                };
            return t;
        }
    }

    /// <summary>Dodaje wpis; dzielenie przez zero jest odrzucane (false).</summary>
    public bool Add(TapeOp op, decimal value, bool fromDocument = false)
    {
        if (op == TapeOp.Divide && value == 0) return false;
        try
        {
            _entries.Add(new TapeEntry(op, value, fromDocument));
            _ = Total; // przepełnienie decimal → wyjątek
        }
        catch (OverflowException)
        {
            _entries.RemoveAt(_entries.Count - 1);
            return false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RemoveLast()
    {
        if (_entries.Count == 0) return;
        _entries.RemoveAt(_entries.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_entries.Count > 0) _history.Insert(0, Total);
        _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string Symbol(TapeOp op) => op switch { TapeOp.Add => "+", TapeOp.Subtract => "−", TapeOp.Multiply => "×", _ => "÷" };

    /// <summary>Liczba w formacie kultury (separator tysięcy, 2 miejsca, więcej gdy potrzeba – maks. 6).</summary>
    public static string Format(decimal value, CultureInfo culture)
    {
        var rounded = Math.Round(value, 6);
        int decimals = 2;
        while (decimals < 6 && Math.Round(rounded, decimals) != rounded) decimals++;
        return rounded.ToString("N" + decimals, culture);
    }
}

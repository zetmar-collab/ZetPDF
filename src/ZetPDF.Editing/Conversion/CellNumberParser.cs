using System.Globalization;
using System.Text.RegularExpressions;

namespace ZetPDF.Editing.Conversion;

/// <summary>Który znak jest separatorem dziesiętnym w dokumencie (z zapisów jednoznacznych).</summary>
internal enum DecimalSeparatorHint { Unknown, Comma, Dot }

/// <summary>
/// Rozpoznawanie liczb w komórkach (format polski i angielski). Zasada: lepiej zostawić tekst, niż po cichu zapisać
/// wartość tysiąc razy inną. „1,234” i „1.234” są niejednoznaczne: bez wskazówki z reszty dokumentu zostają tekstem.
/// Numery (zera na początku, 11 i więcej cyfr), daty, kody i wartości z jednostką też zostają tekstem.
/// </summary>
internal static partial class CellNumberParser
{
    private const int MaxSignificantDigits = 15;
    private const int MaxPlainIntegerDigits = 10;

    [GeneratedRegex(@"^\d+$")] private static partial Regex Plain();
    [GeneratedRegex(@"^\d{1,3}( \d{3})+([.,]\d+)?$")] private static partial Regex SpaceGrouped();
    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+,\d+$")] private static partial Regex DotGroupedCommaDecimal();
    [GeneratedRegex(@"^\d{1,3}(,\d{3})+\.\d+$")] private static partial Regex CommaGroupedDotDecimal();
    [GeneratedRegex(@"^\d{1,3}(\.\d{3}){2,}$")] private static partial Regex DotGrouped();
    [GeneratedRegex(@"^\d{1,3}(,\d{3}){2,}$")] private static partial Regex CommaGrouped();
    [GeneratedRegex(@"^\d+,\d+$")] private static partial Regex CommaDecimal();
    [GeneratedRegex(@"^\d+\.\d+$")] private static partial Regex DotDecimal();

    /// <summary>Wskazówka z całego dokumentu: separator wygrywa, gdy zapisów jednoznacznych ma co najmniej trzy razy więcej.</summary>
    public static DecimalSeparatorHint DetectHint(IEnumerable<string> cells)
    {
        int comma = 0, dot = 0;
        foreach (var cell in cells)
        {
            var s = Normalize(cell, out _);
            if (s.Length == 0) continue;
            if (DotGroupedCommaDecimal().IsMatch(s) || DotGrouped().IsMatch(s)) comma++;
            else if (CommaGroupedDotDecimal().IsMatch(s) || CommaGrouped().IsMatch(s)) dot++;
            else if (SpaceGrouped().IsMatch(s)) { if (s.Contains(',')) comma++; else if (s.Contains('.')) dot++; }
            else if (CommaDecimal().IsMatch(s) && !IsAmbiguous(s, ',')) comma++;
            else if (DotDecimal().IsMatch(s) && !IsAmbiguous(s, '.')) dot++;
        }
        if (comma > 0 && comma >= dot * 3) return DecimalSeparatorHint.Comma;
        if (dot > 0 && dot >= comma * 3) return DecimalSeparatorHint.Dot;
        return DecimalSeparatorHint.Unknown;
    }

    public static bool TryParse(string? text, DecimalSeparatorHint hint, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = Normalize(text, out bool negative);
        if (s.Length == 0) return false;
        // „+48 123 456 789” to telefon, a nie kwota.
        if (text.TrimStart().StartsWith('+') && SpaceGrouped().IsMatch(s)) return false;

        string? invariant = null;
        if (Plain().IsMatch(s))
        {
            if (s.Length > MaxPlainIntegerDigits || (s.Length > 1 && s[0] == '0')) return false;
            invariant = s;
        }
        else if (SpaceGrouped().IsMatch(s))
        {
            if (s[0] == '0') return false;
            if (s.Contains(',') && hint == DecimalSeparatorHint.Dot) return false;
            if (s.Contains('.') && hint == DecimalSeparatorHint.Comma) return false;
            invariant = s.Replace(" ", string.Empty).Replace(',', '.');
        }
        else if (DotGroupedCommaDecimal().IsMatch(s) || DotGrouped().IsMatch(s))
        {
            if (s[0] == '0') return false;
            invariant = s.Replace(".", string.Empty).Replace(',', '.');
        }
        else if (CommaGroupedDotDecimal().IsMatch(s) || CommaGrouped().IsMatch(s))
        {
            if (s[0] == '0') return false;
            invariant = s.Replace(",", string.Empty);
        }
        else if (CommaDecimal().IsMatch(s))
        {
            invariant = Single(s, ',', hint == DecimalSeparatorHint.Comma, hint == DecimalSeparatorHint.Dot);
        }
        else if (DotDecimal().IsMatch(s))
        {
            invariant = Single(s, '.', hint == DecimalSeparatorHint.Dot, hint == DecimalSeparatorHint.Comma);
        }
        if (invariant is null) return false;
        if (invariant.Count(char.IsDigit) > MaxSignificantDigits) return false;
        if (!double.TryParse(invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value)) return false;
        if (negative) value = -value;
        return true;
    }

    /// <summary>Jeden separator: dziesiętny, grupujący albo (bez wskazówki, gdy niejednoznaczny) żaden z nich.</summary>
    private static string? Single(string s, char sep, bool isDecimalSep, bool isOtherDecimalSep)
    {
        int at = s.IndexOf(sep);
        if (at > 1 && s[0] == '0') return null;
        if (IsAmbiguous(s, sep))
        {
            if (isDecimalSep) return s.Replace(sep, '.');
            return isOtherDecimalSep ? s.Remove(at, 1) : null;
        }
        // Wiadomo, że dokument używa drugiego separatora, więc to nie jest liczba (np. „12.05” to data).
        return isOtherDecimalSep ? null : s.Replace(sep, '.');
    }

    /// <summary>„1,234”: 1–3 cyfry, separator, dokładnie 3 cyfry, i nie „0,123”.</summary>
    private static bool IsAmbiguous(string s, char sep)
    {
        int at = s.IndexOf(sep);
        return at is >= 1 and <= 3 && s.Length - at - 1 == 3 && s[0] != '0';
    }

    private static string Normalize(string text, out bool negative)
    {
        negative = false;
        var s = text.Trim().Replace((char)0x00A0, ' ').Replace((char)0x2009, ' ').Replace((char)0x202F, ' ');
        if (s.Length == 0) return s;
        if (s[0] is '-' or '+' || s[0] == (char)0x2212)
        {
            negative = s[0] != '+';
            s = s[1..];
        }
        return s;
    }
}

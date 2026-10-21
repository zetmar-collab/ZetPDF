namespace ZetPDF.Annotations;

/// <summary>
/// Komórki CSV dla wszystkich eksportów modułu: neutralizacja wstrzykiwania formuł (CSV/Formula Injection) dla tekstów
/// pochodzących z dokumentu lub od użytkownika (początek '=', '+', '-', '@', tabulator, '\r' → poprzedzony apostrofem),
/// potem cytowanie RFC 4180. Liczby generowane przez program przekazuj z <c>neutralize: false</c>.
/// </summary>
public static class CsvText
{
    private static readonly char[] Dangerous = { '=', '+', '-', '@', '\t', '\r' };

    public static bool NeedsNeutralizing(string s) => s.Length > 0 && Array.IndexOf(Dangerous, s[0]) >= 0;

    /// <summary>Tekst bezpieczny dla arkusza: apostrof przed znakiem rozpoczynającym formułę.</summary>
    public static string Neutralize(string s) => NeedsNeutralizing(s) ? "'" + s : s;

    /// <summary>Odwrotność <see cref="Neutralize"/> przy imporcie CSV zapisanego przez ZetPDF.</summary>
    public static string Restore(string s) => s.Length > 1 && s[0] == '\'' && Array.IndexOf(Dangerous, s[1]) >= 0 ? s[1..] : s;

    /// <summary>Komórka: neutralizacja (opcjonalnie) + cytowanie, gdy zawiera separator, cudzysłów lub koniec wiersza.</summary>
    public static string Cell(string? value, char separator, bool neutralize = true)
    {
        var s = value ?? string.Empty;
        if (neutralize) s = Neutralize(s);
        // przy separatorze ',' cytujemy też ';' (import wykrywa separator z nagłówka)
        var special = separator == ',' ? new[] { ',', ';', '"', '\n', '\r' } : new[] { separator, '"', '\n', '\r' };
        return s.IndexOfAny(special) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

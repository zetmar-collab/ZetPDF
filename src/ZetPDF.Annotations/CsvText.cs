namespace ZetPDF.Annotations;

/// <summary>
/// Komórki CSV dla wszystkich eksportów modułu: neutralizacja wstrzykiwania formuł (CSV/Formula Injection) dla tekstów
/// pochodzących z dokumentu lub od użytkownika (początek '=', '+', '-', '@', tabulator, '\r' → poprzedzony apostrofem),
/// potem cytowanie RFC 4180. Liczby generowane przez program przekazuj z <c>neutralize: false</c>.
/// </summary>
public static class CsvText
{
    private static readonly char[] Special = { ',', ';', '\t', '"', '\n', '\r' };
    private static readonly char[] Dangerous = { '=', '+', '-', '@', '\t', '\r' };

    public static bool NeedsNeutralizing(string s) => s.Length > 0 && Array.IndexOf(Dangerous, s[0]) >= 0;

    /// <summary>Tekst bezpieczny dla arkusza: apostrof przed znakiem rozpoczynającym formułę.</summary>
    public static string Neutralize(string s) => NeedsNeutralizing(s) ? "'" + s : s;

    /// <summary>Odwrotność <see cref="Neutralize"/> przy imporcie CSV zapisanego przez ZetPDF.</summary>
    public static string Restore(string s) => s.Length > 1 && s[0] == '\'' && Array.IndexOf(Dangerous, s[1]) >= 0 ? s[1..] : s;

    /// <summary>Komórka: neutralizacja (opcjonalnie) + cytowanie, gdy zawiera separator, cudzysłów lub koniec wiersza.</summary>
    public static string Cell(string? value, char separator, bool neutralize = true)
    {
        _ = separator; // cytowanie nie zależy od separatora pliku (patrz niżej)
        return CellCore(value, neutralize);
    }

    private static string CellCore(string? value, bool neutralize)
    {
        var s = value ?? string.Empty;
        if (neutralize) s = Neutralize(s);
        // cytujemy ZAWSZE wartości z dowolnym znakiem separatora (',', ';', tabulator), cudzysłowem lub końcem wiersza –
        // niezależnie od separatora pliku: czytnik z innym separatorem (ustawienia regionalne) nie rozbije wartości na komórki
        return s.IndexOfAny(Special) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

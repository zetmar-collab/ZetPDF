using System.Globalization;

namespace ZetPDF.Pages.Stamping;

/// <summary>Makra tekstów nagłówka/stopki i numeracji: &lt;&lt;strona&gt;&gt;/&lt;&lt;page&gt;&gt;, &lt;&lt;stron&gt;&gt;/&lt;&lt;pages&gt;&gt;, &lt;&lt;data&gt;&gt;/&lt;&lt;date&gt;&gt;, &lt;&lt;plik&gt;&gt;/&lt;&lt;file&gt;&gt;.</summary>
internal static class Macros
{
    /// <param name="file">Nazwa pliku (powłoka podstawia ją wcześniej; tu rezerwowo tytuł dokumentu).</param>
    public static string Expand(string? text, int page, int pages, string date, string file)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string p = page.ToString(CultureInfo.InvariantCulture), n = pages.ToString(CultureInfo.InvariantCulture);
        return text
            .Replace("<<strona>>", p, StringComparison.OrdinalIgnoreCase)
            .Replace("<<page>>", p, StringComparison.OrdinalIgnoreCase)
            .Replace("<<stron>>", n, StringComparison.OrdinalIgnoreCase)
            .Replace("<<pages>>", n, StringComparison.OrdinalIgnoreCase)
            .Replace("<<data>>", date, StringComparison.OrdinalIgnoreCase)
            .Replace("<<date>>", date, StringComparison.OrdinalIgnoreCase)
            .Replace("<<plik>>", file, StringComparison.OrdinalIgnoreCase)
            .Replace("<<file>>", file, StringComparison.OrdinalIgnoreCase)
            .Trim('\r', '\n');
    }

    /// <summary>Podstawia nazwę pliku (wywołuje warstwa poleceń, która ją zna).</summary>
    public static string WithFile(string? text, string fileName) =>
        (text ?? string.Empty).Replace("<<plik>>", fileName, StringComparison.OrdinalIgnoreCase).Replace("<<file>>", fileName, StringComparison.OrdinalIgnoreCase);
}

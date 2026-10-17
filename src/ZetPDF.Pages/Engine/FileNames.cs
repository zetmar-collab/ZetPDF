using System.Globalization;

namespace ZetPDF.Pages.Engine;

/// <summary>Wzorce nazw plików wynikowych: {name}, {n}, {page}, {bookmark} (także polskie {nazwa}, {nr}, {strona}, {zakladka}).</summary>
internal static class FileNames
{
    public static string Expand(string pattern, string baseName, int part, int firstPage, string bookmark)
    {
        var s = pattern
            .Replace("{name}", baseName, StringComparison.OrdinalIgnoreCase)
            .Replace("{nazwa}", baseName, StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", part.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{nr}", part.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{page}", firstPage.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{strona}", firstPage.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{bookmark}", bookmark, StringComparison.OrdinalIgnoreCase)
            .Replace("{zakladka}", bookmark, StringComparison.OrdinalIgnoreCase)
            .Replace("{zakładka}", bookmark, StringComparison.OrdinalIgnoreCase);
        if (s.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return PdfIo.SafeFileName(s) + ".pdf";
    }
}

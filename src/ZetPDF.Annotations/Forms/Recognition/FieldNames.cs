using System.Globalization;
using System.Text;

namespace ZetPDF.Annotations.Forms.Recognition;

/// <summary>Nazwy pól z etykiet: bez kropki (separator hierarchii w nazwach pól PDF) i znaków sterujących, unikalne, ograniczona długość.</summary>
internal static class FieldNames
{
    public const int MaxLength = 40;

    /// <summary>Zostawia litery, cyfry, podkreślenie i myślnik; ciągi innych znaków stają się jednym podkreśleniem.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(MaxLength + 1);
        bool pendingSeparator = false;
        foreach (char c in text.Normalize(NormalizationForm.FormC))
        {
            if (sb.Length >= MaxLength) break;
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
            {
                pendingSeparator = sb.Length > 0;
                continue;
            }
            if (pendingSeparator)
            {
                sb.Append('_');
                pendingSeparator = false;
                if (sb.Length >= MaxLength) break;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim('_', '-');
    }

    /// <summary>Nazwa niewystępująca w <paramref name="used"/>; dopisuje ją do zbioru. Pusta etykieta daje nazwę ogólną z numerem.</summary>
    public static string Unique(string? label, string generic, ISet<string> used)
    {
        var name = Sanitize(label);
        if (name.Length > 0 && used.Add(name)) return name;
        var stem = name.Length > 0 ? name : Sanitize(generic);
        if (stem.Length == 0) stem = "Field";
        if (stem.Length > MaxLength - 6) stem = stem[..(MaxLength - 6)];
        for (int n = name.Length > 0 ? 2 : 1; ; n++)
        {
            var candidate = stem + "_" + n.ToString(CultureInfo.InvariantCulture);
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>Pełne nazwy pól i ich prefiksy: nowe pole nie może mieć nazwy rodzica istniejącego pola.</summary>
    public static HashSet<string> UsedFrom(IEnumerable<string> fullNames)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in fullNames)
        {
            int dot = -1;
            while ((dot = full.IndexOf('.', dot + 1)) >= 0) used.Add(full[..dot]);
            used.Add(full);
        }
        return used;
    }
}

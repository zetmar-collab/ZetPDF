using System.Text.RegularExpressions;

namespace ZetPDF.Editing.Conversion;

public enum WebSourceError { None, Empty, LeadingDash, UnsupportedScheme, NotHtml, FileNotFound, NetworkPath, InvalidAddress }

/// <param name="Argument">Adres dla przeglądarki: http(s) albo file:///</param>
public sealed record WebSource(string Argument, bool IsRemote, string SuggestedName);

/// <summary>
/// Sprawdzenie tego, co wpisał użytkownik: tylko http, https i lokalne pliki .html/.htm. Wartość zaczynająca się od „-”
/// mogłaby stać się przełącznikiem przeglądarki, a znaki sterujące lub spacje w adresie nie mają tam czego szukać.
/// </summary>
public static partial class WebSourceValidator
{
    [GeneratedRegex(@"^[A-Za-z]:[\\/]")] private static partial Regex DrivePath();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:(?!\d)")] private static partial Regex Scheme();

    public static WebSourceError Validate(string? input, out WebSource? source)
    {
        source = null;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0) return WebSourceError.Empty;
        if (text[0] == '-') return WebSourceError.LeadingDash;
        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)) return WebSourceError.NetworkPath;
        if (DrivePath().IsMatch(text)) return LocalFile(text, out source);

        var withScheme = text;
        if (Scheme().IsMatch(text))
        {
            var scheme = text[..text.IndexOf(':')].ToLowerInvariant();
            if (scheme == "file") return FileUri(text, out source);
            if (scheme is not ("http" or "https")) return WebSourceError.UnsupportedScheme;
        }
        else withScheme = "https://" + text;

        if (text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return WebSourceError.InvalidAddress;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return WebSourceError.InvalidAddress;
        // Dane logowania w adresie to częsty sposób na podszycie się pod inny host.
        if (!string.IsNullOrEmpty(uri.UserInfo)) return WebSourceError.InvalidAddress;
        // Bez schematu tylko coś, co wygląda na nazwę hosta.
        if (withScheme != text && !uri.Host.Contains('.') && !uri.IsLoopback) return WebSourceError.InvalidAddress;
        source = new WebSource(uri.AbsoluteUri, true, uri.Host + ".pdf");
        return WebSourceError.None;
    }

    private static WebSourceError FileUri(string text, out WebSource? source)
    {
        source = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile) return WebSourceError.InvalidAddress;
        if (uri.IsUnc || !string.IsNullOrEmpty(uri.Host)) return WebSourceError.NetworkPath;
        return LocalFile(uri.LocalPath, out source);
    }

    private static WebSourceError LocalFile(string path, out WebSource? source)
    {
        source = null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return WebSourceError.InvalidAddress; }
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is not (".html" or ".htm")) return WebSourceError.NotHtml;
        if (!File.Exists(full)) return WebSourceError.FileNotFound;
        source = new WebSource(new Uri(full).AbsoluteUri, false, Path.GetFileNameWithoutExtension(full) + ".pdf");
        return WebSourceError.None;
    }
}

namespace ZetPDF.Core.Services;

/// <summary>Łącza z dokumentów: tylko bezpieczne schematy i opis adresu do okna potwierdzenia.</summary>
public static class LinkSafety
{
    /// <summary>Czy adres można otworzyć w przeglądarce lub programie pocztowym (po potwierdzeniu).</summary>
    public static bool IsAllowed(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" or "mailto";

    /// <summary>
    /// Adres do okna potwierdzenia. Domena z literami spoza ASCII (np. cyrylickie „а” udające łacińskie „a”) jest dopisywana
    /// w postaci punycode, żeby podróbka znanej domeny była widoczna.
    /// </summary>
    public static string Describe(Uri uri)
    {
        var text = uri.AbsoluteUri;
        if (uri.Scheme is "http" or "https" && !string.Equals(uri.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase))
            text += Environment.NewLine + "(" + uri.IdnHost + ")";
        return text;
    }
}

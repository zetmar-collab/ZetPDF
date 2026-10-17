using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Localization;

/// <summary>
/// Zlokalizowane komunikaty błędów pokazywanych użytkownikowi (DECYZJE O-3). Wyjątki domenowe zachowują typ
/// i angielski <see cref="Exception.Message"/> (dziennik, szczegóły techniczne), a klucz tekstu z argumentami niesie
/// <see cref="Exception.Data"/> – tekst powstaje w chwili wyświetlenia, w bieżącym języku interfejsu.
/// Wyjątki bez klucza (biblioteki, .NET) dostają ogólny zlokalizowany tekst wg rodzaju; ich angielski komunikat
/// trafia wyłącznie do rozwijanych szczegółów i dziennika.
/// </summary>
public static class LocalizedError
{
    private const string KeyEntry = "ZetPDF.MessageKey";
    private const string ArgsEntry = "ZetPDF.MessageArgs";
    private const string LocalizedEntry = "ZetPDF.MessageLocalized";
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    /// <summary>Dołącza klucz tekstu (zasoby .resx) i argumenty formatu do wyjątku. Zwraca ten sam wyjątek.</summary>
    public static TException WithMessageKey<TException>(this TException exception, string key, params object?[] args) where TException : Exception
    {
        exception.Data[KeyEntry] = key;
        exception.Data[ArgsEntry] = args.Select(a => a?.ToString()).ToArray();
        return exception;
    }

    /// <summary>Oznacza wyjątek, którego <see cref="Exception.Message"/> jest już w języku interfejsu.</summary>
    public static TException MarkLocalized<TException>(this TException exception) where TException : Exception
    {
        exception.Data[LocalizedEntry] = true;
        return exception;
    }

    /// <summary>Czy komunikat wyjątku jest już w języku interfejsu (<see cref="MarkLocalized"/>).</summary>
    public static bool IsLocalized(Exception exception) => exception.Data[LocalizedEntry] is true;

    public static bool TryGetMessageKey(Exception exception, out string key, out object?[] args)
    {
        if (exception.Data[KeyEntry] is string k)
        {
            key = k;
            args = exception.Data[ArgsEntry] as object?[] ?? Array.Empty<object?>();
            return true;
        }
        key = string.Empty;
        args = Array.Empty<object?>();
        return false;
    }

    /// <summary>
    /// Tekst błędu dla użytkownika w bieżącym języku. <paramref name="isSpecific"/> = false, gdy to tylko ogólny
    /// opis nieoczekiwanego błędu (wtedy warto pokazać szczegóły techniczne).
    /// </summary>
    public static string Describe(this ILocalizationService loc, Exception exception, out bool isSpecific, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(exception);
        isSpecific = true;
        if (exception is AggregateException { InnerExceptions.Count: 1 } agg) exception = agg.InnerExceptions[0];
        if (TryGetMessageKey(exception, out var key, out var args) && loc.HasKey(key)) return loc.Format(key, args);
        if (exception.Data[LocalizedEntry] is true) return exception.Message;
        var file = context ?? string.Empty;
        switch (exception)
        {
            case PdfCorruptException: return loc.Format("Core.Error.Corrupt", file);
            case PdfInvalidPasswordException: return loc["Core.Error.InvalidPassword"];
            case PdfPasswordRequiredException: return loc["Core.Error.PasswordRequired"];
            case DocumentPermissionException: return loc["Core.Error.PermissionDenied"];
            case NotImplementedException: return loc["Core.Error.NotImplemented"];
            case OperationCanceledException: return loc["Core.Error.Cancelled"];
            case FileNotFoundException fnf: return loc.Format("Core.Error.FileNotFound", fnf.FileName ?? file);
            case DirectoryNotFoundException: return loc.Format("Core.Error.FolderNotFound", file);
            case UnauthorizedAccessException: return loc.Format("Core.Error.AccessDenied", file);
            case PathTooLongException: return loc.Format("Core.Error.PathTooLong", file);
            case IOException: return loc.Format("Core.Error.FileInUse", file);
            case ExternalException ext when ext.ErrorCode == ClipboardCantOpen || ext.HResult == ClipboardCantOpen: return loc["Core.Error.Clipboard"];
            case XmlException:
            case InvalidDataException:
            case FormatException:
                return loc["Core.Error.InvalidFormat"];
            case CryptographicException: return loc["Core.Error.Cryptography"];
            case OutOfMemoryException: return loc["Core.Error.OutOfMemory"];
        }
        isSpecific = false;
        return loc["Core.Error.Unexpected"];
    }

    public static string Describe(this ILocalizationService loc, Exception exception, string? context = null) =>
        Describe(loc, exception, out _, context);

    /// <summary>Szczegóły techniczne (po angielsku – komunikat biblioteki/.NET) do rozwijanej sekcji okna błędu.</summary>
    public static string TechnicalDetails(Exception exception) =>
        exception.GetType().FullName + ": " + exception.Message;
}

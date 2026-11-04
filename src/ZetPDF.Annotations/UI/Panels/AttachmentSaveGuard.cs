using System.Globalization;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Panels;

/// <summary>
/// Zapis załącznika na dysk: nazwa z PDF jest niezaufana, a plik wykonywalny zapisany z dokumentu to typowy sposób ataku.
/// Podpowiedź nazwy jest oczyszczana (bez folderów, znaków niedozwolonych i nazw urządzeń), a typ wykonywalny wymaga potwierdzenia.
/// </summary>
internal static class AttachmentSaveGuard
{
    private const int MaxSuggestedNameLength = 200;

    /// <summary>Typy uruchamiane przez Windows, jego skrypty i popularne środowiska wykonawcze.</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".mst", ".dll", ".ocx", ".sys", ".drv",
        ".ps1", ".psm1", ".psd1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2",
        ".vbs", ".vbe", ".vb", ".js", ".jse", ".wsf", ".wsh", ".wsc", ".ws", ".sct",
        ".lnk", ".url", ".scf", ".inf", ".reg", ".hta", ".chm", ".hlp", ".msc", ".gadget", ".shb", ".shs",
        ".jar", ".jnlp", ".py", ".pyw", ".appx", ".appxbundle", ".msix", ".msixbundle", ".appinstaller", ".application", ".appref-ms", ".xbap",
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Czy Windows potraktuje nazwę jako typ wykonywalny (końcowe kropki i spacje system i tak pomija).</summary>
    public static bool IsExecutable(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return ExecutableExtensions.Contains(Path.GetExtension(name.TrimEnd(' ', '.')));
    }

    /// <summary>Bezpieczna podpowiedź nazwy: sama nazwa pliku, bez znaków niedozwolonych i niewidocznych znaków sterujących kierunkiem tekstu.</summary>
    public static string SuggestFileName(string? attachmentName, IFileService files)
    {
        var name = Path.GetFileName((attachmentName ?? string.Empty).Replace('/', '\\'));
        // Znaki formatujące (np. odwrócenie kierunku tekstu) potrafią ukryć prawdziwe rozszerzenie.
        name = string.Concat(name.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format));
        name = files.SanitizeFileName(name);
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedDeviceNames.Contains(stem)) name = "_" + name;
        if (name.Length > MaxSuggestedNameLength)
        {
            if (extension.Length >= MaxSuggestedNameLength) extension = string.Empty;
            name = name[..(MaxSuggestedNameLength - extension.Length)] + extension;
        }
        return name;
    }

    /// <summary>Okno zapisu z oczyszczoną nazwą i ostrzeżeniem o typie wykonywalnym. Null = anulowano.</summary>
    public static string? ChooseTarget(string attachmentName, IDialogService dialogs, IFileService files, ILocalizationService loc)
    {
        var suggested = SuggestFileName(attachmentName, files);
        var target = dialogs.SaveFile(loc["Annot.Attach.Save"], new[] { FileFilter.All(loc) }, suggested);
        if (target is null) return null;
        // Liczy się także nazwa wybrana w oknie: użytkownik mógł zmienić rozszerzenie w obie strony, a zawartość pliku pozostaje ta sama.
        var risky = IsExecutable(target) ? target : IsExecutable(attachmentName) ? attachmentName : IsExecutable(suggested) ? suggested : null;
        if (risky is null) return target;
        var shown = Path.GetExtension(risky.TrimEnd(' ', '.'));
        return dialogs.Confirm(loc.Format("Annot.Attach.ExecutableWarning", suggested, shown), loc["Annot.Attach.ExecutableTitle"], MessageSeverity.Warning) ? target : null;
    }
}

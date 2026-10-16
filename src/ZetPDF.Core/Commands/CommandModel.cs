namespace ZetPDF.Core.Commands;

/// <summary>Moduł (projekt) odpowiedzialny za obsługę polecenia. Mapowanie na agentów: docs/ARCHITEKTURA.md.</summary>
public enum FeatureModuleId
{
    Shell,        // ZetPDF.App          – ui-shell
    Rendering,    // ZetPDF.Rendering    – renderer-viewer
    Viewer,       // ZetPDF.Viewer       – renderer-viewer
    Pages,        // ZetPDF.Pages        – pages-engine
    Annotations,  // ZetPDF.Annotations  – annotations-forms
    Editing,      // ZetPDF.Editing      – edit-convert-ocr
    Ocr,          // ZetPDF.Ocr          – edit-convert-ocr
    Security,     // ZetPDF.Security     – security-signatures
}

public enum CommandKind
{
    /// <summary>Zwykły przycisk.</summary>
    Button,
    /// <summary>Przełącznik (narzędzie, tryb) – obsługa powinna implementować IToggleCommand.</summary>
    Toggle,
    /// <summary>Przycisk z menu: dzieci z RibbonCatalog.Menus albo dynamiczne (ICommandRegistry.SetMenuItems).
    /// Kliknięcie części głównej wykonuje obsługę polecenia (jeśli jest), strzałka otwiera menu.</summary>
    Menu,
}

/// <summary>
/// Opis polecenia. Teksty NIE są tu zapisane: etykieta = klucz <see cref="TextKey"/> (domyślnie "Cmd.{Id}"),
/// podpowiedź = "{TextKey}.Tip" (jeśli istnieje).
/// Shortcut – notacja WPF KeyGesture ("Ctrl+Shift+I"), kilka rozdzielonych ';'.
/// SingleKey – klawisz jednoliterowy (aktywny tylko przy włączonej opcji w Ustawieniach i poza polami tekstowymi).
/// IconGlyph – znak z czcionki Segoe Fluent Icons / Segoe MDL2 Assets (powłoka może nadpisać własną mapą ikon).
/// </summary>
public sealed record CommandDescriptor(
    string Id,
    FeatureModuleId Module,
    CommandKind Kind,
    string IconGlyph,
    string Shortcut,
    string SingleKey,
    string? CustomTextKey = null)
{
    public string TextKey => CustomTextKey ?? "Cmd." + Id;
    public string TooltipKey => TextKey + ".Tip";

    public IReadOnlyList<string> Shortcuts =>
        string.IsNullOrWhiteSpace(Shortcut) ? Array.Empty<string>() : Shortcut.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record RibbonGroupDescriptor(string Id, IReadOnlyList<string> CommandIds)
{
    public string TextKey => "Ribbon.Group." + Id;
}

public sealed record RibbonTabDescriptor(string Id, bool IsContextual, IReadOnlyList<RibbonGroupDescriptor> Groups)
{
    public string TextKey => "Ribbon.Tab." + Id;
}

/// <summary>Stały katalog poleceń i układ wstążki (generowany z tools/catalog/catalog.py).</summary>
public static partial class RibbonCatalog
{
    private static readonly Lazy<IReadOnlyList<CommandDescriptor>> _commands = new(() => CreateCommands());
    private static readonly Lazy<IReadOnlyDictionary<string, CommandDescriptor>> _byId =
        new(() => _commands.Value.ToDictionary(c => c.Id, StringComparer.Ordinal));
    private static readonly Lazy<IReadOnlyList<RibbonTabDescriptor>> _tabs = new(() => CreateTabs());
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> _menus = new(() => CreateMenus());

    public static IReadOnlyList<CommandDescriptor> Commands => _commands.Value;
    public static IReadOnlyList<RibbonTabDescriptor> Tabs => _tabs.Value;

    /// <summary>Stałe menu: id polecenia typu Menu → id poleceń-dzieci.</summary>
    public static IReadOnlyDictionary<string, string[]> Menus => _menus.Value;

    /// <summary>Kolejność poleceń w menu Plik (backstage); ostatnie pliki dokłada powłoka.</summary>
    public static IReadOnlyList<string> Backstage { get; } = CreateBackstage();

    /// <summary>Domyślny pasek szybkiego dostępu.</summary>
    public static IReadOnlyList<string> QuickAccess { get; } = CreateQuickAccess();

    public static string FileTabTextKey => "Ribbon.Tab.File";

    public static CommandDescriptor? Find(string id) => _byId.Value.TryGetValue(id, out var d) ? d : null;
}

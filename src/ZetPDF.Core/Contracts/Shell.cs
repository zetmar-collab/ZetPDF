using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;

namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Integracja modułów z powłoką, usługi interfejsu, lokalizacja, motywy, ustawienia
// =====================================================================================

/// <summary>
/// Moduł funkcjonalny. Każdy projekt modułu ma DOKŁADNIE jedną klasę implementującą ten interfejs
/// (z konstruktorem bezparametrowym). Lista modułów jest wpisana raz w ZetPDF.App (ModuleCatalog) –
/// agenci modułów jej nie edytują.
/// Kolejność: ConfigureServices wszystkich modułów → budowa kontenera DI → Initialize wszystkich modułów.
/// </summary>
public interface IFeatureModule
{
    FeatureModuleId Id { get; }

    /// <summary>Rejestracja usług modułu w kontenerze (implementacje kontraktów, własne klasy).</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Rejestracja obsługi poleceń wstążki (<see cref="ICommandRegistry.Register"/>), paneli, zasobów tekstów.
    /// Wołane na wątku UI po zbudowaniu kontenera.
    /// </summary>
    void Initialize(IServiceProvider services);
}

/// <summary>
/// Rejestr poleceń. Układ wstążki jest stały (<see cref="RibbonCatalog"/>); moduł podpina do id z katalogu
/// swoje <see cref="System.Windows.Input.ICommand"/>. Polecenie bez obsługi jest WYSZARZONE (nie ukryte).
/// Implementacja: Core (<see cref="CommandRegistry"/>).
/// </summary>
public interface ICommandRegistry
{
    /// <summary>Wszystkie deskryptory (katalog + dodatkowe zarejestrowane przez moduły).</summary>
    IReadOnlyList<CommandDescriptor> Descriptors { get; }

    /// <summary>Podpina obsługę polecenia z katalogu. Wyjątek, gdy id nie istnieje lub już ma obsługę.</summary>
    void Register(string commandId, System.Windows.Input.ICommand handler);

    /// <summary>
    /// Dodatkowe polecenie spoza katalogu (np. opcja w menu). Tekst: klucz <see cref="CommandDescriptor.TextKey"/>
    /// w zasobach modułu. Umieszczane na końcu wskazanej grupy.
    /// </summary>
    void RegisterExtra(CommandDescriptor descriptor, string tabId, string groupId, System.Windows.Input.ICommand handler);

    /// <summary>Pozycje dynamicznego menu (np. lista stempli w Comment.Stamp, kształty w Edit.AddShape).</summary>
    void SetMenuItems(string menuCommandId, IReadOnlyList<MenuItemDescriptor> items);
    IReadOnlyList<MenuItemDescriptor> GetMenuItems(string menuCommandId);

    System.Windows.Input.ICommand? GetHandler(string commandId);
    CommandDescriptor? GetDescriptor(string commandId);

    /// <summary>Wykonuje polecenie, jeśli ma obsługę i CanExecute. Zwraca, czy wykonano.</summary>
    bool TryExecute(string commandId, object? parameter = null);

    /// <summary>Wymusza ponowne CanExecute wszystkich poleceń (np. po zmianie aktywnego dokumentu).</summary>
    void InvalidateAll();

    /// <summary>Zmiana zbioru obsług/menu (powłoka odświeża wstążkę).</summary>
    event EventHandler? Changed;
}

/// <summary>Polecenie przełącznik (narzędzie, tryb widoku) – powłoka wiąże IsChecked.</summary>
public interface IToggleCommand : System.Windows.Input.ICommand, INotifyPropertyChanged
{
    bool IsChecked { get; }
}

/// <summary>Pozycja menu dynamicznego. Text już przetłumaczony LUB TextKey do przetłumaczenia.</summary>
public sealed record MenuItemDescriptor(string Id, string? TextKey, string? Text, System.Windows.Input.ICommand Command, object? Parameter = null, string? IconGlyph = null);

/// <summary>Rejestr paneli bocznych (Miniatury, Zakładki, Komentarze, Załączniki, Podpisy, …).</summary>
public interface IPanelRegistry
{
    void Register(PanelDescriptor panel);
    IReadOnlyList<PanelDescriptor> Panels { get; }
    event EventHandler? Changed;
}

/// <summary>
/// Panel boczny. Factory tworzy zawartość raz (panel sam śledzi IDocumentManager.ActiveDocument).
/// Standardowe id: PanelIds.*.
/// </summary>
public sealed record PanelDescriptor(string Id, string TitleKey, string IconGlyph, int Order, Func<IServiceProvider, FrameworkElement> Factory);

public static class PanelIds
{
    public const string Thumbnails = "Panel.Thumbnails";   // renderer-viewer
    public const string Bookmarks = "Panel.Bookmarks";     // renderer-viewer
    public const string Comments = "Panel.Comments";       // annotations-forms
    public const string Attachments = "Panel.Attachments"; // annotations-forms
    public const string Signatures = "Panel.Signatures";   // security-signatures
}

/// <summary>Kontekstowe karty wstążki.</summary>
public static class ContextualTabIds
{
    public const string FillSign = "FillSign";
    public const string CompareResults = "CompareResults";
}

/// <summary>Usługi powłoki dla modułów. Implementacja: ZetPDF.App (ui-shell).</summary>
public interface IShellService
{
    Window MainWindow { get; }

    bool IsReadingMode { get; set; }
    bool IsFullScreen { get; set; }
    bool IsSidePanelVisible { get; set; }
    bool IsRibbonMinimized { get; set; }
    event EventHandler? ShellStateChanged;

    void ShowPanel(string panelId);
    void SetContextualTabVisible(string tabId, bool visible);

    /// <summary>Komunikat na pasku stanu (już przetłumaczony); null czyści.</summary>
    void SetStatus(string? text);
}

public enum MessageSeverity { Info, Warning, Error }

public enum YesNoCancel { Yes, No, Cancel }

/// <summary>Filtr pliku: Description (przetłumaczony) i wzorce, np. ("PDF", "*.pdf").</summary>
public sealed record FileFilter(string Description, params string[] Patterns)
{
    public static FileFilter Pdf(ILocalizationService loc) => new(loc["Core.FileFilter.Pdf"], "*.pdf");
    public static FileFilter All(ILocalizationService loc) => new(loc["Core.FileFilter.All"], "*.*");
}

/// <summary>
/// Okna dialogowe. Teksty przekazujemy JUŻ PRZETŁUMACZONE (moduł woła ILocalizationService).
/// Implementacja: ZetPDF.App (ui-shell). Wołać z wątku UI.
/// </summary>
public interface IDialogService
{
    IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null);
    string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null);
    string? SelectFolder(string title, string? initialDirectory = null);

    void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info);
    bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning);
    YesNoCancel AskYesNoCancel(string text, string? title = null);

    /// <summary>Okno hasła. isRetry = poprzednie było złe. Zwraca null przy anulowaniu.</summary>
    string? AskPassword(string documentName, bool isRetry);

    /// <summary>Okno postępu modalne; praca w tle; zwraca false, gdy anulowano.</summary>
    Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true);

    /// <summary>Pokazuje okno modułu (ustawia Owner, styl motywu). Zwraca DialogResult.</summary>
    bool? ShowDialog(Window dialog);

    /// <summary>Pokazuje błąd z wyjątku (przetłumaczony opis + zapis do dziennika).</summary>
    void ShowError(Exception exception, string? contextText = null);
}

/// <summary>
/// Lokalizacja z przełączaniem na żywo. Indeksator zwraca tekst dla bieżącej kultury; po zmianie kultury
/// podnosi PropertyChanged("Item[]"), więc wiązania XAML ({loc:Loc Klucz}) odświeżają się same.
/// Brakujący klucz zwraca "[Klucz]" i zapisuje ostrzeżenie w dzienniku. Implementacja: Core.
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    string this[string key] { get; }

    /// <summary>Tekst sformatowany (string.Format w bieżącej kulturze).</summary>
    string Format(string key, params object?[] args);

    CultureInfo CurrentCulture { get; }

    /// <summary>Obsługiwane: "pl", "en".</summary>
    IReadOnlyList<CultureInfo> SupportedCultures { get; }

    void SetCulture(CultureInfo culture);
    event EventHandler? CultureChanged;

    /// <summary>Dodaje zasoby modułu (np. new ResourceManager("ZetPDF.Pages.Resources.Strings", asm)).</summary>
    void RegisterResources(ResourceManager resources);

    bool HasKey(string key);

    /// <summary>Wszystkie klucze ze wszystkich zarejestrowanych zasobów (testy, wyszukiwanie poleceń).</summary>
    IReadOnlyCollection<string> AllKeys { get; }
}

public enum AppTheme { System, Light, Dark }

/// <summary>Motywy. Implementacja: ZetPDF.App (ui-shell). Klucze pędzli: <see cref="Theming.ThemeKeys"/>.</summary>
public interface IThemeService
{
    AppTheme Theme { get; }
    /// <summary>Faktycznie użyty (System rozwinięty do Light/Dark).</summary>
    AppTheme EffectiveTheme { get; }
    void SetTheme(AppTheme theme);
    event EventHandler? ThemeChanged;
}

/// <summary>Ustawienia aplikacji (%APPDATA%\ZetPDF\settings.json). Implementacja: Core.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Zmienia i zapisuje ustawienia (atomowo), podnosi Changed.</summary>
    void Update(Action<AppSettings> change);

    /// <summary>Ustawienia modułu (osobna sekcja JSON pod kluczem, np. "Annotations").</summary>
    T GetModuleSettings<T>(string key) where T : class, new();
    void SetModuleSettings<T>(string key, T value) where T : class, new();

    event EventHandler? Changed;
}

/// <summary>Ustawienia ogólne. Nowe pola ogólne – przez koordynatora; moduły używają GetModuleSettings.</summary>
public sealed class AppSettings
{
    /// <summary>"pl", "en" lub null = język systemu (pl dla pl-*, inaczej en).</summary>
    public string? Language { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string AuthorName { get; set; } = Environment.UserName;
    public string Organization { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public ZoomMode DefaultZoomMode { get; set; } = ZoomMode.FitWidth;
    public double DefaultZoom { get; set; } = 1.0;
    public PageLayoutMode DefaultLayout { get; set; } = PageLayoutMode.Continuous;
    public bool SingleKeyShortcuts { get; set; }
    /// <summary>Adres serwera TSA (RFC 3161) – domyślnie pusty = funkcja wyłączona.</summary>
    public string TsaUrl { get; set; } = string.Empty;
    /// <summary>Folder plików tymczasowych; pusty = %TEMP%\ZetPDF.</summary>
    public string TempFolder { get; set; } = string.Empty;
    public int UndoLevels { get; set; } = 30;
    public int MaxRecentFiles { get; set; } = 15;
    public List<string> RecentFiles { get; set; } = new();
    public bool SidePanelVisible { get; set; } = true;
    public string? LastSidePanel { get; set; }
    public bool RibbonMinimized { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    /// <summary>Polecenia dodane przez użytkownika do paska szybkiego dostępu (id).</summary>
    public List<string> QuickAccessExtra { get; set; } = new();
    /// <summary>Próg pewności OCR (0–100) poniżej którego słowo jest "podejrzane".</summary>
    public int OcrSuspectThreshold { get; set; } = 70;
}

/// <summary>Ostatnie pliki. Implementacja: Core (na bazie ISettingsService).</summary>
public interface IRecentFilesService
{
    IReadOnlyList<string> Files { get; }
    void Add(string path);
    void Remove(string path);
    void Clear();
    event EventHandler? Changed;
}

/// <summary>Ścieżki aplikacji. Implementacja: Core. Zmienna środowiskowa ZETPDF_HOME przekierowuje wszystko (testy).</summary>
public interface IAppPaths
{
    /// <summary>%APPDATA%\ZetPDF (ustawienia, podpisy, stemple, działania).</summary>
    string AppDataDirectory { get; }
    /// <summary>%LOCALAPPDATA%\ZetPDF (dzienniki, natywne biblioteki OCR, cache).</summary>
    string LocalDataDirectory { get; }
    string LogDirectory { get; }
    /// <summary>Folder tymczasowy (ustawienie lub %TEMP%\ZetPDF\&lt;pid&gt;), czyszczony przy zamknięciu.</summary>
    string TempDirectory { get; }
    string SettingsFile { get; }
    /// <summary>Podpisy odręczne (annotations-forms).</summary>
    string SignaturesDirectory { get; }
    /// <summary>Własne stemple (annotations-forms).</summary>
    string StampsDirectory { get; }
    /// <summary>Działania Kreatora (security-signatures).</summary>
    string ActionsDirectory { get; }
    /// <summary>Identyfikatory cyfrowe utworzone w ZetPDF (security-signatures).</summary>
    string DigitalIdsDirectory { get; }
}

/// <summary>Bezpieczny zapis plików. Implementacja: Core.</summary>
public interface IFileService
{
    /// <summary>
    /// Zapis przez plik tymczasowy w tym samym folderze, potem File.Replace (z kopią .bak, usuwaną po sukcesie,
    /// chyba że keepBackup) lub File.Move, gdy plik nie istniał. Nigdy nie nadpisuje oryginału bezpośrednio.
    /// </summary>
    Task WriteAllBytesSafeAsync(string path, byte[] data, bool keepBackup = false, CancellationToken ct = default);

    /// <summary>Unikalna ścieżka pliku tymczasowego w IAppPaths.TempDirectory.</summary>
    string GetTempFilePath(string extension = ".tmp");

    /// <summary>Zastępuje znaki niedozwolone w nazwie pliku.</summary>
    string SanitizeFileName(string name);
}

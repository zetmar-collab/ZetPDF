using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.App.Dialogs;

/// <summary>
/// Model okna Ustawień. Język i motyw stosowane NA ŻYWO przy wyborze (podgląd), Anuluj przywraca poprzednie.
/// Pozostałe pola zapisywane przy OK/Zastosuj do %APPDATA%\ZetPDF\settings.json (ISettingsService).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public const int MinUndo = 1, MaxUndo = 500, MinRecent = 1, MaxRecent = 50;

    private readonly ISettingsService _settings;
    private readonly ILocalizationService _loc;
    private readonly IThemeService _theme;
    private readonly IDocumentManager? _documents;
    private string _originalLanguage;
    private AppTheme _originalTheme;

    private string _language;
    private AppTheme _themeValue;
    private string _authorName;
    private string _organization;
    private string _email;
    private ZoomMode _zoomMode;
    private string _zoomPercent;
    private PageLayoutMode _layout;
    private bool _singleKey;
    private string _tsaUrl;
    private string _tempFolder;
    private string _undoLevels;
    private string _maxRecent;

    public SettingsViewModel(ISettingsService settings, ILocalizationService loc, IThemeService theme, IDocumentManager? documents = null)
    {
        _settings = settings;
        _loc = loc;
        _theme = theme;
        _documents = documents;
        var s = settings.Current;
        _originalLanguage = _language = s.Language ?? string.Empty;
        _originalTheme = _themeValue = s.Theme;
        _authorName = s.AuthorName;
        _organization = s.Organization;
        _email = s.Email;
        _zoomMode = s.DefaultZoomMode;
        _zoomPercent = (s.DefaultZoom * 100).ToString("0", CultureInfo.CurrentCulture);
        _layout = s.DefaultLayout;
        _singleKey = s.SingleKeyShortcuts;
        _tsaUrl = s.TsaUrl;
        _tempFolder = s.TempFolder;
        _undoLevels = s.UndoLevels.ToString(CultureInfo.CurrentCulture);
        _maxRecent = s.MaxRecentFiles.ToString(CultureInfo.CurrentCulture);
        _loc.CultureChanged += OnCultureChanged;
    }

    /// <summary>"" = język systemu, "pl", "en". Zmiana stosowana natychmiast.</summary>
    public string Language
    {
        get => _language;
        set
        {
            if (!SetProperty(ref _language, value ?? string.Empty)) return;
            _loc.SetCulture(LocalizationService.ResolveCulture(string.IsNullOrEmpty(_language) ? null : _language));
        }
    }

    /// <summary>Motyw – zmiana stosowana natychmiast.</summary>
    public AppTheme Theme
    {
        get => _themeValue;
        set
        {
            if (!SetProperty(ref _themeValue, value)) return;
            _theme.SetTheme(value);
        }
    }

    public string AuthorName { get => _authorName; set => SetAndValidate(ref _authorName, value); }
    public string Organization { get => _organization; set => SetAndValidate(ref _organization, value); }
    public string Email { get => _email; set => SetAndValidate(ref _email, value); }

    public ZoomMode ZoomMode
    {
        get => _zoomMode;
        set
        {
            if (SetAndValidate(ref _zoomMode, value)) OnPropertyChanged(nameof(IsCustomZoom));
        }
    }

    public bool IsCustomZoom => ZoomMode == ZoomMode.Custom;
    public string ZoomPercent { get => _zoomPercent; set => SetAndValidate(ref _zoomPercent, value); }
    public PageLayoutMode Layout { get => _layout; set => SetAndValidate(ref _layout, value); }
    public bool SingleKeyShortcuts { get => _singleKey; set => SetAndValidate(ref _singleKey, value); }
    public string TsaUrl { get => _tsaUrl; set => SetAndValidate(ref _tsaUrl, value); }
    public string TempFolder { get => _tempFolder; set => SetAndValidate(ref _tempFolder, value); }
    public string UndoLevels { get => _undoLevels; set => SetAndValidate(ref _undoLevels, value); }
    public string MaxRecentFiles { get => _maxRecent; set => SetAndValidate(ref _maxRecent, value); }

    // ---------------------------------------------------------------- walidacja (komunikaty w bieżącym języku)

    public string? EmailError => ValidateEmail(Email) ? null : _loc["Shell.Settings.Error.Email"];
    public string? TsaError => ValidateTsa(TsaUrl) ? null : _loc["Shell.Settings.Error.Tsa"];
    public string? TempFolderError => ValidateFolder(TempFolder) ? null : _loc["Shell.Settings.Error.Folder"];
    public string? UndoError => TryInt(UndoLevels, MinUndo, MaxUndo, out _) ? null : _loc.Format("Shell.Settings.Error.Range", MinUndo, MaxUndo);
    public string? RecentError => TryInt(MaxRecentFiles, MinRecent, MaxRecent, out _) ? null : _loc.Format("Shell.Settings.Error.Range", MinRecent, MaxRecent);
    public string? ZoomError => !IsCustomZoom || TryZoom(ZoomPercent, out _) ? null : _loc.Format("Shell.Settings.Error.Range", 10, 6400);

    public bool IsValid => EmailError is null && TsaError is null && TempFolderError is null && UndoError is null && RecentError is null && ZoomError is null;

    public static bool ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return true;
        var e = email.Trim();
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.EndsWith('.') && !e.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>Serwer TSA: pusty (wyłączony) albo bezwzględny adres http/https.</summary>
    public static bool ValidateTsa(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);
    }

    public static bool ValidateFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            return System.IO.Path.IsPathFullyQualified(path.Trim()) && path.Trim().IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryInt(string? text, int min, int max, out int value) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= min && value <= max;

    public static bool TryZoom(string? text, out double zoom)
    {
        zoom = 0;
        var t = (text ?? string.Empty).Replace("%", string.Empty, StringComparison.Ordinal).Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p < 10 || p > 6400) return false;
        zoom = p / 100.0;
        return true;
    }

    // ---------------------------------------------------------------- zapis / anulowanie

    /// <summary>Zapisuje wszystkie pola. Zwraca false, gdy dane są niepoprawne.</summary>
    public bool Apply()
    {
        if (!IsValid) return false;
        TryInt(UndoLevels, MinUndo, MaxUndo, out var undo);
        TryInt(MaxRecentFiles, MinRecent, MaxRecent, out var recent);
        var zoom = IsCustomZoom && TryZoom(ZoomPercent, out var z) ? z : _settings.Current.DefaultZoom;
        _settings.Update(s =>
        {
            s.Language = string.IsNullOrEmpty(Language) ? null : Language;
            s.Theme = Theme;
            s.AuthorName = AuthorName.Trim();
            s.Organization = Organization.Trim();
            s.Email = Email.Trim();
            s.DefaultZoomMode = ZoomMode;
            s.DefaultZoom = zoom;
            s.DefaultLayout = Layout;
            s.SingleKeyShortcuts = SingleKeyShortcuts;
            s.TsaUrl = TsaUrl.Trim();
            s.TempFolder = TempFolder.Trim();
            s.UndoLevels = undo;
            s.MaxRecentFiles = recent;
            if (s.RecentFiles.Count > recent) s.RecentFiles.RemoveRange(recent, s.RecentFiles.Count - recent);
        });
        if (_documents is not null)
            foreach (var d in _documents.Documents) d.History.Limit = undo;
        _originalLanguage = Language;
        _originalTheme = Theme;
        return true;
    }

    /// <summary>Anuluj: przywraca język i motyw sprzed otwarcia okna (zmienione na żywo).</summary>
    public void Revert()
    {
        if (_language != _originalLanguage)
        {
            _language = _originalLanguage;
            _loc.SetCulture(LocalizationService.ResolveCulture(string.IsNullOrEmpty(_originalLanguage) ? null : _originalLanguage));
            if ((_settings.Current.Language ?? string.Empty) != _originalLanguage)
                _settings.Update(s => s.Language = string.IsNullOrEmpty(_originalLanguage) ? null : _originalLanguage);
        }
        if (_themeValue != _originalTheme)
        {
            _themeValue = _originalTheme;
            _theme.SetTheme(_originalTheme);
        }
    }

    /// <summary>Odłącza model od zdarzeń lokalizacji (przy zamknięciu okna).</summary>
    public void Detach() => _loc.CultureChanged -= OnCultureChanged;

    private void OnCultureChanged(object? sender, EventArgs e) => RaiseValidation();

    private bool SetAndValidate<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return false;
        RaiseValidation();
        return true;
    }

    private void RaiseValidation()
    {
        OnPropertyChanged(nameof(EmailError));
        OnPropertyChanged(nameof(TsaError));
        OnPropertyChanged(nameof(TempFolderError));
        OnPropertyChanged(nameof(UndoError));
        OnPropertyChanged(nameof(RecentError));
        OnPropertyChanged(nameof(ZoomError));
        OnPropertyChanged(nameof(IsValid));
    }
}

using System.Windows;
using System.Windows.Controls;
using ControlzEx.Theming;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Services;

/// <summary>
/// Motywy: podmienia słownik Themes/Light|Dark.xaml i motyw Fluent.Ribbon (ThemeManager – zweryfikowane
/// przełączanie na żywo w scratch/RibbonCheck). "Jak w systemie" – rejestr AppsUseLightTheme + nasłuch zmian.
/// Szkielet; rozbudowa: ui-shell.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;
    private ResourceDictionary? _current;

    public ThemeService(ISettingsService settings)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Theme == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply());
        };
    }

    public AppTheme Theme { get; private set; } = AppTheme.System;
    public AppTheme EffectiveTheme { get; private set; } = AppTheme.Light;
    public event EventHandler? ThemeChanged;

    public void SetTheme(AppTheme theme)
    {
        Theme = theme;
        if (_settings.Current.Theme != theme) _settings.Update(s => s.Theme = theme);
        Apply();
    }

    public static bool IsSystemLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    private void Apply()
    {
        var app = Application.Current;
        if (app is null) return;
        EffectiveTheme = Theme == AppTheme.System ? (IsSystemLight() ? AppTheme.Light : AppTheme.Dark) : Theme;
        var uri = new Uri($"pack://application:,,,/ZetPDF;component/Themes/{(EffectiveTheme == AppTheme.Dark ? "Dark" : "Light")}.xaml");
        var dict = new ResourceDictionary { Source = uri };
        var merged = app.Resources.MergedDictionaries;
        var old = _current ?? merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true
                                                       || d.Source?.OriginalString.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        int index = old is null ? merged.Count : merged.IndexOf(old);
        if (old is not null) merged.Remove(old);
        merged.Insert(Math.Min(index, merged.Count), dict);
        _current = dict;
        ThemeManager.Current.ChangeTheme(app, EffectiveTheme == AppTheme.Dark ? "Dark.Blue" : "Light.Blue");
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Okna dialogowe – wersja szkieletowa (ui-shell zastąpi MessageBox własnymi oknami w motywie).</summary>
public sealed class DialogService : IDialogService
{
    private readonly ILocalizationService _loc;
    private readonly ILogger<DialogService> _log;

    public DialogService(ILocalizationService loc, ILogger<DialogService> log)
    {
        _loc = loc;
        _log = log;
    }

    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    private static string ToFilter(IReadOnlyList<FileFilter> filters) =>
        string.Join("|", filters.Select(f => $"{f.Description}|{string.Join(";", f.Patterns)}"));

    public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null)
    {
        var d = new OpenFileDialog { Title = title, Filter = ToFilter(filters), Multiselect = multiselect, InitialDirectory = initialDirectory ?? string.Empty };
        return d.ShowDialog(Owner) == true ? d.FileNames : null;
    }

    public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null)
    {
        var d = new SaveFileDialog { Title = title, Filter = ToFilter(filters), FileName = defaultFileName ?? string.Empty, InitialDirectory = initialDirectory ?? string.Empty, OverwritePrompt = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? SelectFolder(string title, string? initialDirectory = null)
    {
        var d = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory ?? string.Empty };
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) =>
        MessageBox.Show(Owner!, text, title ?? _loc["Shell.AppTitle"], MessageBoxButton.OK,
            severity switch { MessageSeverity.Error => MessageBoxImage.Error, MessageSeverity.Warning => MessageBoxImage.Warning, _ => MessageBoxImage.Information });

    public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) =>
        MessageBox.Show(Owner!, text, title ?? _loc["Shell.AppTitle"], MessageBoxButton.YesNo,
            severity == MessageSeverity.Error ? MessageBoxImage.Error : MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public YesNoCancel AskYesNoCancel(string text, string? title = null) =>
        MessageBox.Show(Owner!, text, title ?? _loc["Shell.AppTitle"], MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => YesNoCancel.Yes,
            MessageBoxResult.No => YesNoCancel.No,
            _ => YesNoCancel.Cancel,
        };

    public string? AskPassword(string documentName, bool isRetry)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 280 };
        var ok = new Button { Content = _loc["Core.Button.Ok"], IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = _loc["Core.Button.Cancel"], IsCancel = true, MinWidth = 80 };
        var prompt = new TextBlock
        {
            Text = isRetry ? _loc["Core.Password.Retry"] : _loc.Format("Core.Password.Prompt", documentName),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var window = new Window
        {
            Title = _loc["Core.Password.Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = Owner,
            ShowInTaskbar = Owner is null,
            Content = new StackPanel { Margin = new Thickness(16), Children = { prompt, box, buttons } },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }

    public async Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true)
    {
        // Szkielet: bez okna postępu (ui-shell doda okno z paskiem i przyciskiem Anuluj).
        using var cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => work(new Progress<OperationProgress>(), cts.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public bool? ShowDialog(Window dialog)
    {
        dialog.Owner ??= Owner;
        return dialog.ShowDialog();
    }

    public void ShowError(Exception exception, string? contextText = null)
    {
        _log.LogError(exception, "Error shown to user: {Context}", contextText);
        var text = exception switch
        {
            PdfCorruptException => _loc.Format("Core.Error.Corrupt", contextText ?? string.Empty),
            PdfInvalidPasswordException => _loc["Core.Error.InvalidPassword"],
            PdfPasswordRequiredException => _loc["Core.Error.PasswordRequired"],
            DocumentPermissionException => _loc["Core.Error.PermissionDenied"],
            NotImplementedException => _loc["Core.Error.NotImplemented"],
            IOException io => _loc.Format("Core.Error.FileAccess", contextText ?? string.Empty, io.Message),
            UnauthorizedAccessException ua => _loc.Format("Core.Error.FileAccess", contextText ?? string.Empty, ua.Message),
            _ => _loc.Format("Core.Error.Generic", exception.Message),
        };
        ShowMessage(text, _loc["Core.Error.Title"], MessageSeverity.Error);
    }
}

/// <summary>Usługi powłoki – szkielet (ui-shell).</summary>
public sealed class ShellService : IShellService
{
    public Window MainWindow => Application.Current.MainWindow;
    public bool IsReadingMode { get; set; }
    public bool IsFullScreen { get; set; }
    public bool IsSidePanelVisible { get; set; } = true;
    public bool IsRibbonMinimized { get; set; }
    public event EventHandler? ShellStateChanged { add { } remove { } }
    public event EventHandler<string?>? StatusRequested;
    public event EventHandler<(string TabId, bool Visible)>? ContextualTabRequested;
    public void ShowPanel(string panelId) { }
    public void SetContextualTabVisible(string tabId, bool visible) => ContextualTabRequested?.Invoke(this, (tabId, visible));
    public void SetStatus(string? text) => StatusRequested?.Invoke(this, text);
}

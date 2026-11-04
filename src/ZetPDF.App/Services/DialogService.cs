using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZetPDF.App.Dialogs;
using ZetPDF.App.Shell;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.App.Services;

/// <summary>
/// Okna dialogowe w motywie ZetPDF. Komunikaty – własne okno <see cref="MessageWindow"/> (systemowy MessageBox
/// nie obsługuje motywu ciemnego). Okna wyboru plików/folderów – systemowe (język i motyw wg Windows).
/// </summary>
public sealed class DialogService : IDialogService, IOwnerProtectionPrompts
{
    private readonly ILocalizationService _loc;
    private readonly IAppPaths _paths;
    private readonly ILogger<DialogService> _log;

    public DialogService(ILocalizationService loc, IAppPaths paths, ILogger<DialogService> log)
    {
        _loc = loc;
        _paths = paths;
        _log = log;
    }

    /// <summary>Okno-właściciel: aktywne okno aplikacji (np. otwarte Ustawienia) lub okno główne.</summary>
    internal static Window? Owner
    {
        get
        {
            var app = Application.Current;
            if (app is null) return null;
            var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
            if (active is not null) return active;
            return app.MainWindow is { IsLoaded: true, IsVisible: true } m ? m : null;
        }
    }

    private static string ToFilter(IReadOnlyList<FileFilter> filters) =>
        string.Join("|", filters.Select(f => $"{f.Description} ({string.Join(";", f.Patterns)})|{string.Join(";", f.Patterns)}"));

    public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null)
    {
        var d = new OpenFileDialog { Title = title, Filter = ToFilter(filters), Multiselect = multiselect, InitialDirectory = initialDirectory ?? string.Empty };
        return d.ShowDialog(Owner) == true ? d.FileNames : null;
    }

    public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null)
    {
        var d = new SaveFileDialog { Title = title, Filter = ToFilter(filters), FileName = defaultFileName ?? string.Empty, InitialDirectory = initialDirectory ?? string.Empty, OverwritePrompt = true, AddExtension = true };
        if (filters.Count > 0 && filters[0].Patterns.Length > 0 && filters[0].Patterns[0].StartsWith("*.", StringComparison.Ordinal) && filters[0].Patterns[0] != "*.*")
            d.DefaultExt = filters[0].Patterns[0][1..];
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? SelectFolder(string title, string? initialDirectory = null)
    {
        var d = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory ?? string.Empty };
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) =>
        Show(title ?? _loc["Shell.AppTitle"], text, severity, false,
            new[] { new MessageWindow.ButtonSpec(_loc["Core.Button.Ok"], 1, IsDefault: true, IsCancel: true) }, 1);

    public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) =>
        Show(title ?? _loc["Shell.AppTitle"], text, severity, severity == MessageSeverity.Info,
            new[]
            {
                new MessageWindow.ButtonSpec(_loc["Core.Button.Yes"], 1, IsDefault: true),
                new MessageWindow.ButtonSpec(_loc["Core.Button.No"], 0, IsCancel: true),
            }, 0) == 1;

    public YesNoCancel AskYesNoCancel(string text, string? title = null) =>
        (YesNoCancel)Show(title ?? _loc["Shell.AppTitle"], text, null, true,
            new[]
            {
                new MessageWindow.ButtonSpec(_loc["Core.Button.Yes"], (int)YesNoCancel.Yes, IsDefault: true),
                new MessageWindow.ButtonSpec(_loc["Core.Button.No"], (int)YesNoCancel.No),
                new MessageWindow.ButtonSpec(_loc["Core.Button.Cancel"], (int)YesNoCancel.Cancel, IsCancel: true),
            }, (int)YesNoCancel.Cancel);

    /// <summary>Pytanie o zapis zmian przed zamknięciem: Zapisz / Nie zapisuj / Anuluj.</summary>
    public YesNoCancel AskSaveChanges(string documentName) =>
        (YesNoCancel)Show(_loc["Shell.AppTitle"], _loc.Format("Shell.Close.SavePrompt", documentName), null, true,
            new[]
            {
                new MessageWindow.ButtonSpec(_loc["Core.Button.Save"], (int)YesNoCancel.Yes, IsDefault: true),
                new MessageWindow.ButtonSpec(_loc["Shell.Close.DontSave"], (int)YesNoCancel.No),
                new MessageWindow.ButtonSpec(_loc["Core.Button.Cancel"], (int)YesNoCancel.Cancel, IsCancel: true),
            }, (int)YesNoCancel.Cancel);

    /// <summary>Zapis zmian w podpisanym pliku: Yes = zapisz kopię, No = nadpisz, Cancel = anuluj.</summary>
    public YesNoCancel AskSaveSigned(string documentName) =>
        (YesNoCancel)Show(_loc["Shell.AppTitle"], _loc.Format("Shell.Save.Signed", documentName), MessageSeverity.Warning, true,
            new[]
            {
                new MessageWindow.ButtonSpec(_loc["Shell.Save.SignedCopy"], (int)YesNoCancel.Yes, IsDefault: true),
                new MessageWindow.ButtonSpec(_loc["Shell.Save.SignedOverwrite"], (int)YesNoCancel.No),
                new MessageWindow.ButtonSpec(_loc["Core.Button.Cancel"], (int)YesNoCancel.Cancel, IsCancel: true),
            }, (int)YesNoCancel.Cancel);

    private static int Show(string title, string text, MessageSeverity? severity, bool question, IReadOnlyList<MessageWindow.ButtonSpec> buttons, int cancelResult, string? details = null, string? detailsHeader = null)
    {
        var owner = Owner;
        var w = new MessageWindow(title, text, severity, question, buttons, cancelResult, details, detailsHeader)
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        WindowThemer.Prepare(w);
        w.ShowDialog();
        return w.Result;
    }

    public string? AskPassword(string documentName, bool isRetry) =>
        AskSecret(_loc["Core.Password.Title"], documentName,
            isRetry ? _loc["Core.Password.Retry"] : _loc.Format("Core.Password.Prompt", documentName), isRetry, confirm: false);

    // ------------------------------------------------------------------ D-23 (IOwnerProtectionPrompts)

    public OwnerProtectionChoice AskOwnerProtectionChoice(string documentName, bool canEnterOwnerPassword)
    {
        var buttons = new List<MessageWindow.ButtonSpec>();
        if (canEnterOwnerPassword)
            buttons.Add(new MessageWindow.ButtonSpec(_loc["Shell.OwnerOnly.EnterOwner"], (int)OwnerProtectionChoice.EnterOwnerPassword, IsDefault: true));
        buttons.Add(new MessageWindow.ButtonSpec(_loc["Shell.OwnerOnly.NewPassword"], (int)OwnerProtectionChoice.NewPermissionsPassword, IsDefault: !canEnterOwnerPassword));
        buttons.Add(new MessageWindow.ButtonSpec(_loc["Shell.OwnerOnly.NoProtection"], (int)OwnerProtectionChoice.NoProtection));
        buttons.Add(new MessageWindow.ButtonSpec(_loc["Core.Button.Cancel"], (int)OwnerProtectionChoice.Cancel, IsCancel: true));
        return (OwnerProtectionChoice)Show(_loc["Shell.OwnerOnly.Title"], _loc.Format("Shell.OwnerOnly.Text", documentName), MessageSeverity.Warning, false,
            buttons, (int)OwnerProtectionChoice.Cancel);
    }

    public string? AskOwnerPassword(string documentName, bool isRetry) =>
        AskSecret(_loc["Shell.OwnerOnly.EnterOwner.Title"], documentName,
            isRetry ? _loc["Shell.OwnerOnly.EnterOwner.Retry"] : _loc["Shell.OwnerOnly.EnterOwner.Prompt"], isRetry, confirm: false);

    public string? AskNewPermissionsPassword(string documentName) =>
        AskSecret(_loc["Shell.OwnerOnly.NewPassword.Title"], documentName, _loc["Shell.OwnerOnly.NewPassword.Prompt"], false, confirm: true);

    /// <summary>Nowe hasło otwarcia dokumentu odzyskanego po awarii (ochrony nie dało się odtworzyć z pliku).</summary>
    public string? AskRecoveryPassword(string documentName) =>
        AskSecret(_loc["Shell.Recovery.NewPassword.Title"], documentName, _loc["Shell.Recovery.NewPassword.Prompt"], false, confirm: true);

    /// <summary>Okno z polem hasła (opcjonalnie drugim – powtórzenie; OK aktywne dopiero przy zgodnych, niepustych hasłach).</summary>
    private string? AskSecret(string title, string documentName, string promptText, bool isError, bool confirm)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 10, 0, 0), MinWidth = 300 };
        var repeat = new PasswordBox { Margin = new Thickness(0, 4, 0, 0), MinWidth = 300 };
        var ok = new Button { Content = _loc["Core.Button.Ok"], IsDefault = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = _loc["Core.Button.Cancel"], IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        var prompt = new TextBlock { Text = promptText, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        if (isError) prompt.SetResourceReference(TextBlock.ForegroundProperty, Core.Theming.ThemeKeys.StatusError);
        var name = new TextBlock { Text = documentName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 6) };
        var content = new StackPanel { Margin = new Thickness(20, 18, 20, 18), Children = { name, prompt, box } };
        if (confirm)
        {
            content.Children.Add(new TextBlock { Text = _loc["Shell.OwnerOnly.NewPassword.Repeat"], Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            content.Children.Add(repeat);
            void Validate() => ok.IsEnabled = box.Password.Length > 0 && box.Password == repeat.Password;
            box.PasswordChanged += (_, _) => Validate();
            repeat.PasswordChanged += (_, _) => Validate();
            Validate();
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var footer = new Border { Padding = new Thickness(20, 12, 20, 12), Child = buttons };
        footer.SetResourceReference(Border.BackgroundProperty, Core.Theming.ThemeKeys.SurfaceAlt);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(content);
        var owner = Owner;
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
            Content = root,
        };
        WindowThemer.Prepare(window);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }

    public async Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true)
    {
        using var cts = new CancellationTokenSource();
        var owner = Owner;
        var window = new ProgressWindow(title, _loc["Core.Progress.Working"], cancellable ? _loc["Core.Button.Cancel"] : null, cancellable ? cts.Cancel : null)
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        WindowThemer.Prepare(window);
        // Progress<T> utworzony na wątku UI – raporty wracają na wątek UI.
        var progress = new Progress<OperationProgress>(window.Report);
        var task = Task.Run(() => work(progress, cts.Token), CancellationToken.None);

        // Krótkie operacje kończą się bez migania okna.
        if (await Task.WhenAny(task, Task.Delay(350)) != task)
        {
            _ = task.ContinueWith(_ => window.CloseFromCode(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            if (!window.IsCompleted) window.ShowDialog();
        }

        try
        {
            await task;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            // Okno niepokazane (krótka operacja) też jest oknem podrzędnym głównego – bez tego przy zamykaniu programu
            // dostawało OnClosing, anulowało zwolniony CancellationTokenSource (ObjectDisposedException, okno błędu
            // przy wyjściu, zawieszony proces) i blokowało zamknięcie.
            window.CloseFromCode();
        }
    }

    public bool? ShowDialog(Window dialog)
    {
        var owner = Owner;
        if (dialog.Owner is null && owner is not null && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
        if (dialog.WindowStartupLocation == WindowStartupLocation.Manual && double.IsNaN(dialog.Left) && double.IsNaN(dialog.Top))
            dialog.WindowStartupLocation = dialog.Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (dialog.Owner is not null) dialog.ShowInTaskbar = false;
        WindowThemer.Prepare(dialog);
        return dialog.ShowDialog();
    }

    public void ShowModeless(Window window)
    {
        // Właścicielem jest okno główne, nie aktywne: inne okno mogłoby zamknąć się wcześniej i zabrać ze sobą to niemodalne.
        var owner = Application.Current?.MainWindow is { IsLoaded: true, IsVisible: true } main && !ReferenceEquals(main, window) ? main : null;
        if (window.Owner is null && owner is not null) window.Owner = owner;
        if (window.Owner is not null) window.ShowInTaskbar = false;
        WindowThemer.Prepare(window);
        window.Show();
    }

    public void ShowError(Exception exception, string? contextText = null)
    {
        _log.LogError(exception, "Error shown to user: {Context}", contextText);
        // Tekst w języku interfejsu (O-3); angielski komunikat biblioteki/.NET tylko w rozwijanych szczegółach.
        var text = _loc.Describe(exception, out var specific, contextText);
        bool keyed = LocalizedError.TryGetMessageKey(exception, out _, out _);
        var details = keyed ? null
            : LocalizedError.TechnicalDetails(exception) + (specific ? string.Empty : Environment.NewLine + Environment.NewLine + _loc.Format("Shell.Error.LogHint", _paths.LogDirectory));
        Show(_loc["Core.Error.Title"], text, MessageSeverity.Error, false,
            new[] { new MessageWindow.ButtonSpec(_loc["Core.Button.Ok"], 1, IsDefault: true, IsCancel: true) }, 1,
            details, _loc["Shell.Error.Details"]);
    }

    /// <summary>Błąd nieobsłużony (globalny handler) – z informacją o pliku dziennika.</summary>
    internal void ShowUnhandled(Exception exception)
    {
        Show(_loc["Core.Error.Title"], _loc.Format("Shell.Error.Unhandled", _paths.LogDirectory), MessageSeverity.Error, false,
            new[] { new MessageWindow.ButtonSpec(_loc["Core.Button.Ok"], 1, IsDefault: true, IsCancel: true) }, 1,
            exception.GetType().FullName + ": " + exception.Message, _loc["Shell.Error.Details"]);
    }
}

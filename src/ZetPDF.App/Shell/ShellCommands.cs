using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.App.Dialogs;
using ZetPDF.App.Licenses;
using ZetPDF.App.Services;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Shell;

/// <summary>
/// Obsługa poleceń modułu Shell z katalogu (25 poleceń: Plik, Cofnij/Ponów, karty, panele, wstążka, Pomoc)
/// oraz polecenia zapasowe View.ReadingMode / View.FullScreen, jeśli moduł Viewer ich nie zarejestrował.
/// Wołane PO Initialize wszystkich modułów.
/// </summary>
internal static class ShellCommands
{
    public static void Register(IServiceProvider sp, Func<MainWindow> window)
    {
        var registry = sp.GetRequiredService<ICommandRegistry>();
        var documents = sp.GetRequiredService<IDocumentManager>();
        var dialogs = sp.GetRequiredService<IDialogService>();
        var loc = sp.GetRequiredService<ILocalizationService>();
        var shell = sp.GetRequiredService<ShellService>();
        var actions = sp.GetRequiredService<DocumentActions>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var paths = sp.GetRequiredService<IAppPaths>();
        var panels = sp.GetRequiredService<IPanelRegistry>();

        // ---------------------------------------------------------------- Plik
        registry.Register(CommandIds.File.Open, new AsyncCommand(dialogs, actions.OpenWithDialogAsync));
        registry.Register(CommandIds.File.Save, new ActiveDocumentCommand(documents, dialogs, d => actions.SaveAsync(d)));
        registry.Register(CommandIds.File.SaveAs, new ActiveDocumentCommand(documents, dialogs, d => actions.SaveAsAsync(d)));
        registry.Register(CommandIds.File.Revert, new ActiveDocumentCommand(documents, dialogs, actions.RevertAsync, canExecute: d => d.FilePath is not null));
        registry.Register(CommandIds.File.Close, new ActiveDocumentCommand(documents, dialogs, d => actions.CloseAsync(d)));
        registry.Register(CommandIds.File.CloseAll, new AsyncCommand(dialogs, () => actions.CloseAllAsync(), () => documents.Documents.Count > 0));
        registry.Register(CommandIds.File.Properties, new ActiveDocumentCommand(documents, dialogs, d => ShowPropertiesAsync(sp, d)));
        registry.Register(CommandIds.File.Settings, new AsyncCommand(dialogs, () =>
        {
            ShowSettings(sp);
            return Task.CompletedTask;
        }));
        registry.Register(CommandIds.File.Exit, new AsyncCommand(dialogs, () =>
        {
            window().Close();
            return Task.CompletedTask;
        }));

        // ---------------------------------------------------------------- Cofnij / Ponów
        registry.Register(CommandIds.Edit.Undo, new ActiveDocumentCommand(documents, dialogs, d => d.History.UndoAsync(), canExecute: d => d.History.CanUndo));
        registry.Register(CommandIds.Edit.Redo, new ActiveDocumentCommand(documents, dialogs, d => d.History.RedoAsync(), canExecute: d => d.History.CanRedo));

        // ---------------------------------------------------------------- karty dokumentów, wyszukiwanie poleceń
        registry.Register(CommandIds.Shell.CommandSearch, new AsyncCommand(dialogs, () =>
        {
            window().ActivateCommandSearch();
            return Task.CompletedTask;
        }));
        registry.Register(CommandIds.Shell.NextDocument, new AsyncCommand(dialogs, () =>
        {
            window().CycleDocument(+1);
            return Task.CompletedTask;
        }, () => documents.Documents.Count > 1));
        registry.Register(CommandIds.Shell.PreviousDocument, new AsyncCommand(dialogs, () =>
        {
            window().CycleDocument(-1);
            return Task.CompletedTask;
        }, () => documents.Documents.Count > 1));

        // ---------------------------------------------------------------- panele i wstążka
        var togglePanel = new ToggleCommand(_ => shell.IsSidePanelVisible = !shell.IsSidePanelVisible, () => shell.IsSidePanelVisible);
        var toggleRibbon = new ToggleCommand(_ => shell.IsRibbonMinimized = !shell.IsRibbonMinimized, () => shell.IsRibbonMinimized);
        shell.ShellStateChanged += (_, _) =>
        {
            togglePanel.RaiseIsCheckedChanged();
            toggleRibbon.RaiseIsCheckedChanged();
        };
        registry.Register(CommandIds.View.TogglePanel, togglePanel);
        registry.Register(CommandIds.View.ToggleRibbon, toggleRibbon);
        RegisterPanel(CommandIds.View.PanelThumbnails, PanelIds.Thumbnails);
        RegisterPanel(CommandIds.View.PanelBookmarks, PanelIds.Bookmarks);
        RegisterPanel(CommandIds.View.PanelComments, PanelIds.Comments);
        RegisterPanel(CommandIds.View.PanelAttachments, PanelIds.Attachments);
        RegisterPanel(CommandIds.View.PanelSignatures, PanelIds.Signatures);

        // Kontrakt z renderer-viewer: View.ReadingMode / View.FullScreen należą do modułu Viewer.
        // Jeśli Viewer ich nie zarejestrował (np. zaślepka), powłoka daje działające przełączniki na IShellService.
        if (registry.GetHandler(CommandIds.View.ReadingMode) is null)
        {
            var reading = new ToggleCommand(_ => shell.IsReadingMode = !shell.IsReadingMode, () => shell.IsReadingMode);
            shell.ShellStateChanged += (_, _) => reading.RaiseIsCheckedChanged();
            registry.Register(CommandIds.View.ReadingMode, reading);
        }
        if (registry.GetHandler(CommandIds.View.FullScreen) is null)
        {
            var full = new ToggleCommand(_ => shell.IsFullScreen = !shell.IsFullScreen, () => shell.IsFullScreen);
            shell.ShellStateChanged += (_, _) => full.RaiseIsCheckedChanged();
            registry.Register(CommandIds.View.FullScreen, full);
        }

        // ---------------------------------------------------------------- Pomoc
        registry.Register(CommandIds.Help.Shortcuts, new AsyncCommand(dialogs, () =>
        {
            ShowShortcuts(sp);
            return Task.CompletedTask;
        }));
        registry.Register(CommandIds.Help.Licenses, new AsyncCommand(dialogs, () =>
        {
            dialogs.ShowDialog(new LicensesWindow());
            return Task.CompletedTask;
        }));
        registry.Register(CommandIds.Help.OpenLogFolder, new AsyncCommand(dialogs, () =>
        {
            SettingsWindow.OpenFolder(paths.LogDirectory);
            return Task.CompletedTask;
        }));
        registry.Register(CommandIds.Help.About, new AsyncCommand(dialogs, () =>
        {
            dialogs.ShowDialog(new AboutWindow(loc, lib => dialogs.ShowDialog(new LicensesWindow(lib))));
            return Task.CompletedTask;
        }));

        void RegisterPanel(string commandId, string panelId) =>
            registry.Register(commandId, new AsyncCommand(dialogs, () =>
            {
                shell.ShowPanel(panelId);
                return Task.CompletedTask;
            }, () => panels.Panels.Any(p => p.Id == panelId)));
    }

    internal static void ShowSettings(IServiceProvider sp)
    {
        var dialogs = sp.GetRequiredService<IDialogService>();
        var vm = new SettingsViewModel(sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IThemeService>(), sp.GetRequiredService<IDocumentManager>());
        var w = new SettingsWindow(vm, dialogs, sp.GetRequiredService<ILocalizationService>(), sp.GetRequiredService<IAppPaths>(), () => ShowShortcuts(sp));
        dialogs.ShowDialog(w);
    }

    internal static void ShowShortcuts(IServiceProvider sp) =>
        sp.GetRequiredService<IDialogService>().ShowDialog(new ShortcutsWindow(sp.GetRequiredService<ICommandRegistry>(),
            sp.GetRequiredService<ILocalizationService>(), sp.GetRequiredService<ISettingsService>()));

    /// <summary>Ctrl+D: odczyt metadanych w tle, okno, zapis przez polecenie z Cofnij/Ponów (Core.Undo.Metadata).</summary>
    private static async Task ShowPropertiesAsync(IServiceProvider sp, IPdfDocumentSession d)
    {
        var metadataService = sp.GetRequiredService<IMetadataService>();
        var dialogs = sp.GetRequiredService<IDialogService>();
        var metadata = await metadataService.GetAsync(d.Current.GetBytes());
        var w = new DocumentPropertiesWindow(d, metadata, sp.GetRequiredService<ILocalizationService>());
        if (dialogs.ShowDialog(w) != true || w.Result is null) return;
        var updated = w.Result;
        await d.ExecuteAsync(new DelegateDocumentCommand("Core.Undo.Metadata",
            (pdf, _, ct) => metadataService.SetAsync(pdf, updated, ct),
            DocumentChangeKind.Metadata, DocumentPermissions.ModifyContent));
    }

    /// <summary>Polecenie asynchroniczne bez dokumentu; wyjątki → IDialogService.ShowError.</summary>
    private sealed class AsyncCommand : ICommand
    {
        private readonly IDialogService _dialogs;
        private readonly Func<Task> _run;
        private readonly Func<bool>? _canExecute;
        private bool _running;

        public AsyncCommand(IDialogService dialogs, Func<Task> run, Func<bool>? canExecute = null)
        {
            _dialogs = dialogs;
            _run = run;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (_running) return;
            _running = true;
            try { await _run(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _dialogs.ShowError(ex); }
            finally
            {
                _running = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}

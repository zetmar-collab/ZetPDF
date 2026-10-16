using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Shell;

/// <summary>
/// Polecenia powłoki – SZKIELET (Otwórz, Zapisz, Zapisz jako, Zamknij, Cofnij, Ponów, Zakończ).
/// ui-shell uzupełnia pozostałe polecenia modułu Shell z katalogu.
/// </summary>
internal static class ShellCommands
{
    public static void Register(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<ICommandRegistry>();
        var documents = sp.GetRequiredService<IDocumentManager>();
        var dialogs = sp.GetRequiredService<IDialogService>();
        var loc = sp.GetRequiredService<ILocalizationService>();

        registry.Register(CommandIds.File.Open, new AsyncCommand(async () =>
        {
            var files = dialogs.OpenFiles(loc["Cmd.File.Open"], new[] { FileFilter.Pdf(loc), FileFilter.All(loc) }, multiselect: true);
            if (files is null) return;
            foreach (var f in files)
            {
                try { await documents.OpenAsync(f); }
                catch (Exception ex) { dialogs.ShowError(ex, f); }
            }
        }));

        registry.Register(CommandIds.File.Save, new ActiveDocumentCommand(documents, dialogs, async d =>
        {
            if (d.FilePath is null) await SaveAsAsync(d);
            else await documents.SaveAsync(d);
        }));

        registry.Register(CommandIds.File.SaveAs, new ActiveDocumentCommand(documents, dialogs, SaveAsAsync));

        registry.Register(CommandIds.File.Close, new ActiveDocumentCommand(documents, dialogs, d =>
        {
            if (d.IsModified && !dialogs.Confirm(loc.Format("Shell.Close.Unsaved", d.DisplayName))) return Task.CompletedTask; // ui-shell: Zapisz/Nie zapisuj/Anuluj
            documents.Close(d);
            return Task.CompletedTask;
        }));

        registry.Register(CommandIds.Edit.Undo, new ActiveDocumentCommand(documents, dialogs, d => d.History.UndoAsync(), canExecute: d => d.History.CanUndo));
        registry.Register(CommandIds.Edit.Redo, new ActiveDocumentCommand(documents, dialogs, d => d.History.RedoAsync(), canExecute: d => d.History.CanRedo));
        registry.Register(CommandIds.File.Exit, new AsyncCommand(() =>
        {
            Application.Current.MainWindow?.Close();
            return Task.CompletedTask;
        }));

        async Task SaveAsAsync(IPdfDocumentSession d)
        {
            var path = dialogs.SaveFile(loc["Cmd.File.SaveAs"], new[] { FileFilter.Pdf(loc) }, d.DisplayName);
            if (path is not null) await documents.SaveAsync(d, path);
        }
    }

    private sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _run;
        public AsyncCommand(Func<Task> run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter)
        {
            try { await _run(); }
            catch (Exception ex) { App.Services.GetRequiredService<IDialogService>().ShowError(ex); }
        }
    }
}

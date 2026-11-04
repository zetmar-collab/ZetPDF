using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Security.UI;

namespace ZetPDF.Security;

/// <summary>Obsługa 13 poleceń wstążki modułu Security, panelu Podpisy i karty kontekstowej Wyniki porównania.</summary>
internal sealed class SecurityCommands
{
    private readonly IServiceProvider _services;
    private readonly ICommandRegistry _registry;
    private readonly IDocumentManager _documents;
    private readonly ILocalizationService _loc;
    private SignaturesPanel? _panel;
    private CompareWindow? _compare;
    private CompareResult? _compareResult;

    public SecurityCommands(IServiceProvider services, ICommandRegistry registry)
    {
        _services = services;
        _registry = registry;
        _documents = services.GetRequiredService<IDocumentManager>();
        _loc = services.GetRequiredService<ILocalizationService>();
    }

    private IDialogService Dialogs => _services.GetRequiredService<IDialogService>();
    private IShellService? Shell => _services.GetService<IShellService>();
    private SecurityService Security => _services.GetRequiredService<SecurityService>();
    private ISignatureService Signatures => _services.GetRequiredService<ISignatureService>();
    private ISettingsService? Settings => _services.GetService<ISettingsService>();

    public void RegisterAll()
    {
        // ochrona
        _registry.Register(CommandIds.Security.PasswordProtect, new ActiveDocumentCommand(_documents, _services.GetService<IDialogService>(), PasswordProtectAsync));
        _registry.Register(CommandIds.Security.RemoveSecurity, new ActiveDocumentCommand(_documents, _services.GetService<IDialogService>(), RemoveSecurityAsync,
            canExecute: d => d.Current.Encryption is not null));
        _registry.Register(CommandIds.Security.Properties, new ActiveDocumentCommand(_documents, _services.GetService<IDialogService>(), ShowPropertiesAsync));

        // podpisy (podpisywanie = uprawnienie „wypełnianie formularzy i podpisywanie”)
        _registry.Register(CommandIds.Sign.PlaceSignature, new ActiveDocumentCommand(_documents, _services.GetService<IDialogService>(), PlaceSignatureAsync, DocumentPermissions.FillForms));
        _registry.Register(CommandIds.Sign.Validate, new ActiveDocumentCommand(_documents, _services.GetService<IDialogService>(), ValidateAsync));
        _registry.Register(CommandIds.Sign.Timestamp, new ActiveDocumentCommand(_documents, _services.GetService<IDialogService>(), TimestampAsync, DocumentPermissions.FillForms));
        _registry.Register(CommandIds.Sign.DigitalIds, new Relay(() => Guard(() =>
            Dialogs.ShowDialog(new DigitalIdsWindow(_loc, Signatures, _services.GetRequiredService<IAppPaths>(), Dialogs, Settings)))));

        // porównanie
        _registry.Register(CommandIds.Compare.Start, new Relay(() => _ = GuardAsync(StartCompareAsync)));
        _registry.Register(CommandIds.Compare.Next, new Relay(() => _compare?.Move(+1), () => _compare is not null));
        _registry.Register(CommandIds.Compare.Previous, new Relay(() => _compare?.Move(-1), () => _compare is not null));
        _registry.Register(CommandIds.Compare.Report, new Relay(() => _ = GuardAsync(ExportReportAsync), () => _compareResult is not null));
        _registry.Register(CommandIds.Compare.Close, new Relay(() => _compare?.Close(), () => _compare is not null));

        // Kreator działań
        _registry.Register(CommandIds.File.ActionWizard, new Relay(() => Guard(() =>
        {
            var files = _documents.ActiveDocument?.FilePath is { } p ? new[] { p } : Array.Empty<string>();
            Dialogs.ShowDialog(new ActionWizardWindow(_loc, _services.GetRequiredService<IActionWizard>(), Dialogs, files));
        })));

        _services.GetService<IPanelRegistry>()?.Register(new PanelDescriptor(PanelIds.Signatures, "Security.Panel.Title", "", 50, sp => _panel = new SignaturesPanel(sp)));

        // DECYZJE D-08: komunikat, gdy przy zapisie hasło właściciela zastąpi nieznane hasło otwierania
        _documents.DocumentOpened += (_, s) =>
        {
            if (Security.IsResaveOwnerOnly(s.SecurityInfo)) Shell?.SetStatus(_loc["Core.Encryption.ResaveOwnerOnly"]);
        };
    }

    private void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Dialogs.ShowError(ex); }
    }

    private async Task GuardAsync(Func<Task> a)
    {
        try { await a(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Dialogs.ShowError(ex); }
    }

    // ------------------------------------------------------------------ ochrona hasłem

    /// <summary>
    /// Zmiana zabezpieczeń wymaga pełnych uprawnień. Dokument otwarty hasłem użytkownika: pytamy o hasło właściciela,
    /// sprawdzamy je na pliku i otwieramy dokument ponownie z tym hasłem.
    /// </summary>
    private async Task<IPdfDocumentSession?> EnsureOwnerAccessAsync(IPdfDocumentSession session)
    {
        if (session.Can(DocumentPermissions.All)) return session;
        if (session.FilePath is not { } path || !File.Exists(path))
        {
            Dialogs.ShowMessage(_loc["Security.Owner.NotPossible"], _loc["Security.Owner.Title"], MessageSeverity.Warning);
            return null;
        }
        Dialogs.ShowMessage(_loc["Security.Owner.Needed"], _loc["Security.Owner.Title"]);
        var original = await File.ReadAllBytesAsync(path);
        for (bool retry = false; ; retry = true)
        {
            var pwd = Dialogs.AskPassword(session.DisplayName, retry);
            if (pwd is null) return null;
            PdfSecurityInfo info;
            try { info = await Security.AnalyzeAsync(original, pwd); }
            catch (PdfPasswordRequiredException) { continue; }
            if (!info.OpenedWithOwnerPassword) continue;
            if (session.IsModified && !Dialogs.Confirm(_loc["Security.Owner.DiscardChanges"], _loc["Security.Owner.Title"])) return null;
            _documents.Close(session);
            return await _documents.OpenAsync(path, pwd);
        }
    }

    private async Task PasswordProtectAsync(IPdfDocumentSession session)
    {
        var s = await EnsureOwnerAccessAsync(session);
        if (s is null) return;
        var dlg = new PasswordProtectDialog(_loc, s.Current.Encryption);
        if (Dialogs.ShowDialog(dlg) != true || dlg.Result is null) return;
        await s.ExecuteAsync(new SetEncryptionCommand("Core.Undo.SetEncryption", dlg.Result));
        Shell?.SetStatus(_loc["Security.Protect.Done"]);
    }

    private async Task RemoveSecurityAsync(IPdfDocumentSession session)
    {
        var s = await EnsureOwnerAccessAsync(session);
        if (s is null) return;
        if (s.Current.Encryption is null) { Dialogs.ShowMessage(_loc["Security.Remove.NoSecurity"], _loc["Cmd.Security.RemoveSecurity"]); return; }
        if (!Dialogs.Confirm(_loc["Security.Remove.Confirm"], _loc["Cmd.Security.RemoveSecurity"])) return;
        await s.ExecuteAsync(new SetEncryptionCommand("Core.Undo.RemoveEncryption", null));
        Shell?.SetStatus(_loc["Security.Remove.Done"]);
    }

    private Task ShowPropertiesAsync(IPdfDocumentSession session)
    {
        Dialogs.ShowDialog(new SecurityPropertiesDialog(_loc, session, Security.IsResaveOwnerOnly(session.SecurityInfo)));
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ podpisy

    private async Task PlaceSignatureAsync(IPdfDocumentSession session)
    {
        var tools = _services.GetService<IToolService>();
        if (tools is null || _services.GetService<IViewerService>()?.GetView(session) is null)
        {
            await SignAsync(session, 0, default, null); // brak widoku – podpis niewidoczny
            return;
        }
        var tool = new SignatureTool(tools);
        try { tool.EmptyFields = SignatureService.GetEmptySignatureFields(session.Current.GetSharedBytes(), null); }
        catch (Exception ex) when (ex is PdfCorruptException or FormatException or NotSupportedException) { }
        tool.Placed += (page, rect, field) => _ = GuardAsync(() => SignAsync(session, page, rect, field));
        tools.Activate(tool);
        Shell?.SetStatus(_loc[tool.EmptyFields.Count > 0 ? "Security.Sign.DrawOrPick" : "Security.Sign.Draw"]);
    }

    /// <summary>Bajty do podpisu/znacznika czasu: oryginalny plik, gdy dokument niezmieniony (zachowuje wcześniejsze podpisy), inaczej wersja do zapisu (zaszyfrowana, jeśli trzeba).</summary>
    private async Task<(byte[] Pdf, string? Password, string? ReopenPassword)> GetFinalBytesAsync(IPdfDocumentSession session)
    {
        if (session.FilePath is { } path && !session.IsModified && File.Exists(path))
            return (await File.ReadAllBytesAsync(path), session.SecurityInfo.Password, session.SecurityInfo.Password);
        var bytes = await _documents.GetBytesForSaveAsync(session);
        var enc = session.Current.Encryption;
        return (bytes, enc?.OwnerPassword, enc is null ? null : enc.UserPassword ?? enc.OwnerPassword);
    }

    private string? AskTarget(IPdfDocumentSession session, string suffixKey)
    {
        var baseName = Path.GetFileNameWithoutExtension(session.FilePath ?? session.DisplayName);
        return Dialogs.SaveFile(_loc["Security.Sign.SaveTitle"], new[] { FileFilter.Pdf(_loc) }, baseName + _loc[suffixKey] + ".pdf",
            session.FilePath is null ? null : Path.GetDirectoryName(session.FilePath));
    }

    private async Task SignAsync(IPdfDocumentSession session, int page, PdfRect rect, string? field)
    {
        Shell?.SetStatus(null);
        var settings = Settings?.GetModuleSettings<SecurityModuleSettings>("Security") ?? new SecurityModuleSettings();
        var tsa = Settings?.Current.TsaUrl ?? string.Empty;
        var dlg = new SignDialog(_loc, Signatures, _services.GetRequiredService<IAppPaths>(), Dialogs, settings, tsa, !rect.IsEmpty);
        if (Dialogs.ShowDialog(dlg) != true || dlg.Certificate is null) return;
        Settings?.SetModuleSettings("Security", settings);
        var target = AskTarget(session, "Security.Sign.FileSuffix");
        if (target is null) return;

        var (pdf, password, reopen) = await GetFinalBytesAsync(session);
        var request = new SignRequest
        {
            Certificate = new SigningCertificate(dlg.Source, dlg.Certificate, dlg.PfxPath),
            FieldName = field,
            PageIndex = page,
            Rect = field is null && dlg.Invisible ? default : rect,
            Reason = dlg.Reason,
            Location = dlg.Location,
            ContactInfo = dlg.ContactInfo,
            Appearance = dlg.Appearance,
            TsaUrl = dlg.UseTsa ? tsa : null, // połączenie sieciowe tylko na wyraźne życzenie użytkownika
        };
        byte[]? signed = null;
        if (!await Dialogs.RunWithProgressAsync(_loc["Security.Sign.Progress"], async (_, ct) => signed = await Signatures.SignAsync(pdf, request, password, ct), cancellable: dlg.UseTsa)
            || signed is null) return;
        await WriteAndOpenAsync(session, target, signed, reopen);
        Shell?.SetStatus(_loc["Security.Sign.Done"]);
    }

    private async Task WriteAndOpenAsync(IPdfDocumentSession session, string target, byte[] data, string? reopenPassword)
    {
        await _services.GetRequiredService<IFileService>().WriteAllBytesSafeAsync(target, data);
        if (session.FilePath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            await _documents.RevertAsync(session);
        else
            await _documents.OpenAsync(target, reopenPassword);
        Shell?.ShowPanel(PanelIds.Signatures);
        if (_panel is not null) await _panel.RefreshAsync();
    }

    private async Task ValidateAsync(IPdfDocumentSession session)
    {
        Shell?.ShowPanel(PanelIds.Signatures);
        if (_panel is not null) await _panel.RefreshAsync();
    }

    private async Task TimestampAsync(IPdfDocumentSession session)
    {
        var tsa = Settings?.Current.TsaUrl?.Trim() ?? string.Empty;
        if (tsa.Length == 0)
        {
            Dialogs.ShowMessage(_loc["Security.Timestamp.Error.NoTsa"], _loc["Cmd.Sign.Timestamp"], MessageSeverity.Warning);
            return;
        }
        if (!Dialogs.Confirm(_loc.Format("Security.Timestamp.Confirm", tsa), _loc["Cmd.Sign.Timestamp"], MessageSeverity.Info)) return;
        var target = AskTarget(session, "Security.Timestamp.FileSuffix");
        if (target is null) return;
        var (pdf, password, reopen) = await GetFinalBytesAsync(session);
        byte[]? stamped = null;
        if (!await Dialogs.RunWithProgressAsync(_loc["Security.Timestamp.Progress"], async (_, ct) => stamped = await Signatures.TimestampDocumentAsync(pdf, tsa, password, ct))
            || stamped is null) return;
        await WriteAndOpenAsync(session, target, stamped, reopen);
        Shell?.SetStatus(_loc["Security.Timestamp.Done"]);
    }

    // ------------------------------------------------------------------ porównanie

    private async Task<byte[]?> LoadSourceAsync(CompareSource src)
    {
        if (src.Session is { } s)
        {
            if (!s.Can(DocumentPermissions.CopyContent)) throw new DocumentPermissionException(DocumentPermissions.CopyContent);
            return s.Current.GetSharedBytes();
        }
        var bytes = await File.ReadAllBytesAsync(src.Path!);
        if (!PdfSecurityHelper.IsEncrypted(bytes)) return bytes;
        for (bool retry = false; ; retry = true)
        {
            var pwd = Dialogs.AskPassword(src.Name, retry);
            if (pwd is null) return null;
            PdfSecurityInfo info;
            try { info = await Security.AnalyzeAsync(bytes, pwd); }
            catch (PdfPasswordRequiredException) { continue; }
            if (!info.OpenedWithOwnerPassword && !info.FilePermissions.HasFlag(DocumentPermissions.CopyContent))
                throw new DocumentPermissionException(DocumentPermissions.CopyContent);
            if (_services.GetService<IPdfRenderer>() is { } r)
            {
                try { return await r.RemoveSecurityAsync(bytes, pwd); }
                catch (NotImplementedException) { }
            }
            return await Task.Run(() => PdfSecurityHelper.RemoveSecurity(bytes, pwd));
        }
    }

    private async Task StartCompareAsync()
    {
        var dlg = new CompareDialog(_loc, Dialogs, _documents);
        if (Dialogs.ShowDialog(dlg) != true || dlg.Old is null || dlg.New is null) return;
        var a = await LoadSourceAsync(dlg.Old);
        if (a is null) return;
        var b = await LoadSourceAsync(dlg.New);
        if (b is null) return;
        var options = dlg.Options;
        CompareResult? result = null;
        if (!await Dialogs.RunWithProgressAsync(_loc["Security.Compare.Progress"], async (p, ct) =>
                result = await _services.GetRequiredService<ICompareService>().CompareAsync(a, dlg.Old.Name, b, dlg.New.Name, options, p, ct))
            || result is null) return;

        _compare?.Close();
        IRenderDocument? docA = null, docB = null;
        if (_services.GetService<IPdfRenderer>() is { } renderer)
        {
            try
            {
                docA = await renderer.OpenAsync(a);
                docB = await renderer.OpenAsync(b);
            }
            catch (Exception ex) when (ex is NotImplementedException or PdfCorruptException) { docA?.Dispose(); docA = docB = null; }
        }
        var window = new CompareWindow(_loc, result, docA, docB, () => _ = GuardAsync(ExportReportAsync));
        if (Shell?.MainWindow is { } owner && owner.IsLoaded) window.Owner = owner;
        _compare = window;
        _compareResult = result;
        window.Closed += (_, _) =>
        {
            docA?.Dispose();
            docB?.Dispose();
            if (ReferenceEquals(_compare, window)) { _compare = null; _compareResult = null; }
            Shell?.SetContextualTabVisible(ContextualTabIds.CompareResults, false);
            CommandManager.InvalidateRequerySuggested();
        };
        Shell?.SetContextualTabVisible(ContextualTabIds.CompareResults, true);
        window.Show();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ExportReportAsync()
    {
        if (_compareResult is not { } r) return;
        var path = Dialogs.SaveFile(_loc["Cmd.Compare.Report"], new[]
        {
            new FileFilter(_loc["Security.FileFilter.Html"], "*.html"),
            new FileFilter(_loc["Security.FileFilter.Text"], "*.txt"),
        }, _loc["Security.Compare.ReportFileName"] + ".html");
        if (path is null) return;
        var format = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? CompareReportFormat.Text : CompareReportFormat.Html;
        await _services.GetRequiredService<ICompareService>().ExportReportAsync(r, path, format);
        Shell?.SetStatus(_loc.Format("Security.Compare.ReportSaved", path));
    }

    /// <summary>Proste polecenie niezależne od dokumentu.</summary>
    private sealed class Relay : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _can;
        public Relay(Action execute, Func<bool>? can = null) { _execute = execute; _can = can; }
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
        public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();
    }
}

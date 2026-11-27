using Microsoft.Extensions.Logging;
using ZetPDF.App.Services;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Shell;

/// <summary>
/// Operacje powłoki na dokumentach: otwieranie (wiele plików, hasło z linii poleceń), zapis / zapis jako,
/// zamykanie z pytaniem o niezapisane zmiany (Zapisz / Nie zapisuj / Anuluj), zamykanie wszystkich.
/// Wszystko asynchronicznie – wątek UI nie jest blokowany (ciężka praca dzieje się w IDocumentManager).
/// </summary>
public sealed class DocumentActions
{
    private readonly IDocumentManager _documents;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _loc;
    private readonly IRecentFilesService _recent;
    private readonly IShellService _shell;
    private readonly ILogger<DocumentActions> _log;
    private readonly OwnerProtectionGuard _ownerGuard;

    public DocumentActions(IDocumentManager documents, IDialogService dialogs, ILocalizationService loc, IRecentFilesService recent, IShellService shell, ILogger<DocumentActions> log, OwnerProtectionGuard ownerGuard)
    {
        _ownerGuard = ownerGuard;
        _documents = documents;
        _dialogs = dialogs;
        _loc = loc;
        _recent = recent;
        _shell = shell;
        _log = log;
    }

    /// <summary>Otwiera pliki po kolei; błędy pokazuje i przechodzi do następnego pliku.</summary>
    public async Task OpenAsync(IEnumerable<string> paths, string? password = null)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = Path.GetFullPath(raw.Trim().Trim('"')); }
            catch (Exception ex)
            {
                _dialogs.ShowError(ex, raw);
                continue;
            }
            if (!File.Exists(path))
            {
                if (_recent.Files.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    if (_dialogs.Confirm(_loc.Format("Shell.Recent.Missing", path), null, MessageSeverity.Warning)) _recent.Remove(path);
                }
                else _dialogs.ShowMessage(_loc.Format("Shell.Open.NotFound", path), null, MessageSeverity.Warning);
                continue;
            }
            _shell.SetStatus(_loc.Format("Shell.Status.Opening", Path.GetFileName(path)));
            try
            {
                await _documents.OpenAsync(path, password);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Open failed: {Path}", path);
                _dialogs.ShowError(ex, path);
            }
            finally
            {
                _shell.SetStatus(null);
            }
        }
    }

    public async Task OpenWithDialogAsync()
    {
        var dir = _documents.ActiveDocument?.FilePath is { } p ? Path.GetDirectoryName(p) : null;
        var files = _dialogs.OpenFiles(_loc["Cmd.File.Open"], new[] { FileFilter.Pdf(_loc), FileFilter.All(_loc) }, multiselect: true, initialDirectory: dir);
        if (files is not null) await OpenAsync(files);
    }

    /// <summary>Zapis; dokument bez ścieżki → Zapisz jako. Zwraca false, gdy anulowano lub wystąpił błąd.</summary>
    public async Task<bool> SaveAsync(IPdfDocumentSession d)
    {
        if (d.FilePath is null) return await SaveAsAsync(d);
        switch (ConfirmOverwriteOwnFile(d))
        {
            case OverwriteDecision.Cancel: return false;
            case OverwriteDecision.SaveCopy: return await SaveAsAsync(d);
        }
        if (!await _ownerGuard.EnsureAsync(d)) return false;
        try
        {
            _shell.SetStatus(_loc.Format("Shell.Status.Saving", d.DisplayName));
            await _documents.SaveAsync(d);
            _shell.SetStatus(SavedStatus(d, d.DisplayName));
            return true;
        }
        catch (Exception ex)
        {
            _shell.SetStatus(null);
            _dialogs.ShowError(ex, d.FilePath);
            return false;
        }
    }

    public async Task<bool> SaveAsAsync(IPdfDocumentSession d)
    {
        var name = d.DisplayName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? d.DisplayName : d.DisplayName + ".pdf";
        var dir = d.FilePath is null ? null : Path.GetDirectoryName(d.FilePath);
        var path = _dialogs.SaveFile(_loc["Cmd.File.SaveAs"], new[] { FileFilter.Pdf(_loc) }, name, dir);
        if (path is null) return false;
        var full = Path.GetFullPath(path);
        if (_documents.Documents.Any(o => !ReferenceEquals(o, d) && string.Equals(o.FilePath, full, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.ShowMessage(_loc.Format("Shell.SaveAs.AlreadyOpen", Path.GetFileName(full)), null, MessageSeverity.Warning);
            return false;
        }
        // Wskazanie w oknie „Zapisz jako” tego samego pliku to zwykły zapis – te same ostrzeżenia (zmiana na dysku, podpisy).
        if (d.FilePath is { } own && string.Equals(Path.GetFullPath(own), full, StringComparison.OrdinalIgnoreCase))
        {
            switch (ConfirmOverwriteOwnFile(d))
            {
                case OverwriteDecision.Cancel: return false;
                case OverwriteDecision.SaveCopy: return await SaveAsAsync(d);
            }
        }
        if (!await _ownerGuard.EnsureAsync(d)) return false;
        try
        {
            _shell.SetStatus(_loc.Format("Shell.Status.Saving", Path.GetFileName(path)));
            await _documents.SaveAsync(d, path);
            _shell.SetStatus(SavedStatus(d, Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex)
        {
            _shell.SetStatus(null);
            _dialogs.ShowError(ex, path);
            return false;
        }
    }

    internal enum OverwriteDecision { Proceed, Cancel, SaveCopy }

    /// <summary>Przed nadpisaniem własnego pliku dokumentu: plik zmieniony na dysku przez inny program i podpisy, które zapis unieważni.</summary>
    internal OverwriteDecision ConfirmOverwriteOwnFile(IPdfDocumentSession d)
    {
        if (_documents is not Core.Documents.DocumentManager dm) return OverwriteDecision.Proceed;
        if (dm.HasChangedOnDisk(d) && !_dialogs.Confirm(_loc.Format("Shell.Save.ChangedOnDisk", d.DisplayName))) return OverwriteDecision.Cancel;
        if (d.IsModified && dm.HasSignatures(d))
        {
            var answer = _dialogs is DialogService ds ? ds.AskSaveSigned(d.DisplayName) : _dialogs.AskYesNoCancel(_loc.Format("Shell.Save.Signed", d.DisplayName));
            return answer switch
            {
                YesNoCancel.Yes => OverwriteDecision.SaveCopy,
                YesNoCancel.No => OverwriteDecision.Proceed,
                _ => OverwriteDecision.Cancel,
            };
        }
        return OverwriteDecision.Proceed;
    }

    /// <summary>
    /// Tekst paska stanu po zapisie. DECYZJE D-22: plik RC4 (lub o nieznanej metodzie) zapisany z ochroną jest
    /// szyfrowany ponownie AES-256 – użytkownik dostaje o tym informację.
    /// </summary>
    internal string SavedStatus(IPdfDocumentSession d, string name) => SavedStatus(_loc, _documents, d, name);

    public static string SavedStatus(ILocalizationService loc, IDocumentManager documents, IPdfDocumentSession d, string name)
    {
        var effective = documents is Core.Documents.DocumentManager dm ? dm.GetEffectiveSaveEncryption(d) : d.Current.Encryption;
        var source = d.SecurityInfo;
        if (effective is not null && source.IsEncrypted && source.Method != effective.Method)
            return loc.Format("Shell.Status.SavedReencrypted", name, loc["Shell.Encryption." + source.Method], loc["Shell.Encryption." + effective.Method]);
        return loc.Format("Shell.Status.Saved", name);
    }

    /// <summary>Pyta o zapis (jeśli trzeba) i zamyka. Zwraca false, gdy użytkownik anulował.</summary>
    public async Task<bool> CloseAsync(IPdfDocumentSession d)
    {
        if (!await ConfirmDiscardOrSaveAsync(d)) return false;
        _documents.Close(d);
        return true;
    }

    /// <summary>Zamyka wszystkie dokumenty (z pytaniami). Zwraca false, gdy użytkownik przerwał.</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var d in _documents.Documents.ToArray())
        {
            if (!await CloseAsync(d)) return false;
        }
        return true;
    }

    /// <summary>Przed wyjściem z programu: pyta o każdy zmieniony dokument. Zwraca false, gdy anulowano.</summary>
    public async Task<bool> ConfirmExitAsync()
    {
        foreach (var d in _documents.Documents.Where(x => x.IsModified).ToArray())
        {
            _documents.ActiveDocument = d;
            if (!await ConfirmDiscardOrSaveAsync(d)) return false;
        }
        return true;
    }

    private async Task<bool> ConfirmDiscardOrSaveAsync(IPdfDocumentSession d)
    {
        if (!d.IsModified) return true;
        var answer = _dialogs is DialogService ds ? ds.AskSaveChanges(d.DisplayName) : _dialogs.AskYesNoCancel(_loc.Format("Shell.Close.SavePrompt", d.DisplayName));
        return answer switch
        {
            YesNoCancel.Yes => await SaveAsync(d),
            YesNoCancel.No => true,
            _ => false,
        };
    }

    public async Task RevertAsync(IPdfDocumentSession d)
    {
        if (d.IsModified && !_dialogs.Confirm(_loc.Format("Shell.Revert.Confirm", d.DisplayName))) return;
        try { await _documents.RevertAsync(d); }
        catch (Exception ex) { _dialogs.ShowError(ex, d.FilePath); }
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ZetPDF.App.Dialogs;
using ZetPDF.App.Services;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Services;

namespace ZetPDF.App.Shell;

/// <summary>
/// Start programu: kopie odzyskiwania po procesach, które już nie działają → okno z listą (Odzyskaj / Odrzuć / Zdecyduj później).
/// Odzyskany dokument od razu dostaje własną kopię autozapisu, dopiero potem znika kopia po awarii.
/// </summary>
public sealed class RecoveryFlow
{
    private readonly AutoSaveService _autoSave;
    private readonly IDocumentManager _documents;
    private readonly DialogService _dialogs;
    private readonly ILocalizationService _loc;
    private readonly IShellService _shell;
    private readonly ILogger<RecoveryFlow> _log;

    public RecoveryFlow(AutoSaveService autoSave, IDocumentManager documents, IDialogService dialogs, ILocalizationService loc, IShellService shell, ILogger<RecoveryFlow> log)
    {
        _autoSave = autoSave;
        _documents = documents;
        _dialogs = (DialogService)dialogs;
        _loc = loc;
        _shell = shell;
        _log = log;
    }

    public async Task RunAsync()
    {
        var abandoned = await Task.Run(_autoSave.Store.FindAbandoned);
        if (abandoned.Count == 0) return;
        _log.LogInformation("Found {Count} abandoned recovery copies", abandoned.Count);
        _dialogs.ShowDialog(new RecoveryWindow(_loc, abandoned, RecoverAsync, Discard));
    }

    /// <summary>Zwraca true, gdy kopia została obsłużona (dokument otwarty) i wiersz może zniknąć z listy.</summary>
    internal async Task<bool> RecoverAsync(RecoveryInfo info)
    {
        var store = _autoSave.Store;
        byte[] pdf;
        try
        {
            pdf = await Task.Run(() => store.ReadPdf(info.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log.LogWarning(ex, "Cannot read recovery copy {Id}", info.Id);
            _dialogs.ShowMessage(_loc.Format("Shell.Recovery.Unreadable", info.DisplayName), null, MessageSeverity.Warning);
            return false;
        }
        try
        {
            var session = await ((DocumentManager)_documents).OpenRecoveredAsync(info, pdf, AskNewPassword);
            if (session is null) return false;
            await _autoSave.SaveNowAsync();
            store.Delete(info.Id);
            _shell.SetStatus(_loc.Format("Shell.Recovery.Status", info.DisplayName));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dialogs.ShowError(ex, info.DisplayName);
            return false;
        }
    }

    private bool Discard(RecoveryInfo info)
    {
        if (!_dialogs.Confirm(_loc.Format("Shell.Recovery.DiscardConfirm", info.DisplayName))) return false;
        _autoSave.Store.Delete(info.Id);
        return true;
    }

    private string? AskNewPassword(string documentName) =>
        _dialogs.AskRecoveryPassword(documentName);
}

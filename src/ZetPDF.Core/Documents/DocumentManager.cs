using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf.IO;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Documents;

/// <summary>
/// Implementacja <see cref="IDocumentManager"/>. Otwieranie: odczyt bajtów → sprawdzenie (IPdfRenderer.ProbeAsync,
/// zapasowo PDFsharp) → pytanie o hasło → odszyfrowanie do pamięci (IPdfRenderer.RemoveSecurityAsync) →
/// analiza zabezpieczeń (ISecurityService, zapasowo <see cref="PdfSecurityHelper"/>) → sesja.
/// Zapis: zrzut → szyfrowanie wg <see cref="DocumentSnapshot.Encryption"/> → IFileService.WriteAllBytesSafeAsync.
/// Usługi modułów, które jeszcze rzucają NotImplementedException, są zastępowane ścieżką zapasową.
/// </summary>
public sealed class DocumentManager : IDocumentManager
{
    private readonly IServiceProvider _services;
    private readonly IFileService _files;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IRecentFilesService _recent;
    private readonly ILogger<DocumentManager> _log;
    private readonly ObservableCollection<IPdfDocumentSession> _documents = new();
    private IPdfDocumentSession? _active;
    private readonly List<SaveTransform> _saveTransforms = new();

    public DocumentManager(IServiceProvider services, IFileService files, IAppPaths paths, ISettingsService settings, IRecentFilesService recent, ILogger<DocumentManager> log)
    {
        _services = services;
        _files = files;
        _paths = paths;
        _settings = settings;
        _recent = recent;
        _log = log;
        Documents = new ReadOnlyObservableCollection<IPdfDocumentSession>(_documents);
    }

    public ReadOnlyObservableCollection<IPdfDocumentSession> Documents { get; }

    public IPdfDocumentSession? ActiveDocument
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value)) return;
            if (value is not null && !_documents.Contains(value)) throw new ArgumentException("Document is not open.", nameof(value));
            _active = value;
            ActiveDocumentChanged?.Invoke(this, value);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public event EventHandler<IPdfDocumentSession?>? ActiveDocumentChanged;
    public event EventHandler<IPdfDocumentSession>? DocumentOpened;
    public event EventHandler<IPdfDocumentSession>? DocumentClosed;

    private IPdfRenderer? Renderer => _services.GetService<IPdfRenderer>();
    private ISecurityService? Security => _services.GetService<ISecurityService>();
    private IDialogService? Dialogs => _services.GetService<IDialogService>();

    public async Task<IPdfDocumentSession?> OpenAsync(string path, string? password = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var existing = _documents.FirstOrDefault(d => string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveDocument = existing;
            return existing;
        }

        var original = await File.ReadAllBytesAsync(full, ct);
        var loaded = await LoadAsync(original, Path.GetFileName(full), password, ct);
        if (loaded is null) return null;

        var session = CreateSession(full, Path.GetFileName(full), loaded.Value, startModified: false);
        _recent.Add(full);
        _log.LogInformation("Opened {File} ({Bytes} B, encrypted={Encrypted})", full, original.Length, loaded.Value.Info.IsEncrypted);
        return session;
    }

    public async Task<IPdfDocumentSession> CreateNewAsync(byte[] pdf, string displayName, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(pdf, displayName, null, ct) ?? throw new PdfPasswordRequiredException();
        return CreateSession(null, displayName, loaded, startModified: true);
    }

    public async Task SaveAsync(IPdfDocumentSession session, string? path = null, CancellationToken ct = default)
    {
        var target = path ?? session.FilePath ?? throw new InvalidOperationException("Document has no file path – use Save As.");
        target = Path.GetFullPath(target);
        var bytes = await GetBytesForSaveAsync(session, ct);
        await _files.WriteAllBytesSafeAsync(target, bytes, keepBackup: false, ct);
        if (session is PdfDocumentSession s) s.MarkSaved(target);
        _recent.Add(target);
        _log.LogInformation("Saved {File} ({Bytes} B)", target, bytes.Length);
    }

    public IDisposable AddSaveTransform(SaveTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        lock (_saveTransforms) _saveTransforms.Add(transform);
        return new Unregister(() => { lock (_saveTransforms) _saveTransforms.Remove(transform); });
    }

    private sealed class Unregister(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    public async Task<byte[]> GetBytesForSaveAsync(IPdfDocumentSession session, CancellationToken ct = default)
    {
        var snapshot = session.Current;
        var plain = snapshot.GetBytes();
        SaveTransform[] transforms;
        lock (_saveTransforms) transforms = _saveTransforms.ToArray();
        foreach (var transform in transforms)
        {
            ct.ThrowIfCancellationRequested();
            var input = plain;
            plain = await Task.Run(() => transform(session, input, ct), ct) ?? input;
        }
        if (snapshot.Encryption is null) return plain;
        var security = Security;
        if (security is not null)
        {
            try { return await security.EncryptAsync(plain, snapshot.Encryption, ct); }
            catch (NotImplementedException) { }
        }
        return await Task.Run(() => PdfSecurityHelper.Encrypt(plain, snapshot.Encryption), ct);
    }

    public async Task RevertAsync(IPdfDocumentSession session, CancellationToken ct = default)
    {
        if (session.FilePath is null) throw new InvalidOperationException("Document was never saved.");
        var original = await File.ReadAllBytesAsync(session.FilePath, ct);
        var loaded = await LoadAsync(original, session.DisplayName, session.SecurityInfo.Password, ct);
        if (loaded is null) return;
        if (session is PdfDocumentSession s)
            await s.ResetAsync(loaded.Value.Snapshot, loaded.Value.Info, loaded.Value.Permissions, ct);
    }

    public void Close(IPdfDocumentSession session)
    {
        int index = _documents.IndexOf(session);
        if (index < 0) return;
        _documents.RemoveAt(index);
        if (ReferenceEquals(_active, session))
            ActiveDocument = _documents.Count == 0 ? null : _documents[Math.Min(index, _documents.Count - 1)];
        DocumentClosed?.Invoke(this, session);
        (session as IDisposable)?.Dispose();
    }

    // ------------------------------------------------------------------ wczytywanie

    private readonly record struct Loaded(DocumentSnapshot Snapshot, PdfSecurityInfo Info, DocumentPermissions Permissions);

    private async Task<Loaded?> LoadAsync(byte[] original, string displayName, string? password, CancellationToken ct)
    {
        bool retry = false;
        while (true)
        {
            var probe = await ProbeAsync(original, password, ct);
            switch (probe.Status)
            {
                case PdfProbeStatus.Ok:
                    return await DecryptAndAnalyzeAsync(original, probe, password, ct);
                case PdfProbeStatus.Corrupt:
                    throw new PdfCorruptException(probe.ErrorMessage ?? "The file is damaged or is not a PDF document.");
                case PdfProbeStatus.PasswordRequired:
                case PdfProbeStatus.InvalidPassword:
                    var dialogs = Dialogs;
                    if (dialogs is null)
                        throw probe.Status == PdfProbeStatus.InvalidPassword ? new PdfInvalidPasswordException() : new PdfPasswordRequiredException();
                    password = dialogs.AskPassword(displayName, retry || probe.Status == PdfProbeStatus.InvalidPassword);
                    if (password is null) return null;
                    retry = true;
                    break;
            }
        }
    }

    private async Task<PdfProbeResult> ProbeAsync(byte[] pdf, string? password, CancellationToken ct)
    {
        var renderer = Renderer;
        if (renderer is not null)
        {
            try { return await renderer.ProbeAsync(pdf, password, ct); }
            catch (NotImplementedException) { }
        }
        return await Task.Run(() => ProbeWithPdfSharp(pdf, password), ct);
    }

    internal static PdfProbeResult ProbeWithPdfSharp(byte[] pdf, string? password)
    {
        PdfSharpEdit.EnsureInitialized();
        if (pdf.Length < 8 || !System.Text.Encoding.ASCII.GetString(pdf, 0, Math.Min(1024, pdf.Length)).Contains("%PDF-", StringComparison.Ordinal))
            return new PdfProbeResult(PdfProbeStatus.Corrupt, 0, false, -1, DocumentPermissions.None, "Missing %PDF header.");
        try
        {
            using var doc = password is null
                ? PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Import)
                : PdfReader.Open(new MemoryStream(pdf, false), password, PdfDocumentOpenMode.Import);
            bool encrypted = doc.SecurityHandler.Elements.ContainsKey("/Filter");
            var perms = encrypted ? PdfSecurityHelper.FromPValue(doc.SecurityHandler.Elements.GetInteger("/P")) : DocumentPermissions.All;
            return new PdfProbeResult(PdfProbeStatus.Ok, doc.PageCount, encrypted, doc.SecurityHandler.Elements.GetInteger("/R"), perms);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return new PdfProbeResult(password is null ? PdfProbeStatus.PasswordRequired : PdfProbeStatus.InvalidPassword, 0, true, -1, DocumentPermissions.None, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PdfProbeResult(PdfProbeStatus.Corrupt, 0, false, -1, DocumentPermissions.None, ex.Message);
        }
    }

    private async Task<Loaded> DecryptAndAnalyzeAsync(byte[] original, PdfProbeResult probe, string? password, CancellationToken ct)
    {
        if (!probe.IsEncrypted)
            return new Loaded(DocumentSnapshot.FromBytes(original), PdfSecurityInfo.None, DocumentPermissions.All);

        // 1) analiza zabezpieczeń
        PdfSecurityInfo? info = null;
        var security = Security;
        if (security is not null)
        {
            try { info = await security.AnalyzeAsync(original, password, ct); }
            catch (NotImplementedException) { }
        }
        info ??= await Task.Run(() => PdfSecurityHelper.Analyze(original, password), ct);

        // 2) odszyfrowanie
        byte[]? plain = null;
        var renderer = Renderer;
        if (renderer is not null)
        {
            try { plain = await renderer.RemoveSecurityAsync(original, password ?? string.Empty, ct); }
            catch (NotImplementedException) { }
        }
        plain ??= await Task.Run(() => PdfSecurityHelper.RemoveSecurity(original, password), ct);

        // 3) ustawienia ponownego szyfrowania przy zapisie
        PdfEncryptionSettings? resave = null;
        bool resaveKnown = false;
        if (security is not null)
        {
            try { resave = security.CreateResaveSettings(info); resaveKnown = true; }
            catch (NotImplementedException) { }
        }
        if (!resaveKnown)
            resave = PdfSecurityHelper.CreateResaveSettings(info, PdfSecurityHelper.HasEmptyUserPassword(original));

        var effective = info.OpenedWithOwnerPassword ? DocumentPermissions.All : info.FilePermissions;
        return new Loaded(DocumentSnapshot.FromBytes(plain, resave), info, effective);
    }

    private PdfDocumentSession CreateSession(string? path, string name, Loaded loaded, bool startModified)
    {
        var storage = new SnapshotStorageOptions(Path.Combine(_paths.TempDirectory, "snapshots"));
        var session = new PdfDocumentSession(path, name, loaded.Snapshot, loaded.Info, loaded.Permissions, storage, _settings.Current.UndoLevels, startModified);
        _documents.Add(session);
        DocumentOpened?.Invoke(this, session);
        ActiveDocument = session;
        return session;
    }
}

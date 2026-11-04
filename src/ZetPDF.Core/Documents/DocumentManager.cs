using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf.IO;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

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

        var stamp = FileStamp.Of(full);
        var original = await File.ReadAllBytesAsync(full, ct);
        var loaded = await LoadAsync(original, Path.GetFileName(full), password, ct);
        if (loaded is null) return null;

        var session = CreateSession(full, Path.GetFileName(full), loaded.Value, startModified: false);
        _fileStates.AddOrUpdate(session, new FileState(stamp, ContainsSignature(original)));
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
        var target = path ?? session.FilePath ?? throw new InvalidOperationException("Document has no file path – use Save As.").WithMessageKey("Core.Error.NoFilePath");
        target = Path.GetFullPath(target);
        // Zapisana wersja to ten zrzut – edycje wykonane w trakcie zapisu zostają niezapisane.
        var snapshot = session.Current;
        bool wasModified = session.IsModified;
        var bytes = await GetBytesForSaveAsync(session, snapshot, ct);
        await _files.WriteAllBytesSafeAsync(target, bytes, keepBackup: false, ct);
        if (session is PdfDocumentSession s) s.MarkSaved(target, snapshot.Version);
        // Zapis zmian przepisuje plik, więc wcześniejsze podpisy przestają obowiązywać.
        bool stillSigned = HasSignatures(session) && !wasModified && snapshot.Encryption is null;
        _fileStates.AddOrUpdate(session, new FileState(FileStamp.Of(target), stillSigned));
        _recent.Add(target);
        _log.LogInformation("Saved {File} ({Bytes} B)", target, bytes.Length);
    }

    // ------------------------------------------------------------------ stan pliku na dysku

    private readonly record struct FileStamp(long Length, long ModifiedUtcTicks)
    {
        public static FileStamp Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : default;
        }
    }

    private sealed record FileState(FileStamp Stamp, bool Signed);

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPdfDocumentSession, FileState> _fileStates = new();

    /// <summary>Czy plik zmienił się na dysku od otwarcia lub ostatniego zapisu (np. inny program, synchronizacja z chmurą).</summary>
    public bool HasChangedOnDisk(IPdfDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.FilePath is not { } path || !_fileStates.TryGetValue(session, out var state) || !File.Exists(path)) return false;
        return FileStamp.Of(path) != state.Stamp;
    }

    /// <summary>Czy plik na dysku ma podpisy cyfrowe, które zapis zmian by unieważnił.</summary>
    public bool HasSignatures(IPdfDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _fileStates.TryGetValue(session, out var state) && state.Signed;
    }

    /// <summary>/ByteRange występuje tylko w słownikach podpisu; nazwy PDF nie są szyfrowane, więc działa też dla plików z hasłem.</summary>
    private static bool ContainsSignature(byte[] pdf) => pdf.AsSpan().IndexOf("/ByteRange"u8) >= 0;

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

    /// <summary>Stan pliku oryginalnego do opisu kopii odzyskiwania.</summary>
    public (long Length, long ModifiedUtcTicks, bool Signed) GetRecoverySource(IPdfDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _fileStates.TryGetValue(session, out var state) ? (state.Stamp.Length, state.Stamp.ModifiedUtcTicks, state.Signed) : default;
    }

    // ------------------------------------------------------------------ odzyskiwanie po awarii (DECYZJE D-27)

    /// <summary>
    /// Otwiera kopię odzyskiwania jako dokument zmodyfikowany z oryginalną ścieżką. Dla dokumentu chronionego ustawienia szyfrowania
    /// powstają z pliku oryginalnego (pyta o hasło jak przy otwieraniu), a gdy się nie da – <paramref name="askNewPassword"/> pyta
    /// o nowe hasło otwarcia. Zwraca null, gdy nie ustalono ochrony: dokumentu nie wolno wtedy otworzyć bez niej (kopia zostaje).
    /// </summary>
    public async Task<IPdfDocumentSession?> OpenRecoveredAsync(RecoveryInfo info, byte[] pdf, Func<string, string?> askNewPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(pdf);
        var path = info.OriginalPath;
        if (path is not null && _documents.Any(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            path = null; // ten plik jest już otwarty w innej karcie – kopia dostaje własną kartę, zapis przez Zapisz jako

        Loaded loaded = new(DocumentSnapshot.FromBytes(pdf), PdfSecurityInfo.None, info.Permissions);
        if (info.IsProtected)
        {
            var resolved = await ResolveRecoveredProtectionAsync(info, pdf, askNewPassword, ct);
            if (resolved is null) return null;
            loaded = resolved.Value;
        }

        var session = CreateSession(path, info.DisplayName, loaded, startModified: true);
        if (path is not null)
        {
            // Stan pliku z chwili sprzed awarii: zmiana na dysku w międzyczasie i podpisy nadal ostrzegają przy zapisie.
            bool signed = info.SourceSigned;
            try { signed |= File.Exists(path) && ContainsSignature(await File.ReadAllBytesAsync(path, ct)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Cannot read original of recovered document"); }
            _fileStates.AddOrUpdate(session, new FileState(new FileStamp(info.SourceLength, info.SourceModifiedUtcTicks), signed));
        }
        _log.LogInformation("Recovered document {Id}", info.Id);
        return session;
    }

    private async Task<Loaded?> ResolveRecoveredProtectionAsync(RecoveryInfo info, byte[] pdf, Func<string, string?> askNewPassword, CancellationToken ct)
    {
        if (info.OriginalPath is { } path && File.Exists(path))
        {
            try
            {
                var original = await File.ReadAllBytesAsync(path, ct);
                var fromFile = await LoadAsync(original, info.DisplayName, null, ct);
                if (fromFile is { Info.IsEncrypted: true } f)
                    return new Loaded(DocumentSnapshot.FromBytes(pdf, f.Snapshot.Encryption), f.Info, f.Permissions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfCorruptException)
            {
                _log.LogWarning(ex, "Cannot read protection from the original of recovered document");
            }
        }
        var password = askNewPassword(info.DisplayName);
        if (string.IsNullOrEmpty(password)) return null;
        var settings = new PdfEncryptionSettings(password, PdfSecurityHelper.GenerateRandomPassword(), PdfEncryptionMethod.Aes256, info.Permissions) { GeneratedOwnerPassword = true };
        var security = new PdfSecurityInfo(true, PdfEncryptionMethod.Aes256, 6, info.Permissions, info.Permissions == DocumentPermissions.All, password);
        return new Loaded(DocumentSnapshot.FromBytes(pdf, settings), security, info.Permissions);
    }

    // ------------------------------------------------------------------ D-23: plik z samym hasłem uprawnień

    private sealed record EncryptionOverride(PdfEncryptionSettings Generated, PdfEncryptionSettings? Settings);

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPdfDocumentSession, EncryptionOverride> _overrides = new();

    /// <summary>
    /// Czy przed zapisem trzeba zapytać użytkownika o ochronę (DECYZJE D-23): plik ma tylko hasło uprawnień
    /// (puste hasło otwarcia), otwarto go bez hasła właściciela, więc ustawienia zapisu mają hasło właściciela
    /// wygenerowane przez ZetPDF – a użytkownik jeszcze nie wybrał, co z tym zrobić.
    /// </summary>
    public bool RequiresOwnerPasswordDecision(IPdfDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var enc = session.Current.Encryption;
        if (enc is not { GeneratedOwnerPassword: true } || !string.IsNullOrEmpty(enc.UserPassword)) return false;
        return !(_overrides.TryGetValue(session, out var o) && Equals(o.Generated, enc));
    }

    /// <summary>
    /// Wybór użytkownika dla <see cref="RequiresOwnerPasswordDecision"/>: ustawienia szyfrowania użyte przy zapisie
    /// zamiast wygenerowanych (null = zapis bez ochrony). Nie zmienia historii dokumentu; obowiązuje, dopóki
    /// ustawienia szyfrowania sesji się nie zmienią (np. Przywróć tworzy nowe – wtedy pytanie wraca).
    /// </summary>
    public void SetSaveEncryptionOverride(IPdfDocumentSession session, PdfEncryptionSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(session);
        var enc = session.Current.Encryption ?? throw new InvalidOperationException("Document has no encryption settings.");
        _overrides.AddOrUpdate(session, new EncryptionOverride(enc, settings));
    }

    /// <summary>Ustawienia szyfrowania, które zostaną zastosowane przy zapisie (z uwzględnieniem wyboru D-23).</summary>
    public PdfEncryptionSettings? GetEffectiveSaveEncryption(IPdfDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var enc = session.Current.Encryption;
        if (enc is not null && _overrides.TryGetValue(session, out var o) && Equals(o.Generated, enc)) return o.Settings;
        return enc;
    }

    public Task<byte[]> GetBytesForSaveAsync(IPdfDocumentSession session, CancellationToken ct = default) =>
        GetBytesForSaveAsync(session, session.Current, ct);

    private async Task<byte[]> GetBytesForSaveAsync(IPdfDocumentSession session, DocumentSnapshot snapshot, CancellationToken ct)
    {
        var encryption = GetEffectiveSaveEncryption(session);
        var plain = snapshot.GetBytes();
        SaveTransform[] transforms;
        lock (_saveTransforms) transforms = _saveTransforms.ToArray();
        foreach (var transform in transforms)
        {
            ct.ThrowIfCancellationRequested();
            var input = plain;
            plain = await Task.Run(() => transform(session, input, ct), ct) ?? input;
        }
        if (encryption is null) return plain;
        var security = Security;
        if (security is not null)
        {
            try { return await security.EncryptAsync(plain, encryption, ct); }
            catch (NotImplementedException) { }
        }
        return await Task.Run(() => PdfSecurityHelper.Encrypt(plain, encryption), ct);
    }

    public async Task RevertAsync(IPdfDocumentSession session, CancellationToken ct = default)
    {
        if (session.FilePath is null) throw new InvalidOperationException("Document was never saved.").WithMessageKey("Core.Error.NeverSaved");
        var stamp = FileStamp.Of(session.FilePath);
        var original = await File.ReadAllBytesAsync(session.FilePath, ct);
        var loaded = await LoadAsync(original, session.DisplayName, session.SecurityInfo.Password, ct);
        if (loaded is null) return;
        _fileStates.AddOrUpdate(session, new FileState(stamp, ContainsSignature(original)));
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

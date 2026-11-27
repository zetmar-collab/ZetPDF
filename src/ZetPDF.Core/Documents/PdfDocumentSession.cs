using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Documents;

/// <summary>Gdzie i ile zrzutów trzymać w pamięci.</summary>
public sealed record SnapshotStorageOptions(string TempDirectory, long MemoryBudgetBytes = 256L * 1024 * 1024);

/// <summary>
/// Implementacja <see cref="IPdfDocumentSession"/>. Edycje serializowane semaforem; polecenie wykonuje się
/// w Task.Run; zdarzenia podnoszone na kontekście synchronizacji z chwili utworzenia (wątek UI).
/// </summary>
public sealed class PdfDocumentSession : IPdfDocumentSession, IDisposable
{
    private readonly SemaphoreSlim _editLock = new(1, 1);
    private readonly SynchronizationContext? _ui;
    private readonly UndoRedoHistory _history;
    private DocumentSnapshot _current;
    private long _savedVersion;
    // Zamknięcie może przyjść w trakcie polecenia (okno postępu pojawia się z opóźnieniem, komentarze idą bez niego):
    // semafora nie zwalniamy, a historię zwalnia ten, kto ostatni trzyma blokadę edycji.
    private volatile bool _disposed;
    private int _released;

    public PdfDocumentSession(
        string? filePath,
        string displayName,
        DocumentSnapshot initial,
        PdfSecurityInfo securityInfo,
        DocumentPermissions permissions,
        SnapshotStorageOptions storage,
        int undoLimit = 30,
        bool startModified = false)
    {
        FilePath = filePath;
        DisplayName = displayName;
        _current = initial;
        SecurityInfo = securityInfo;
        Permissions = permissions;
        _ui = SynchronizationContext.Current;
        _history = new UndoRedoHistory(this, storage, undoLimit);
        _savedVersion = startModified ? -1 : initial.Version;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string? FilePath { get; private set; }
    public string DisplayName { get; private set; }
    public DocumentSnapshot Current => _current;
    public bool IsModified => _current.Version != _savedVersion;
    public PdfSecurityInfo SecurityInfo { get; private set; }
    public DocumentPermissions Permissions { get; private set; }
    public IUndoRedoService History => _history;

    public event EventHandler<DocumentChangedEventArgs>? Changed;
    public event EventHandler? StateChanged;

    public async Task<bool> ExecuteAsync(IDocumentCommand command, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((Permissions & command.RequiredPermission) != command.RequiredPermission)
            throw new DocumentPermissionException(command.RequiredPermission);

        bool wasModified;
        DocumentEditResult? result;
        await _editLock.WaitAsync(ct);
        try
        {
            if (_disposed) return false;
            var input = _current;
            var context = new DocumentEditContext(this, input, progress);
            result = await Task.Run(() => command.ExecuteAsync(context, ct), ct);
            if (result is null || _disposed) return false; // dokument zamknięty w trakcie – wynik odrzucony
            ct.ThrowIfCancellationRequested();
            wasModified = IsModified;
            _history.Push(input, result.Output, command.DescriptionKey, result.Kind, result.AffectedPages);
            _current = result.Output;
        }
        finally
        {
            ReleaseEditLock();
        }

        if (_disposed) return false;
        Raise(new DocumentChangedEventArgs(result.Kind, result.AffectedPages, DocumentChangeSource.Command));
        if (wasModified != IsModified) RaiseState();
        return true;
    }

    /// <summary>Wołane przez historię (pod blokadą edycji).</summary>
    internal async Task ApplyHistoryAsync(Func<(DocumentSnapshot Target, DocumentChangeKind Kind)?> step, DocumentChangeSource source, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DocumentChangeKind kind;
        bool wasModified;
        await _editLock.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            var r = step();
            if (r is null) return;
            wasModified = IsModified;
            _current = r.Value.Target;
            kind = r.Value.Kind;
        }
        finally
        {
            ReleaseEditLock();
        }
        if (_disposed) return;
        Raise(new DocumentChangedEventArgs(kind, null, source));
        if (wasModified != IsModified) RaiseState();
    }

    internal IEnumerable<DocumentSnapshot> LiveSnapshots() { yield return _current; }

    /// <summary>Po zapisie: zapamiętuje zapisaną wersję (tę z początku zapisu), ścieżkę i nazwę.</summary>
    internal void MarkSaved(string path, long savedVersion)
    {
        _savedVersion = savedVersion;
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        RaiseState();
    }

    /// <summary>Przywróć / ponowne wczytanie: nowy stan bazowy, czyści historię.</summary>
    internal async Task ResetAsync(DocumentSnapshot snapshot, PdfSecurityInfo info, DocumentPermissions permissions, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _editLock.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            _history.ClearInternal();
            _current = snapshot;
            _savedVersion = snapshot.Version;
            SecurityInfo = info;
            Permissions = permissions;
        }
        finally
        {
            ReleaseEditLock();
        }
        if (_disposed) return;
        Raise(new DocumentChangedEventArgs(DocumentChangeKind.All, null, DocumentChangeSource.Revert));
        RaiseState();
    }

    private void Raise(DocumentChangedEventArgs e) => OnUi(() => Changed?.Invoke(this, e));
    private void RaiseState() => OnUi(() => StateChanged?.Invoke(this, EventArgs.Empty));
    internal void RaiseHistoryOnUi(Action a) => OnUi(a);

    private void OnUi(Action a)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) a();
        else _ui.Send(_ => a(), null);
    }

    /// <summary>Czy trwa polecenie lub krok historii (blokada edycji zajęta).</summary>
    internal bool IsBusy => _editLock.CurrentCount == 0;

    /// <summary>
    /// Zamknięcie. Gdy trwa polecenie, zrzuty zwalnia ono samo po zakończeniu (jego wynik jest odrzucany), więc ani
    /// semafor, ani historia nie znikają spod działającego kodu.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_editLock.Wait(0)) ReleaseEditLock();
    }

    private void ReleaseEditLock()
    {
        _editLock.Release();
        if (!_disposed || Volatile.Read(ref _released) != 0) return;
        if (!_editLock.Wait(0)) return; // inny oczekujący – zwolni po sobie
        try
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _history.ReleaseAll();
        }
        finally
        {
            _editLock.Release();
        }
    }
}

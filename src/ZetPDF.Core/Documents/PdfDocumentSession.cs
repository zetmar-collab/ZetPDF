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
    private bool _disposed;

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
            var input = _current;
            var context = new DocumentEditContext(this, input, progress);
            result = await Task.Run(() => command.ExecuteAsync(context, ct), ct);
            if (result is null) return false;
            ct.ThrowIfCancellationRequested();
            wasModified = IsModified;
            _history.Push(input, result.Output, command.DescriptionKey, result.Kind, result.AffectedPages);
            _current = result.Output;
        }
        finally
        {
            _editLock.Release();
        }

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
            var r = step();
            if (r is null) return;
            wasModified = IsModified;
            _current = r.Value.Target;
            kind = r.Value.Kind;
        }
        finally
        {
            _editLock.Release();
        }
        Raise(new DocumentChangedEventArgs(kind, null, source));
        if (wasModified != IsModified) RaiseState();
    }

    internal IEnumerable<DocumentSnapshot> LiveSnapshots() { yield return _current; }

    /// <summary>Po zapisie: zapamiętuje zapisaną wersję, ścieżkę i nazwę.</summary>
    internal void MarkSaved(string path)
    {
        _savedVersion = _current.Version;
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        RaiseState();
    }

    /// <summary>Przywróć / ponowne wczytanie: nowy stan bazowy, czyści historię.</summary>
    internal async Task ResetAsync(DocumentSnapshot snapshot, PdfSecurityInfo info, DocumentPermissions permissions, CancellationToken ct)
    {
        await _editLock.WaitAsync(ct);
        try
        {
            _history.ClearInternal();
            _current = snapshot;
            _savedVersion = snapshot.Version;
            SecurityInfo = info;
            Permissions = permissions;
        }
        finally
        {
            _editLock.Release();
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _history.ReleaseAll();
        _editLock.Dispose();
    }
}

using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Documents;

/// <summary>
/// Historia Cofnij/Ponów oparta na zrzutach. Wpis = (przed, po, opis, rodzaj zmiany).
/// Cofnij przywraca "przed", Ponów – "po" (bez ponownego wykonywania polecenia).
/// Limit liczby poziomów + budżet pamięci: najstarsze zrzuty spoza budżetu trafiają do plików tymczasowych.
/// </summary>
internal sealed class UndoRedoHistory : IUndoRedoService
{
    private readonly PdfDocumentSession _session;
    private readonly SnapshotStorageOptions _storage;
    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    private readonly object _gate = new();
    private int _limit;

    public UndoRedoHistory(PdfDocumentSession session, SnapshotStorageOptions storage, int limit)
    {
        _session = session;
        _storage = storage;
        _limit = Math.Max(1, limit);
    }

    private sealed record Entry(DocumentSnapshot Before, DocumentSnapshot After, string DescriptionKey, DocumentChangeKind Kind);

    public bool CanUndo { get { lock (_gate) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count > 0; } }
    public string? UndoDescriptionKey { get { lock (_gate) return _undo.Count > 0 ? _undo[^1].DescriptionKey : null; } }
    public string? RedoDescriptionKey { get { lock (_gate) return _redo.Count > 0 ? _redo[^1].DescriptionKey : null; } }

    public int Limit
    {
        get => _limit;
        set
        {
            lock (_gate)
            {
                _limit = Math.Max(1, value);
                TrimLocked();
            }
        }
    }

    public event EventHandler? HistoryChanged;

    public void Push(DocumentSnapshot before, DocumentSnapshot after, string descriptionKey, DocumentChangeKind kind, IReadOnlyList<int>? pages)
    {
        lock (_gate)
        {
            var dropped = _redo.ToList();
            _redo.Clear();
            _undo.Add(new Entry(before, after, descriptionKey, kind));
            TrimLocked();
            ReleaseUnreferencedLocked(dropped.SelectMany(e => new[] { e.Before, e.After }), after);
            EnforceBudgetLocked(after);
        }
        RaiseChanged();
    }

    public async Task UndoAsync(CancellationToken ct = default)
    {
        await _session.ApplyHistoryAsync(() =>
        {
            lock (_gate)
            {
                if (_undo.Count == 0) return null;
                var e = _undo[^1];
                _undo.RemoveAt(_undo.Count - 1);
                _redo.Add(e);
                return (e.Before, e.Kind);
            }
        }, DocumentChangeSource.Undo, ct);
        RaiseChanged();
    }

    public async Task RedoAsync(CancellationToken ct = default)
    {
        await _session.ApplyHistoryAsync(() =>
        {
            lock (_gate)
            {
                if (_redo.Count == 0) return null;
                var e = _redo[^1];
                _redo.RemoveAt(_redo.Count - 1);
                _undo.Add(e);
                return (e.After, e.Kind);
            }
        }, DocumentChangeSource.Redo, ct);
        RaiseChanged();
    }

    public void Clear()
    {
        ClearInternal();
        RaiseChanged();
    }

    internal void ClearInternal()
    {
        lock (_gate)
        {
            var all = _undo.Concat(_redo).SelectMany(e => new[] { e.Before, e.After }).ToList();
            _undo.Clear();
            _redo.Clear();
            ReleaseUnreferencedLocked(all, _session.Current);
        }
    }

    internal void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var s in _undo.Concat(_redo).SelectMany(e => new[] { e.Before, e.After })) s.Release();
            _session.Current.Release();
            _undo.Clear();
            _redo.Clear();
        }
    }

    /// <summary>Liczba zrzutów trzymanych w pamięci (diagnostyka/testy).</summary>
    internal int InMemoryCount
    {
        get { lock (_gate) return Live().Where(s => s.IsInMemory).Distinct().Count(); }
    }

    private IEnumerable<DocumentSnapshot> Live()
    {
        foreach (var e in _undo) { yield return e.Before; yield return e.After; }
        foreach (var e in _redo) { yield return e.Before; yield return e.After; }
        yield return _session.Current;
    }

    private void TrimLocked()
    {
        var dropped = new List<DocumentSnapshot>();
        while (_undo.Count > _limit)
        {
            var e = _undo[0];
            _undo.RemoveAt(0);
            dropped.Add(e.Before);
            dropped.Add(e.After);
        }
        ReleaseUnreferencedLocked(dropped, null);
    }

    private void ReleaseUnreferencedLocked(IEnumerable<DocumentSnapshot> candidates, DocumentSnapshot? alsoLive)
    {
        var live = Live().ToList();
        if (alsoLive is not null) live.Add(alsoLive);
        foreach (var c in candidates.Distinct())
        {
            if (live.Any(l => ReferenceEquals(l, c) || l.SharesDataWith(c))) continue;
            c.Release();
        }
    }

    private void EnforceBudgetLocked(DocumentSnapshot newest)
    {
        // Najnowsze zrzuty zostają w pamięci, starsze (od początku listy cofania) wypadają na dysk.
        long budget = _storage.MemoryBudgetBytes;
        var ordered = new List<DocumentSnapshot> { newest, _session.Current };
        for (int i = _undo.Count - 1; i >= 0; i--) { ordered.Add(_undo[i].After); ordered.Add(_undo[i].Before); }
        foreach (var e in _redo) { ordered.Add(e.Before); ordered.Add(e.After); }

        long used = 0;
        var seen = new HashSet<DocumentSnapshot>(ReferenceEqualityComparer.Instance);
        bool protect = _session.SecurityInfo.IsEncrypted || newest.Encryption is not null;
        foreach (var s in ordered)
        {
            if (!seen.Add(s) || !s.IsInMemory) continue;
            used += s.Length;
            if (used > budget && !ReferenceEquals(s, newest) && !ReferenceEquals(s, _session.Current))
                s.SpillToDisk(_storage.TempDirectory, protect);
        }
    }

    private void RaiseChanged() => _session.RaiseHistoryOnUi(() => HistoryChanged?.Invoke(this, EventArgs.Empty));
}


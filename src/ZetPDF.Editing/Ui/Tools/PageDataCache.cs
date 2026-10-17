using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Ui.Tools;

/// <summary>Asynchroniczna pamięć danych strony dla nakładek (klucz: dokument, wersja zrzutu, strona). Po wczytaniu odświeża nakładki widoku.</summary>
internal sealed class PageDataCache<T> where T : class
{
    private readonly Func<byte[], int, Task<T>> _load;
    private readonly Dictionary<(Guid, long, int), Task<T>> _items = new();

    public PageDataCache(Func<byte[], int, Task<T>> load) => _load = load;

    /// <summary>Dane, jeśli już gotowe; inaczej rozpoczyna wczytywanie i zwraca null.</summary>
    public T? TryGet(IDocumentView view, int page)
    {
        var task = GetAsync(view, page);
        if (task.IsCompletedSuccessfully) return task.Result;
        if (!task.IsCompleted) _ = task.ContinueWith(_ => view.InvalidateOverlays(), TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    public Task<T> GetAsync(IDocumentView view, int page)
    {
        var s = view.Session;
        var key = (s.Id, s.Current.Version, page);
        if (_items.TryGetValue(key, out var t) && !t.IsFaulted) return t;
        if (_items.Count > 64) _items.Clear();
        var bytes = s.Current.GetBytes();
        t = Task.Run(() => _load(bytes, page));
        _items[key] = t;
        return t;
    }

    public void Clear() => _items.Clear();
}

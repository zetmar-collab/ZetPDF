using System.Windows.Media.Imaging;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.View;

/// <summary>Wyrenderowany fragment strony: bitmapa (zamrożona) + parametry, z którymi powstała.</summary>
public sealed record PageBitmap(int PageIndex, int ViewRotation, double PixelScale, PixelRect Region, int FullWidth, int FullHeight, BitmapSource Bitmap)
{
    public long Bytes => (long)Bitmap.PixelWidth * Bitmap.PixelHeight * 4;
    public bool IsFullPage => Region.X == 0 && Region.Y == 0 && Region.Width >= FullWidth && Region.Height >= FullHeight;
}

/// <summary>
/// Pamięć podręczna LRU bitmap stron z limitem bajtów (nie liczby elementów).
/// Klucz = (strona, rodzaj) – dla każdej strony trzymamy co najwyżej jedną bitmapę danego rodzaju
/// (np. podgląd niskiej rozdzielczości i bitmapę ostrą). Dostęp tylko z wątku UI.
/// </summary>
public sealed class RenderCache
{
    private readonly LinkedList<(int Page, int Kind, PageBitmap Value)> _lru = new();
    private readonly Dictionary<(int, int), LinkedListNode<(int Page, int Kind, PageBitmap Value)>> _map = new();

    public RenderCache(long budgetBytes) => BudgetBytes = budgetBytes;

    public long BudgetBytes { get; }
    public long UsedBytes { get; private set; }
    public int Count => _map.Count;

    public PageBitmap? Get(int page, int kind)
    {
        if (!_map.TryGetValue((page, kind), out var node)) return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Value;
    }

    public PageBitmap? Peek(int page, int kind) => _map.TryGetValue((page, kind), out var node) ? node.Value.Value : null;

    public void Put(int page, int kind, PageBitmap value)
    {
        Remove(page, kind);
        var node = _lru.AddFirst((page, kind, value));
        _map[(page, kind)] = node;
        UsedBytes += value.Bytes;
        // usuwamy najdawniej używane, ale nigdy właśnie dodanego elementu
        while (UsedBytes > BudgetBytes && _lru.Last is { } last && last != node)
        {
            _lru.RemoveLast();
            _map.Remove((last.Value.Page, last.Value.Kind));
            UsedBytes -= last.Value.Value.Bytes;
        }
    }

    public void Remove(int page, int kind)
    {
        if (_map.Remove((page, kind), out var node))
        {
            _lru.Remove(node);
            UsedBytes -= node.Value.Value.Bytes;
        }
    }

    public void Clear()
    {
        _lru.Clear();
        _map.Clear();
        UsedBytes = 0;
    }
}

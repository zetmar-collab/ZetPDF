namespace ZetPDF.Security.Compare;

/// <summary>Operacja skryptu edycji: Equal (A[i]=B[j]), Delete (A[i]), Insert (B[j]).</summary>
internal enum DiffOp { Equal, Delete, Insert }

internal readonly record struct DiffEdit(DiffOp Op, int IndexA, int IndexB);

/// <summary>
/// Algorytm różnic Myersa (1986) w wersji o liniowej pamięci (środkowy wąż, dziel i zwyciężaj) –
/// własna implementacja (bez zewnętrznych bibliotek). Wejście: ciągi liczb (np. identyfikatory słów).
/// </summary>
internal static class MyersDiff
{
    public static List<DiffEdit> Diff(IReadOnlyList<int> a, IReadOnlyList<int> b, CancellationToken ct = default)
    {
        var result = new List<DiffEdit>(a.Count + b.Count);
        int lo = 0, aHi = a.Count, bHi = b.Count;
        while (lo < aHi && lo < bHi && a[lo] == b[lo]) lo++;
        int suffix = 0;
        while (aHi - suffix > lo && bHi - suffix > lo && a[aHi - suffix - 1] == b[bHi - suffix - 1]) suffix++;
        for (int i = 0; i < lo; i++) result.Add(new DiffEdit(DiffOp.Equal, i, i));

        var path = FindPath(a, b, lo, lo, aHi - suffix, bHi - suffix, ct);
        if (path is not null)
        {
            for (int p = 0; p + 1 < path.Count; p++)
            {
                var (x1, y1) = path[p];
                var (x2, y2) = path[p + 1];
                (x1, y1) = WalkDiagonal(a, b, x1, y1, x2, y2, result);
                int dx = x2 - x1, dy = y2 - y1;
                if (dx < dy) { result.Add(new DiffEdit(DiffOp.Insert, x1, y1)); y1++; }
                else if (dx > dy) { result.Add(new DiffEdit(DiffOp.Delete, x1, y1)); x1++; }
                WalkDiagonal(a, b, x1, y1, x2, y2, result);
            }
        }
        else
        {
            for (int i = lo; i < aHi - suffix; i++) result.Add(new DiffEdit(DiffOp.Delete, i, lo));
            for (int j = lo; j < bHi - suffix; j++) result.Add(new DiffEdit(DiffOp.Insert, aHi - suffix, j));
        }
        for (int i = 0; i < suffix; i++) result.Add(new DiffEdit(DiffOp.Equal, aHi - suffix + i, bHi - suffix + i));
        return result;
    }

    private static (int, int) WalkDiagonal(IReadOnlyList<int> a, IReadOnlyList<int> b, int x1, int y1, int x2, int y2, List<DiffEdit> result)
    {
        while (x1 < x2 && y1 < y2 && a[x1] == b[y1])
        {
            result.Add(new DiffEdit(DiffOp.Equal, x1, y1));
            x1++; y1++;
        }
        return (x1, y1);
    }

    private static List<(int X, int Y)>? FindPath(IReadOnlyList<int> a, IReadOnlyList<int> b, int left, int top, int right, int bottom, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snake = MidPoint(a, b, left, top, right, bottom);
        if (snake is null) return null;
        var (start, finish) = snake.Value;
        var head = FindPath(a, b, left, top, start.X, start.Y, ct) ?? new List<(int, int)> { start };
        var tail = FindPath(a, b, finish.X, finish.Y, right, bottom, ct) ?? new List<(int, int)> { finish };
        head.AddRange(tail);
        return head;
    }

    private static ((int X, int Y) Start, (int X, int Y) Finish)? MidPoint(IReadOnlyList<int> a, IReadOnlyList<int> b, int left, int top, int right, int bottom)
    {
        int width = right - left, height = bottom - top;
        int size = width + height;
        if (size == 0) return null;
        int max = (size + 1) / 2;
        int off = max + 1;
        var vf = new int[2 * max + 3];
        var vb = new int[2 * max + 3];
        vf[1 + off] = left;
        vb[1 + off] = bottom;
        int delta = width - height;
        bool odd = (delta & 1) != 0;

        for (int d = 0; d <= max; d++)
        {
            // do przodu
            for (int k = d; k >= -d; k -= 2)
            {
                int c = k - delta;
                int px, x;
                if (k == -d || (k != d && vf[k - 1 + off] < vf[k + 1 + off])) { px = x = vf[k + 1 + off]; }
                else { px = vf[k - 1 + off]; x = px + 1; }
                int y = top + (x - left) - k;
                int py = (d == 0 || x != px) ? y : y - 1;
                while (x < right && y < bottom && a[x] == b[y]) { x++; y++; }
                vf[k + off] = x;
                if (odd && c >= -(d - 1) && c <= d - 1 && y >= vb[c + off])
                    return ((px, py), (x, y));
            }
            // wstecz
            for (int c = d; c >= -d; c -= 2)
            {
                int k = c + delta;
                int py, y;
                if (c == -d || (c != d && vb[c - 1 + off] > vb[c + 1 + off])) { py = y = vb[c + 1 + off]; }
                else { py = vb[c - 1 + off]; y = py - 1; }
                int x = left + (y - top) + k;
                int px = (d == 0 || y != py) ? x : x + 1;
                while (x > left && y > top && a[x - 1] == b[y - 1]) { x--; y--; }
                vb[c + off] = y;
                if (!odd && k >= -d && k <= d && x <= vf[k + off])
                    return ((x, y), (px, py));
            }
        }
        return null;
    }
}

using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Forms.Recognition;

/// <summary>Prostokąt w pikselach bitmapy, prawa i dolna krawędź poza prostokątem.</summary>
internal readonly record struct PxBox(int L, int T, int R, int B)
{
    public int Width => R - L;
    public int Height => B - T;
    public long Area => (long)Width * Height;
}

/// <summary>Spójny składnik z przebiegów pikseli: obwiednia, pole, czy dotyka brzegu bitmapy.</summary>
internal sealed record InkComponent(PxBox Box, long Area, bool TouchesBorder);

/// <summary>
/// Bitmapa strony po progowaniu i z sumami narastającymi (liczba ciemnych pikseli w prostokącie w O(1)). Rozmiar bitmapy ogranicza
/// <see cref="FieldRecognizer.ScaleFor"/>, więc pamięć nie zależy od rozmiaru strony zapisanego w pliku.
/// </summary>
internal sealed class InkMap
{
    /// <summary>Luminancja poniżej progu to tusz: jasne tło pola (szare, kremowe) nie zamyka prostokąta i nie jest treścią.</summary>
    public const int DarkThreshold = 170;

    private readonly int[] _sum;
    private readonly int _stride;

    private InkMap(int width, int height, int[] sum)
    {
        Width = width;
        Height = height;
        _sum = sum;
        _stride = width + 1;
    }

    public int Width { get; }
    public int Height { get; }

    public static InkMap From(RenderedBitmap bitmap)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Stride < bitmap.Width * 4 || bitmap.Bgra.Length < (long)bitmap.Stride * bitmap.Height)
            throw new ArgumentException("Invalid bitmap.", nameof(bitmap));
        int w = bitmap.Width, h = bitmap.Height, stride = w + 1;
        var sum = new int[checked((w + 1) * (h + 1))];
        var src = bitmap.Bgra;
        for (int y = 0; y < h; y++)
        {
            int row = y * bitmap.Stride, rowSum = 0, line = (y + 1) * stride, up = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                // ITU-R BT.601 na liczbach całkowitych; bitmapa ma nieprzezroczyste białe tło
                int lum = (29 * src[i] + 150 * src[i + 1] + 77 * src[i + 2]) >> 8;
                if (lum < DarkThreshold) rowSum++;
                sum[line + x + 1] = sum[up + x + 1] + rowSum;
            }
        }
        return new InkMap(w, h, sum);
    }

    /// <summary>Liczba ciemnych pikseli w prostokącie (przycięta do bitmapy).</summary>
    public int Count(int l, int t, int r, int b)
    {
        l = Math.Clamp(l, 0, Width);
        r = Math.Clamp(r, 0, Width);
        t = Math.Clamp(t, 0, Height);
        b = Math.Clamp(b, 0, Height);
        if (r <= l || b <= t) return 0;
        return _sum[b * _stride + r] - _sum[t * _stride + r] - _sum[b * _stride + l] + _sum[t * _stride + l];
    }

    public int Count(PxBox box) => Count(box.L, box.T, box.R, box.B);

    public bool IsDark(int x, int y) => Count(x, y, x + 1, y + 1) > 0;

    /// <summary>Czy w otoczeniu 3x3 jest tusz: zamyka szczeliny jednopikselowe w liniach skanu.</summary>
    public bool IsDarkDilated(int x, int y) => Count(x - 1, y - 1, x + 2, y + 2) > 0;

    /// <summary>
    /// Spójne składniki z poziomych przebiegów pikseli spełniających warunek. Zwraca null przy przekroczeniu limitu przebiegów
    /// (strona zaszumiona lub spreparowana), żeby pamięć była ograniczona.
    /// </summary>
    public List<InkComponent>? Components(Func<int, int, bool> on, int minRun, int maxGap, int rowSlack, int maxRuns)
    {
        var parent = new List<int>();
        var box = new List<(int L, int T, int R, int B, long Area, bool Border)>();
        var prev = new List<(int X0, int X1, int Id)>();
        var cur = new List<(int X0, int X1, int Id)>();
        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }
        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b) return;
            parent[b] = a;
            var (x, y) = (box[a], box[b]);
            box[a] = (Math.Min(x.L, y.L), Math.Min(x.T, y.T), Math.Max(x.R, y.R), Math.Max(x.B, y.B), x.Area + y.Area, x.Border || y.Border);
        }

        for (int y = 0; y < Height; y++)
        {
            cur.Clear();
            int x = 0;
            while (x < Width)
            {
                if (!on(x, y)) { x++; continue; }
                int start = x, end = x + 1, area = 1;
                x++;
                while (x < Width)
                {
                    if (on(x, y)) { end = x + 1; area++; x++; continue; }
                    if (maxGap <= 0 || x - end >= maxGap) break;
                    x++;
                }
                if (end - start < minRun) continue;
                if (parent.Count >= maxRuns) return null;
                int id = parent.Count;
                parent.Add(id);
                bool border = start == 0 || end == Width || y == 0 || y == Height - 1;
                box.Add((start, y, end, y + 1, area, border));
                cur.Add((start, end, id));
            }
            // obie listy są posortowane po X, więc scalenie jest liniowe
            int p = 0;
            foreach (var run in cur)
            {
                while (p < prev.Count && prev[p].X1 + rowSlack <= run.X0) p++;
                for (int q = p; q < prev.Count && prev[q].X0 - rowSlack < run.X1; q++) Union(prev[q].Id, run.Id);
            }
            (prev, cur) = (cur, prev);
        }

        var result = new List<InkComponent>();
        for (int i = 0; i < parent.Count; i++)
        {
            if (parent[i] != i) continue;
            var c = box[i];
            result.Add(new InkComponent(new PxBox(c.L, c.T, c.R, c.B), c.Area, c.Border));
        }
        return result;
    }
}

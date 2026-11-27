using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Utilities;

/// <summary>
/// Prostowanie przekrzywionych skanów przed OCR i rozpoznawaniem pól. Kąt szacowany z profilu rzutu ciemnych pikseli
/// (wiersze tekstu i linie formularza dają najostrzejszy profil przy właściwym kącie): najpierw co 0,5°, potem co 0,05° wokół
/// najlepszego. Obrót wokół środka strony, ten sam rozmiar bitmapy, poza stroną biel. Współrzędne wyników (słowa OCR,
/// prostokąty pól) wracają do układu oryginalnej bitmapy przez <see cref="ToSource"/>.
/// Kąt dodatni = wiersze opadają w prawo (oś Y bitmapy w dół).
/// </summary>
public static class Deskew
{
    public const double MaxAngle = 5.0;
    /// <summary>Poniżej tego kąta obraz zostaje bez zmian (obrót też trochę rozmywa).</summary>
    public const double MinAngle = 0.3;
    private const int MaxSamples = 250_000;

    /// <summary>Szacowany kąt w stopniach (0, gdy strona jest pusta lub kąta nie da się ustalić).</summary>
    public static double EstimateAngle(RenderedBitmap bmp, CancellationToken ct = default)
    {
        var points = DarkPoints(bmp, ct);
        if (points.Count < 200) return 0;
        double best = 0, bestScore = Score(points, 0, bmp.Width, bmp.Height);
        double baseline = bestScore;
        for (double a = -MaxAngle; a <= MaxAngle + 1e-9; a += 0.5)
        {
            ct.ThrowIfCancellationRequested();
            double s = Score(points, a, bmp.Width, bmp.Height);
            if (s > bestScore) { bestScore = s; best = a; }
        }
        double coarse = best;
        for (double a = coarse - 0.5; a <= coarse + 0.5 + 1e-9; a += 0.05)
        {
            ct.ThrowIfCancellationRequested();
            if (Math.Abs(a) > MaxAngle) continue;
            double s = Score(points, a, bmp.Width, bmp.Height);
            if (s > bestScore) { bestScore = s; best = a; }
        }
        // Profil prawie płaski (np. zdjęcie bez tekstu): bez prostowania.
        if (bestScore < baseline * 1.02) return 0;
        return Math.Round(best, 2);
    }

    /// <summary>Wyprostowana bitmapa (nowa) dla kąta z <see cref="EstimateAngle"/>; dla małego kąta – wejście.</summary>
    public static RenderedBitmap Rotate(RenderedBitmap src, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < MinAngle) return src;
        int w = src.Width, h = src.Height, stride = w * 4;
        var dst = new byte[stride * h];
        Array.Fill(dst, (byte)255);
        double rad = angleDegrees * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = w / 2.0, cy = h / 2.0;
        var s = src.Bgra;
        // Interpolacja dwuliniowa: cienkie linie formularza (1–2 px) nie rwą się po obrocie, jak przy najbliższym sąsiedzie.
        Parallel.For(0, h, v =>
        {
            double dy = v - cy;
            int o = v * stride;
            for (int u = 0; u < w; u++, o += 4)
            {
                double dx = u - cx;
                double x = cx + dx * cos - dy * sin, y = cy + dx * sin + dy * cos;
                int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                if (x0 < -1 || y0 < -1 || x0 >= w || y0 >= h) continue;
                double fx = x - x0, fy = y - y0;
                for (int c = 0; c < 4; c++)
                {
                    double p00 = Sample(s, src.Stride, w, h, x0, y0, c), p10 = Sample(s, src.Stride, w, h, x0 + 1, y0, c);
                    double p01 = Sample(s, src.Stride, w, h, x0, y0 + 1, c), p11 = Sample(s, src.Stride, w, h, x0 + 1, y0 + 1, c);
                    double top = p00 + (p10 - p00) * fx, bottom = p01 + (p11 - p01) * fx;
                    dst[o + c] = (byte)Math.Clamp(Math.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        });
        return new RenderedBitmap(w, h, stride, dst, src.Scale);
    }

    /// <summary>Składowa piksela; poza obrazem – biel (nieprzezroczysta).</summary>
    private static byte Sample(byte[] data, int stride, int w, int h, int x, int y, int c) =>
        (uint)x < (uint)w && (uint)y < (uint)h ? data[y * stride + x * 4 + c] : (byte)255;

    /// <summary>Punkt wyprostowanej bitmapy → punkt oryginalnej bitmapy.</summary>
    public static (double X, double Y) ToSource(double u, double v, int width, int height, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < MinAngle) return (u, v);
        double rad = angleDegrees * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = width / 2.0, cy = height / 2.0, dx = u - cx, dy = v - cy;
        return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    /// <summary>Punkt oryginalnej bitmapy → punkt wyprostowanej (odwrotność <see cref="ToSource"/>).</summary>
    public static (double U, double V) ToDeskewed(double x, double y, int width, int height, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < MinAngle) return (x, y);
        return ToSource(x, y, width, height, -angleDegrees);
    }

    /// <summary>Prostokąt wyprostowanej bitmapy → prostokąt otaczający w oryginalnej (L, T, R, B w pikselach).</summary>
    public static (double L, double T, double R, double B) RectToSource(double l, double t, double r, double b, int width, int height, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < MinAngle) return (l, t, r, b);
        var p = new[] { ToSource(l, t, width, height, angleDegrees), ToSource(r, t, width, height, angleDegrees), ToSource(r, b, width, height, angleDegrees), ToSource(l, b, width, height, angleDegrees) };
        return (p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
    }

    /// <summary>Prostokąt oryginalnej bitmapy → prostokąt otaczający w wyprostowanej.</summary>
    public static (double L, double T, double R, double B) RectToDeskewed(double l, double t, double r, double b, int width, int height, double angleDegrees) =>
        Math.Abs(angleDegrees) < MinAngle ? (l, t, r, b) : RectToSource(l, t, r, b, width, height, -angleDegrees);

    private static List<(float X, float Y)> DarkPoints(RenderedBitmap bmp, CancellationToken ct)
    {
        long total = (long)bmp.Width * bmp.Height;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(total / (double)(MaxSamples * 8))));
        var list = new List<(float, float)>();
        for (int y = 0; y < bmp.Height; y += step)
        {
            if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
            int row = y * bmp.Stride;
            for (int x = 0; x < bmp.Width; x += step)
            {
                int i = row + x * 4;
                if (i + 2 >= bmp.Bgra.Length) break;
                int luma = (bmp.Bgra[i + 2] * 299 + bmp.Bgra[i + 1] * 587 + bmp.Bgra[i] * 114) / 1000;
                if (luma < 128) list.Add((x, y));
                if (list.Count >= MaxSamples) return list;
            }
        }
        return list;
    }

    /// <summary>Suma kwadratów liczności wierszy po obrocie o kąt (im ostrzejszy profil, tym większa).</summary>
    private static double Score(List<(float X, float Y)> points, double angleDegrees, int width, int height)
    {
        double rad = angleDegrees * Math.PI / 180, tan = Math.Tan(rad);
        int pad = (int)Math.Ceiling(Math.Abs(tan) * width) + 2;
        var bins = new int[height + 2 * pad + 2];
        foreach (var (x, y) in points)
        {
            // wiersz opadający w prawo: y = y0 + x·tg(kąt) → y0 = y − x·tg(kąt)
            int key = (int)Math.Round(y - x * tan) + pad;
            if ((uint)key < (uint)bins.Length) bins[key]++;
        }
        double sum = 0;
        foreach (var n in bins) sum += (double)n * n;
        return sum;
    }
}

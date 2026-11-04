using System.Windows;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Forms.Recognition;

/// <summary>Skąd wzięła się propozycja pola.</summary>
public enum FieldProposalSource { Underline, Leader, Box, Comb, CheckBox }

/// <summary>Powód pominięcia strony przekraczającej limity analizy (dane z PDF są niezaufane).</summary>
public enum FieldPageSkipReason { TooManyWords, TooManyLines, TooManyCells, TooManyRuns, TooManyProposals, TooManyWidgets }

/// <summary>Propozycja pola na stronie. Współrzędne w punktach strony.</summary>
public sealed record FieldProposal(int PageIndex, FormFieldKind Kind, PdfRect Rect, string Name, string Label, FieldProposalSource Source)
{
    public bool Multiline { get; init; }
}

/// <summary>Strona pominięta przez limit.</summary>
public sealed record FieldSkippedPage(int PageIndex, FieldPageSkipReason Reason);

/// <summary>Wynik rozpoznawania: propozycje i strony pominięte (zawsze jawnie, nie cicha porażka).</summary>
public sealed record FieldRecognitionResult(IReadOnlyList<FieldProposal> Proposals, IReadOnlyList<FieldSkippedPage> SkippedPages);

/// <summary>Teksty nazw ogólnych (z zasobów), gdy w pobliżu pola nie ma etykiety.</summary>
public sealed record FieldGenericNames(string Text, string CheckBox);

/// <summary>Propozycja z analizy jednej strony, jeszcze bez nazwy.</summary>
internal sealed record PageFieldCandidate(FormFieldKind Kind, PdfRect Rect, string Label, FieldProposalSource Source, bool Multiline);

/// <summary>Wynik analizy strony: kandydaci albo powód pominięcia.</summary>
internal sealed record PageAnalysisResult(IReadOnlyList<PageFieldCandidate> Candidates, FieldPageSkipReason? Skipped);

/// <summary>
/// Wykrywanie pól na płaskim PDF lub skanie. Heurystyka na bitmapie strony i słowach, bez struktury pliku (opis i ograniczenia:
/// docs/DECYZJE.md D-39). Linia podkreślenia z etykietą to pole tekstowe; zamknięty pusty obszar obok etykiety to pole tekstowe,
/// mała kratka to pole wyboru, rząd stykających się kratek to jedno pole tekstowe. Siatki (tabele) są odrzucane w całości.
/// Liczba słów, linii, obszarów i propozycji ma limity; strona ponad limit jest pomijana i wymieniana w wyniku.
/// </summary>
internal static class FieldRecognizer
{
    // Limity bitmapy: rozmiar strony w pliku jest niezaufany.
    private const double PreferredDpi = 150;
    private const int MaxSide = 3000;
    private const long MaxPixels = 6_000_000;
    private const int MaxRuns = 400_000;
    private const int MaxLabelWords = 6;
    private const int MinLeaderChars = 5;

    // Limity treści strony. Gęsta strona tekstu to kilka tysięcy słów, formularz kilkaset linii.
    public const int MaxChars = 200_000;
    public const int MaxWords = 20_000;
    public const int MaxLines = 3_000;
    public const int MaxCells = 5_000;
    public const int MaxRawProposals = 2_000;
    public const int MaxWidgetsPerPage = 5_000;

    private sealed record Word(string Text, double L, double T, double R, double B, double Size)
    {
        public double CenterY => (T + B) / 2;
    }

    private sealed record Raw(FormFieldKind Kind, double L, double T, double R, double B, string Label, FieldProposalSource Source, bool Multiline, int Priority);

    private sealed class LimitExceeded(FieldPageSkipReason reason) : Exception
    {
        public FieldPageSkipReason Reason { get; } = reason;
    }

    /// <summary>
    /// Indeks słów: kubełki po środku wysokości, w kubełku tablice posortowane po prawej i lewej krawędzi. Zapytanie to wyszukiwanie
    /// binarne plus przegląd zakresu, z limitem przeglądanych słów, więc nawet słowa nałożone w jednym miejscu nie dają pracy kwadratowej.
    /// </summary>
    private sealed class WordIndex
    {
        private const int MaxExamined = 1024;
        private readonly double _bucket;
        private readonly Word[][] _byR, _byL;

        public WordIndex(List<Word> words, int height, double s)
        {
            Count = words.Count;
            _bucket = Math.Max(1, 8 * s);
            int n = (int)(height / _bucket) + 2;
            var lists = new List<Word>[n];
            foreach (var w in words) (lists[Bucket(w.CenterY, n)] ??= new List<Word>()).Add(w);
            _byR = new Word[n][];
            _byL = new Word[n][];
            for (int i = 0; i < n; i++)
            {
                var list = lists[i];
                _byR[i] = list is null ? Array.Empty<Word>() : list.OrderBy(w => w.R).ToArray();
                _byL[i] = list is null ? Array.Empty<Word>() : list.OrderBy(w => w.L).ToArray();
            }
        }

        public int Count { get; }

        private int Bucket(double y, int n) => (int)Math.Clamp(y / _bucket, 0, n - 1);

        private IEnumerable<Word> Scan(Word[][] table, Func<Word, double> key, double yMin, double yMax, double keyMin, double keyMax)
        {
            int n = table.Length, examined = 0;
            for (int b = Bucket(yMin, n), last = Bucket(yMax, n); b <= last; b++)
            {
                var row = table[b];
                int lo = 0, hi = row.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (key(row[mid]) < keyMin) lo = mid + 1;
                    else hi = mid;
                }
                for (int i = lo; i < row.Length && key(row[i]) <= keyMax; i++)
                {
                    if (++examined > MaxExamined) yield break;
                    if (row[i].CenterY >= yMin && row[i].CenterY <= yMax) yield return row[i];
                }
            }
        }

        /// <summary>Słowa o prawej krawędzi w zakresie, środek w zakresie Y.</summary>
        public IEnumerable<Word> ByRight(double yMin, double yMax, double rMin, double rMax) => Scan(_byR, w => w.R, yMin, yMax, rMin, rMax);

        /// <summary>Słowa o lewej krawędzi w zakresie, środek w zakresie Y.</summary>
        public IEnumerable<Word> ByLeft(double yMin, double yMax, double lMin, double lMax) => Scan(_byL, w => w.L, yMin, yMax, lMin, lMax);
    }

    /// <summary>Maska słów w komórkach 2 px: „czy w prostokącie jest słowo” w O(1). Pamięć zależy od bitmapy, nie od liczby słów.</summary>
    private sealed class WordMask
    {
        private const int Cell = 2;
        private readonly int[] _sum;
        private readonly int _w, _h;

        public WordMask(List<Word> words, int width, int height)
        {
            _w = width / Cell + 1;
            _h = height / Cell + 1;
            var diff = new int[(_w + 1) * (_h + 1)];
            int stride = _w + 1;
            foreach (var word in words)
            {
                int x0 = Math.Clamp((int)((word.L + 1) / Cell), 0, _w), x1 = Math.Clamp((int)Math.Ceiling((word.R - 1) / Cell), 0, _w);
                int y0 = Math.Clamp((int)((word.T + 1) / Cell), 0, _h), y1 = Math.Clamp((int)Math.Ceiling((word.B - 1) / Cell), 0, _h);
                if (x1 <= x0) x1 = Math.Min(x0 + 1, _w);
                if (y1 <= y0) y1 = Math.Min(y0 + 1, _h);
                if (x1 <= x0 || y1 <= y0) continue;
                diff[y0 * stride + x0]++;
                diff[y0 * stride + x1]--;
                diff[y1 * stride + x0]--;
                diff[y1 * stride + x1]++;
            }
            _sum = new int[stride * (_h + 1)];
            for (int y = 0; y < _h; y++)
            {
                int running = 0;
                for (int x = 0; x < _w; x++)
                {
                    int i = y * stride + x;
                    // pokrycie z tablicy różnic (zapis w miejscu), potem suma narastająca zajętych komórek
                    int cover = diff[i] + (x > 0 ? diff[i - 1] : 0) + (y > 0 ? diff[i - stride] : 0) - (x > 0 && y > 0 ? diff[i - stride - 1] : 0);
                    diff[i] = cover;
                    if (cover > 0) running++;
                    _sum[(y + 1) * stride + x + 1] = _sum[y * stride + x + 1] + running;
                }
            }
        }

        public bool Any(double l, double t, double r, double b)
        {
            int x0 = Math.Clamp((int)(l / Cell), 0, _w), x1 = Math.Clamp((int)Math.Ceiling(r / Cell), 0, _w);
            int y0 = Math.Clamp((int)(t / Cell), 0, _h), y1 = Math.Clamp((int)Math.Ceiling(b / Cell), 0, _h);
            if (x1 <= x0 || y1 <= y0) return false;
            int stride = _w + 1;
            return _sum[y1 * stride + x1] - _sum[y0 * stride + x1] - _sum[y1 * stride + x0] + _sum[y0 * stride + x0] > 0;
        }
    }

    /// <summary>Piksele na punkt: 150 DPI, mniej dla stron ogromnych.</summary>
    public static double ScaleFor(PdfSize size)
    {
        double scale = PreferredDpi / 72.0;
        double max = Math.Max(Math.Max(size.Width, size.Height), 1);
        scale = Math.Min(scale, MaxSide / max);
        double area = Math.Max(size.Width, 1) * Math.Max(size.Height, 1);
        return Math.Min(scale, Math.Sqrt(MaxPixels / area));
    }

    /// <summary>Analiza jednej strony. Czysta funkcja, bez PDFium: dane wejściowe to bitmapa i tekst.</summary>
    public static PageAnalysisResult AnalyzePage(RenderedBitmap bitmap, PageText text, PageInfo info, CancellationToken ct = default)
    {
        try
        {
            return new PageAnalysisResult(Analyze(bitmap, text, info, ct), null);
        }
        catch (LimitExceeded limit)
        {
            return new PageAnalysisResult(Array.Empty<PageFieldCandidate>(), limit.Reason);
        }
    }

    private static List<PageFieldCandidate> Analyze(RenderedBitmap bitmap, PageText text, PageInfo info, CancellationToken ct)
    {
        var transform = new PageTransform(info.Size, info.CropOrigin, info.Rotation, bitmap.Scale, new Point(0, 0));
        double s = bitmap.Scale;
        if (text.Chars.Count > MaxChars) throw new LimitExceeded(FieldPageSkipReason.TooManyWords);
        var (wordList, leaders) = BuildWords(text, transform, ct);
        if (wordList.Count + leaders.Count > MaxWords) throw new LimitExceeded(FieldPageSkipReason.TooManyWords);
        var words = new WordIndex(wordList, bitmap.Height, s);
        var mask = new WordMask(wordList, bitmap.Width, bitmap.Height);
        ct.ThrowIfCancellationRequested();

        var ink = InkMap.From(bitmap);
        ct.ThrowIfCancellationRequested();
        var regions = ink.Components((x, y) => !ink.IsDarkDilated(x, y), 1, 0, 0, MaxRuns, ct);
        var lineRuns = ink.Components((x, y) => ink.IsDark(x, y), Px(12, s), 1, 1, MaxRuns, ct);
        if (regions is null || lineRuns is null) throw new LimitExceeded(FieldPageSkipReason.TooManyRuns);

        int minLine = Px(30, s);
        var lines = lineRuns.Where(c => c.Box.Width >= minLine).ToList();
        if (lines.Count > MaxLines) throw new LimitExceeded(FieldPageSkipReason.TooManyLines);
        var cells = regions.Where(c => !c.TouchesBorder && c.Box.Width >= Px(5, s) && c.Box.Height >= Px(5, s) && c.Area >= c.Box.Area / 2).ToList();
        if (cells.Count > MaxCells) throw new LimitExceeded(FieldPageSkipReason.TooManyCells);

        var raws = new List<Raw>();
        AddBoxes(raws, cells, ink, words, mask, s, bitmap.Height, ct);
        AddLines(raws, lines, cells, ink, words, s, bitmap.Width, ct);
        AddLeaders(raws, leaders, words, mask, s, ct);

        var kept = new List<Raw>();
        foreach (var r in raws.OrderBy(r => r.Priority))
        {
            ct.ThrowIfCancellationRequested();
            if (!kept.Any(k => OverlapRatio(k, r) > 0.3)) kept.Add(r);
        }
        return kept.Select(r => new PageFieldCandidate(r.Kind, transform.ToPage(new Rect(r.L, r.T, r.R - r.L, r.B - r.T)), r.Label, r.Source, r.Multiline)).ToList();
    }

    private static void AddRaw(List<Raw> raws, Raw raw)
    {
        if (raws.Count >= MaxRawProposals) throw new LimitExceeded(FieldPageSkipReason.TooManyProposals);
        raws.Add(raw);
    }

    private static int Px(double points, double s) => Math.Max(1, (int)Math.Round(points * s));

    private static double OverlapRatio(Raw a, Raw b)
    {
        double w = Math.Min(a.R, b.R) - Math.Max(a.L, b.L), h = Math.Min(a.B, b.B) - Math.Max(a.T, b.T);
        if (w <= 0 || h <= 0) return 0;
        double min = Math.Min((a.R - a.L) * (a.B - a.T), (b.R - b.L) * (b.B - b.T));
        return min <= 0 ? 0 : w * h / min;
    }

    // ------------------------------------------------------------------ słowa

    private static (List<Word> Words, List<Word> Leaders) BuildWords(PageText text, PageTransform transform, CancellationToken ct)
    {
        var words = new List<Word>();
        var leaders = new List<Word>();
        var buffer = new List<(char C, Rect Box, double Size)>();

        void Flush()
        {
            int i = 0;
            while (i < buffer.Count)
            {
                bool leader = IsLeaderChar(buffer[i].C);
                int j = i + 1;
                while (j < buffer.Count && IsLeaderChar(buffer[j].C) == leader) j++;
                var part = buffer.GetRange(i, j - i);
                var word = new Word(new string(part.Select(p => p.C).ToArray()), part.Min(p => p.Box.Left), part.Min(p => p.Box.Top), part.Max(p => p.Box.Right), part.Max(p => p.Box.Bottom), part.Max(p => p.Size));
                if (leader && part.Count >= MinLeaderChars) leaders.Add(word);
                else words.Add(word);
                i = j;
            }
            buffer.Clear();
        }

        int count = 0;
        foreach (var c in text.Chars)
        {
            if ((++count & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (c.IsGenerated || char.IsWhiteSpace(c.Char) || char.IsControl(c.Char) || c.Box.Width <= 0 || c.Box.Height <= 0)
            {
                Flush();
                continue;
            }
            var box = transform.ToView(c.Box);
            if (!double.IsFinite(box.Left) || !double.IsFinite(box.Top) || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom))
            {
                Flush();
                continue;
            }
            double size = c.FontSize > 0 ? c.FontSize * transform.Scale : box.Height;
            if (buffer.Count > 0)
            {
                var last = buffer[^1];
                bool apart = box.Left - last.Box.Right > 0.35 * size || box.Right < last.Box.Left
                    || Math.Abs((box.Top + box.Bottom) / 2 - (last.Box.Top + last.Box.Bottom) / 2) > 0.7 * Math.Max(size, last.Size);
                if (apart) Flush();
            }
            buffer.Add((c.Char, box, size));
        }
        Flush();
        return (words, leaders);
    }

    private static bool IsLeaderChar(char c) => c is '_' or '.';

    // ------------------------------------------------------------------ zamknięte obszary

    private static void AddBoxes(List<Raw> raws, List<InkComponent> cells, InkMap ink, WordIndex words, WordMask mask, double s, int height, CancellationToken ct)
    {
        // Obszary o wspólnej krawędzi tworzą grupę: tabela, rząd kratek albo pojedynczy prostokąt.
        int n = cells.Count;
        if (n == 0) return;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }
        int touch = Px(3, s);
        // Sortowanie po lewej krawędzi ogranicza porównania do obszarów, które mogą się stykać w poziomie.
        var order = Enumerable.Range(0, n).OrderBy(i => cells[i].Box.L).ToArray();
        for (int oi = 0; oi < n; oi++)
        {
            if ((oi & 63) == 0) ct.ThrowIfCancellationRequested();
            int i = order[oi];
            var a = cells[i].Box;
            for (int oj = oi + 1; oj < n; oj++)
            {
                int j = order[oj];
                var b = cells[j].Box;
                if (b.L > a.R + touch) break;
                int gapX = Math.Max(a.L, b.L) - Math.Min(a.R, b.R), gapY = Math.Max(a.T, b.T) - Math.Min(a.B, b.B);
                bool sideBySide = gapX >= 0 && gapX <= touch && -gapY >= Math.Min(a.Height, b.Height) / 2;
                bool stacked = gapY >= 0 && gapY <= touch && -gapX >= Math.Min(a.Width, b.Width) / 2;
                if (sideBySide || stacked) parent[Find(i)] = Find(j);
            }
        }

        foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
        {
            ct.ThrowIfCancellationRequested();
            var members = group.Select(i => cells[i]).ToList();
            int tol = Px(4, s);
            int rows = Distinct(members.Select(m => m.Box.T), tol);
            int cols = Distinct(members.Select(m => m.Box.L), tol);
            var empty = members.Where(m => IsEmptyBox(m, ink, mask)).ToList();

            // Siatka to tabela. Jedna kolumna z tekstem w którejś komórce też (tabela bez pionowych podziałów).
            if (rows >= 2 && cols >= 2) continue;
            if (cols == 1 && members.Count >= 3 && empty.Count < members.Count) continue;

            // Rząd co najmniej trzech jednakowych pustych kratek (np. PESEL) to jedno pole tekstowe.
            if (rows == 1 && members.Count >= 3 && empty.Count == members.Count && members.All(m => m.Box.Height <= Px(24, s))
                && members.Max(m => m.Box.Width) <= members.Min(m => m.Box.Width) * 1.5)
            {
                double l = members.Min(m => m.Box.L), t = members.Min(m => m.Box.T), r = members.Max(m => m.Box.R), b = members.Max(m => m.Box.B);
                var label = LeftLabel(words, l, t, b, s) ?? AboveLabel(words, l, t, r, s);
                if (label is null && words.Count > 0) continue;
                AddRaw(raws, new Raw(FormFieldKind.Text, l - 1, t - 1, r + 1, b + 1, label ?? string.Empty, FieldProposalSource.Comb, false, 0));
                continue;
            }

            foreach (var cell in empty)
            {
                if (cell.Area < cell.Box.Area * 9 / 10) continue; // okrąg, litera, kształt nieprostokątny
                var box = cell.Box;
                // obszar po dylatacji jest mniejszy o około piksel z każdej strony
                double l = box.L - 1, t = box.T - 1, r = box.R + 1, b = box.B + 1;
                double wPt = (r - l) / s, hPt = (b - t) / s;
                bool square = Math.Max(wPt, hPt) / Math.Max(Math.Min(wPt, hPt), 0.1) <= 1.35;
                if (square && wPt >= 6 && wPt <= 22 && hPt >= 6 && hPt <= 22)
                {
                    var label = RightLabel(words, r, t, b, s) ?? LeftLabel(words, l, t, b, s);
                    AddRaw(raws, new Raw(FormFieldKind.CheckBox, l, t, r, b, label ?? string.Empty, FieldProposalSource.CheckBox, false, 0));
                }
                else if (wPt >= 30 && hPt >= 10 && hPt <= height / s * 0.5)
                {
                    var label = LeftLabel(words, l, t, b, s) ?? AboveLabel(words, l, t, r, s);
                    if (label is null && words.Count > 0) continue; // prostokąt bez etykiety na stronie z tekstem: rysunek, ramka
                    AddRaw(raws, new Raw(FormFieldKind.Text, l, t, r, b, label ?? string.Empty, FieldProposalSource.Box, hPt > 30, 1));
                }
            }
        }
    }

    private static int Distinct(IEnumerable<int> values, int tolerance)
    {
        int count = 0, last = int.MinValue / 2;
        foreach (var v in values.OrderBy(v => v))
        {
            if (v - last > tolerance) count++;
            last = v;
        }
        return count;
    }

    /// <summary>Puste wnętrze: bez słów i prawie bez tuszu. Margines 2 px dopuszcza niewielkie przekrzywienie skanu.</summary>
    private static bool IsEmptyBox(InkComponent cell, InkMap ink, WordMask mask)
    {
        var b = cell.Box;
        var inner = new PxBox(b.L + 2, b.T + 2, b.R - 2, b.B - 2);
        if (inner.Width <= 0 || inner.Height <= 0) return false;
        if (ink.Count(inner) > inner.Area / 100) return false;
        return !mask.Any(b.L, b.T, b.R, b.B);
    }

    // ------------------------------------------------------------------ linie

    private static void AddLines(List<Raw> raws, List<InkComponent> lines, List<InkComponent> cells, InkMap ink, WordIndex words, double s, int pageWidth, CancellationToken ct)
    {
        int maxThick = Px(4, s);
        foreach (var c in lines)
        {
            ct.ThrowIfCancellationRequested();
            var box = c.Box;
            if (box.Height > Math.Max(maxThick, box.Width * 3 / 100)) continue;
            if (IsCellEdge(box, cells, s) || HasStem(box, ink, s)) continue;
            double height = FreeHeightAbove(box, ink, s);
            if (height < Px(9, s)) continue;

            var label = LeftLabel(words, box.L, box.T - height, box.B, s) ?? CaptionBelow(words, box, s, pageWidth);
            if (label is null && (words.Count > 0 || box.Width > pageWidth * 8 / 10)) continue;
            AddRaw(raws, new Raw(FormFieldKind.Text, box.L, box.T - height, box.R, box.T, label ?? string.Empty, FieldProposalSource.Underline, false, 2));
        }
    }

    private static bool IsCellEdge(PxBox line, List<InkComponent> cells, double s)
    {
        int tol = Px(3, s);
        foreach (var cell in cells)
        {
            var b = cell.Box;
            int overlap = Math.Min(line.R, b.R) - Math.Max(line.L, b.L);
            if (overlap < Math.Min(line.Width, b.Width) / 2) continue;
            if (Math.Abs(line.T - b.B) <= tol || Math.Abs(line.B - b.T) <= tol) return true;
        }
        return false;
    }

    /// <summary>Pionowa kreska stykająca się z linią (ściana tabeli, rysunek). Wydłużenia liter są krótsze i nie stykają się z linią.</summary>
    private static bool HasStem(PxBox line, InkMap ink, double s)
    {
        int len = Px(8, s), need = len * 9 / 10;
        for (int x = line.L; x < line.R; x++)
            if (ink.Count(x, line.T - len, x + 1, line.T) >= need || ink.Count(x, line.B, x + 1, line.B + len) >= need) return true;
        return false;
    }

    /// <summary>Wolna wysokość nad linią (do 16 pt). Tekst podkreślony lub przekreślony da wartość zbyt małą.</summary>
    private static double FreeHeightAbove(PxBox line, InkMap ink, double s)
    {
        int max = Px(16, s), l = line.L + 2, r = line.R - 2, y = line.T - 1, free = 0;
        int allowed = Math.Max(1, (r - l) / 200);
        while (free < max && y >= 0 && ink.Count(l, y, r, y + 1) <= allowed)
        {
            free++;
            y--;
        }
        return free;
    }

    private static void AddLeaders(List<Raw> raws, List<Word> leaders, WordIndex words, WordMask mask, double s, CancellationToken ct)
    {
        foreach (var w in leaders)
        {
            ct.ThrowIfCancellationRequested();
            if (w.R - w.L < Px(20, s)) continue;
            // numer strony za wielokropkiem to spis treści
            double reach = Math.Max(w.B - w.T, Px(6, s)) * 2;
            if (words.ByLeft(w.CenterY - reach, w.CenterY + reach, w.R - 1, w.R + Px(24, s)).Any(o => Math.Abs(o.B - w.B) <= Math.Max(o.B - o.T, 1) && o.Text.All(char.IsDigit))) continue;
            double top = w.B - Px(14, s);
            if (mask.Any(w.L, top, w.R, w.T)) continue;
            var label = LeftLabel(words, w.L, top, w.B, s);
            if (label is null) continue;
            AddRaw(raws, new Raw(FormFieldKind.Text, w.L, top, w.R, w.B, label, FieldProposalSource.Leader, false, 3));
        }
    }

    // ------------------------------------------------------------------ etykiety

    private static string Join(IEnumerable<Word> words) => string.Join(' ', words.Select(w => w.Text)).Trim().TrimEnd(':').Trim();

    /// <summary>Słowa kończące się przed polem, w jego rzędzie, ostatnie najbliżej pola.</summary>
    private static string? LeftLabel(WordIndex words, double left, double top, double bottom, double s)
    {
        double slack = Px(3, s);
        var near = words.ByRight(top - slack, bottom + slack, left - Px(36, s), left + slack).MaxBy(w => w.R);
        if (near is null) return null;
        var row = new List<Word> { near };
        double band = near.Size * 0.4;
        while (row.Count < MaxLabelWords)
        {
            double edge = row[0].L;
            var next = words.ByRight(near.CenterY - band, near.CenterY + band, edge - near.Size * 1.2, edge).MaxBy(w => w.R);
            if (next is null || next.R > edge || next.L >= edge) break;
            row.Insert(0, next);
        }
        return Join(row);
    }

    private static string? RightLabel(WordIndex words, double right, double top, double bottom, double s)
    {
        double slack = Px(3, s);
        var near = words.ByLeft(top - slack, bottom + slack, right - slack, right + Px(24, s)).MinBy(w => w.L);
        if (near is null) return null;
        var row = new List<Word> { near };
        double band = near.Size * 0.4;
        while (row.Count < MaxLabelWords)
        {
            double edge = row[^1].R;
            var next = words.ByLeft(near.CenterY - band, near.CenterY + band, edge, edge + near.Size * 1.2).MinBy(w => w.L);
            if (next is null || next.L < edge || next.R <= edge) break;
            row.Add(next);
        }
        return Join(row);
    }

    private static string? AboveLabel(WordIndex words, double left, double top, double right, double s)
    {
        var row = words.ByLeft(top - Px(42, s), top + Px(2, s), left - Px(4, s), right)
            .Where(w => w.B <= top + Px(2, s) && top - w.B <= Px(18, s) && w.L < right)
            .OrderByDescending(w => w.B).ToList();
        if (row.Count == 0) return null;
        var first = row[0];
        var same = row.Where(w => Math.Abs(w.CenterY - first.CenterY) <= first.Size * 0.4).OrderBy(w => w.L).Take(MaxLabelWords).ToList();
        return Join(same);
    }

    /// <summary>Podpis pod linią (np. „data i podpis”): jeden ciągły napis w zakresie linii. Wiele kolumn pod linią to nagłówek tabeli.</summary>
    private static string? CaptionBelow(WordIndex words, PxBox line, double s, int pageWidth)
    {
        if (line.Width > pageWidth * 7 / 10) return null;
        var below = words.ByLeft(line.B - 1, line.B + Px(36, s), double.MinValue, double.MaxValue)
            .Where(w => w.T >= line.B - 1 && w.T - line.B <= Px(12, s)).OrderBy(w => w.T).ToList();
        if (below.Count == 0) return null;
        var first = below[0];
        var row = below.Where(w => Math.Abs(w.CenterY - first.CenterY) <= first.Size * 0.4).OrderBy(w => w.L).ToList();
        if (row[0].L < line.L - Px(4, s) || row[^1].R > line.R + Px(4, s)) return null;
        for (int i = 1; i < row.Count; i++)
            if (row[i].L - row[i - 1].R > first.Size * 1.5) return null;
        return Join(row.Take(MaxLabelWords));
    }

    // ------------------------------------------------------------------ cały dokument

    /// <summary>
    /// Analiza dokumentu: render i tekst przez PDFium (wątek rendera), analiza w puli wątków. Pomija propozycje pokrywające się
    /// z istniejącymi widżetami; nazwy są unikalne względem pól dokumentu i siebie nawzajem. Strony ponad limity trafiają do wyniku.
    /// </summary>
    public static async Task<FieldRecognitionResult> RecognizeAsync(byte[] pdf, IPdfRenderer renderer, IReadOnlyList<(int Page, PdfRect Rect)> existing,
        IEnumerable<string> existingNames, FieldGenericNames generic, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        using var doc = await renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false);
        var used = FieldNames.UsedFrom(existingNames);
        var result = new List<FieldProposal>();
        var skipped = new List<FieldSkippedPage>();
        var byPage = existing.GroupBy(e => e.Page).ToDictionary(g => g.Key, g => g.Select(e => e.Rect).ToList());
        for (int page = 0; page < doc.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            var info = doc.Pages[page];
            var widgets = byPage.TryGetValue(page, out var list) ? list : new List<PdfRect>();
            if (widgets.Count > MaxWidgetsPerPage)
            {
                skipped.Add(new FieldSkippedPage(page, FieldPageSkipReason.TooManyWidgets));
                continue;
            }
            var bitmap = await doc.RenderPageAsync(page, new RenderRequest(ScaleFor(info.Size), Flags: RenderFlags.None), ct).ConfigureAwait(false);
            var text = await doc.GetPageTextAsync(page, ct).ConfigureAwait(false);
            var analysis = await Task.Run(() => AnalyzePage(bitmap, text, info, ct), ct).ConfigureAwait(false);
            if (analysis.Skipped is { } reason) skipped.Add(new FieldSkippedPage(page, reason));
            foreach (var f in analysis.Candidates.OrderByDescending(f => f.Rect.Top).ThenBy(f => f.Rect.Left))
            {
                ct.ThrowIfCancellationRequested();
                if (widgets.Any(e => Overlaps(e, f.Rect))) continue;
                var name = FieldNames.Unique(f.Label, f.Kind == FormFieldKind.CheckBox ? generic.CheckBox : generic.Text, used);
                result.Add(new FieldProposal(page, f.Kind, f.Rect, name, f.Label, f.Source) { Multiline = f.Multiline });
            }
            progress?.Report(OperationProgress.Of(page + 1, doc.PageCount));
        }
        return new FieldRecognitionResult(result, skipped);
    }

    private static bool Overlaps(PdfRect a, PdfRect b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), h = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (w <= 0 || h <= 0) return false;
        double min = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return min > 0 && w * h / min > 0.3;
    }
}

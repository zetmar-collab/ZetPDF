using System.Windows;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Forms.Recognition;

/// <summary>Skąd wzięła się propozycja pola.</summary>
public enum FieldProposalSource { Underline, Leader, Box, Comb, CheckBox }

/// <summary>Propozycja pola na stronie. Współrzędne w punktach strony.</summary>
public sealed record FieldProposal(int PageIndex, FormFieldKind Kind, PdfRect Rect, string Name, string Label, FieldProposalSource Source)
{
    public bool Multiline { get; init; }
}

/// <summary>Teksty nazw ogólnych (z zasobów), gdy w pobliżu pola nie ma etykiety.</summary>
public sealed record FieldGenericNames(string Text, string CheckBox);

/// <summary>Propozycja z analizy jednej strony, jeszcze bez nazwy.</summary>
internal sealed record PageFieldCandidate(FormFieldKind Kind, PdfRect Rect, string Label, FieldProposalSource Source, bool Multiline);

/// <summary>
/// Wykrywanie pól na płaskim PDF lub skanie. Heurystyka na bitmapie strony i słowach, bez struktury pliku (opis i ograniczenia:
/// docs/DECYZJE.md D-39). Linia podkreślenia z etykietą to pole tekstowe; zamknięty pusty obszar obok etykiety to pole tekstowe,
/// mała kratka to pole wyboru, rząd stykających się kratek to jedno pole tekstowe. Siatki (tabele) są odrzucane w całości.
/// </summary>
internal static class FieldRecognizer
{
    // Limity bitmapy: rozmiar strony w pliku jest niezaufany.
    private const double PreferredDpi = 150;
    private const int MaxSide = 3000;
    private const long MaxPixels = 6_000_000;
    private const int MaxRuns = 400_000;
    private const int MaxCells = 5000;
    private const int MaxLabelWords = 6;
    private const int MinLeaderChars = 5;

    private sealed record Word(string Text, double L, double T, double R, double B, double Size)
    {
        public double CenterY => (T + B) / 2;
    }

    private sealed record Raw(FormFieldKind Kind, double L, double T, double R, double B, string Label, FieldProposalSource Source, bool Multiline, int Priority);

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
    public static IReadOnlyList<PageFieldCandidate> AnalyzePage(RenderedBitmap bitmap, PageText text, PageInfo info, CancellationToken ct = default)
    {
        var transform = new PageTransform(info.Size, info.CropOrigin, info.Rotation, bitmap.Scale, new Point(0, 0));
        var ink = InkMap.From(bitmap);
        double s = bitmap.Scale;
        ct.ThrowIfCancellationRequested();
        var (words, leaders) = BuildWords(text, transform);
        var raws = new List<Raw>();

        var regions = ink.Components((x, y) => !ink.IsDarkDilated(x, y), 1, 0, 0, MaxRuns);
        ct.ThrowIfCancellationRequested();
        var lines = ink.Components((x, y) => ink.IsDark(x, y), Px(12, s), 1, 1, MaxRuns);
        ct.ThrowIfCancellationRequested();
        if (regions is null || lines is null) return Array.Empty<PageFieldCandidate>();

        var cells = regions.Where(c => !c.TouchesBorder && c.Box.Width >= Px(5, s) && c.Box.Height >= Px(5, s) && c.Area >= c.Box.Area / 2).ToList();
        AddBoxes(raws, cells, ink, words, s, bitmap.Height, ct);
        ct.ThrowIfCancellationRequested();
        AddLines(raws, lines, cells, ink, words, s, bitmap.Width);
        AddLeaders(raws, leaders, words, s);

        var kept = new List<Raw>();
        foreach (var r in raws.OrderBy(r => r.Priority))
            if (!kept.Any(k => OverlapRatio(k, r) > 0.3)) kept.Add(r);
        return kept.Select(r => new PageFieldCandidate(r.Kind, transform.ToPage(new Rect(r.L, r.T, r.R - r.L, r.B - r.T)), r.Label, r.Source, r.Multiline)).ToList();
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

    private static (List<Word> Words, List<Word> Leaders) BuildWords(PageText text, PageTransform transform)
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

        foreach (var c in text.Chars)
        {
            if (c.IsGenerated || char.IsWhiteSpace(c.Char) || char.IsControl(c.Char) || c.Box.Width <= 0 || c.Box.Height <= 0)
            {
                Flush();
                continue;
            }
            var box = transform.ToView(c.Box);
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

    private static void AddBoxes(List<Raw> raws, List<InkComponent> cells, InkMap ink, List<Word> words, double s, int height, CancellationToken ct)
    {
        // Obszary o wspólnej krawędzi tworzą grupę: tabela, rząd kratek albo pojedynczy prostokąt.
        int n = cells.Count;
        if (n == 0 || n > MaxCells) return; // tysiące komórek to nie formularz, a porównanie par jest kwadratowe
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
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var a = cells[i].Box;
            for (int j = i + 1; j < n; j++)
            {
                var b = cells[j].Box;
                int gapX = Math.Max(a.L, b.L) - Math.Min(a.R, b.R), gapY = Math.Max(a.T, b.T) - Math.Min(a.B, b.B);
                bool sideBySide = gapX >= 0 && gapX <= touch && -gapY >= Math.Min(a.Height, b.Height) / 2;
                bool stacked = gapY >= 0 && gapY <= touch && -gapX >= Math.Min(a.Width, b.Width) / 2;
                if (sideBySide || stacked) parent[Find(i)] = Find(j);
            }
        }

        foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
        {
            var members = group.Select(i => cells[i]).ToList();
            int tol = Px(4, s);
            int rows = Distinct(members.Select(m => m.Box.T), tol);
            int cols = Distinct(members.Select(m => m.Box.L), tol);
            var empty = members.Where(m => IsEmptyBox(m, ink, words)).ToList();

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
                raws.Add(new Raw(FormFieldKind.Text, l - 1, t - 1, r + 1, b + 1, label ?? string.Empty, FieldProposalSource.Comb, false, 0));
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
                    raws.Add(new Raw(FormFieldKind.CheckBox, l, t, r, b, label ?? string.Empty, FieldProposalSource.CheckBox, false, 0));
                }
                else if (wPt >= 30 && hPt >= 10 && hPt <= height / s * 0.5)
                {
                    var label = LeftLabel(words, l, t, b, s) ?? AboveLabel(words, l, t, r, s);
                    if (label is null && words.Count > 0) continue; // prostokąt bez etykiety na stronie z tekstem: rysunek, ramka
                    raws.Add(new Raw(FormFieldKind.Text, l, t, r, b, label ?? string.Empty, FieldProposalSource.Box, hPt > 30, 1));
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
    private static bool IsEmptyBox(InkComponent cell, InkMap ink, List<Word> words)
    {
        var b = cell.Box;
        var inner = new PxBox(b.L + 2, b.T + 2, b.R - 2, b.B - 2);
        if (inner.Width <= 0 || inner.Height <= 0) return false;
        if (ink.Count(inner) > inner.Area / 100) return false;
        return !words.Any(w => w.R > b.L && w.L < b.R && w.B > b.T && w.T < b.B);
    }

    // ------------------------------------------------------------------ linie

    private static void AddLines(List<Raw> raws, List<InkComponent> lines, List<InkComponent> cells, InkMap ink, List<Word> words, double s, int pageWidth)
    {
        int minWidth = Px(30, s), maxThick = Px(4, s);
        foreach (var c in lines)
        {
            var box = c.Box;
            if (box.Width < minWidth) continue;
            if (box.Height > Math.Max(maxThick, box.Width * 3 / 100)) continue;
            if (IsCellEdge(box, cells, s) || HasStem(box, ink, s)) continue;
            double height = FreeHeightAbove(box, ink, s);
            if (height < Px(9, s)) continue;

            var label = LeftLabel(words, box.L, box.T - height, box.B, s) ?? CaptionBelow(words, box, s, pageWidth);
            if (label is null && (words.Count > 0 || box.Width > pageWidth * 8 / 10)) continue;
            raws.Add(new Raw(FormFieldKind.Text, box.L, box.T - height, box.R, box.T, label ?? string.Empty, FieldProposalSource.Underline, false, 2));
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

    private static void AddLeaders(List<Raw> raws, List<Word> leaders, List<Word> words, double s)
    {
        foreach (var w in leaders)
        {
            if (w.R - w.L < Px(20, s)) continue;
            // numer strony za wielokropkiem to spis treści
            if (words.Any(o => o.L >= w.R - 1 && o.L - w.R <= Px(24, s) && Math.Abs(o.B - w.B) <= Math.Max(o.B - o.T, 1) && o.Text.All(char.IsDigit))) continue;
            double top = w.B - Px(14, s);
            if (words.Any(o => o.R > w.L && o.L < w.R && o.B <= w.T && o.B > top)) continue;
            var label = LeftLabel(words, w.L, top, w.B, s);
            if (label is null) continue;
            raws.Add(new Raw(FormFieldKind.Text, w.L, top, w.R, w.B, label, FieldProposalSource.Leader, false, 3));
        }
    }

    // ------------------------------------------------------------------ etykiety

    private static string Join(IEnumerable<Word> words) => string.Join(' ', words.Select(w => w.Text)).Trim().TrimEnd(':').Trim();

    /// <summary>Słowa kończące się przed polem, w jego rzędzie, ostatnie najbliżej pola.</summary>
    private static string? LeftLabel(List<Word> words, double left, double top, double bottom, double s)
    {
        double slack = Px(3, s);
        var near = words.Where(w => w.R <= left + slack && w.CenterY >= top - slack && w.CenterY <= bottom + slack && left - w.R <= Px(36, s))
            .OrderByDescending(w => w.R).FirstOrDefault();
        if (near is null) return null;
        var row = new List<Word> { near };
        foreach (var w in words.Where(w => w.R <= near.L && Math.Abs(w.CenterY - near.CenterY) <= near.Size * 0.4).OrderByDescending(w => w.R))
        {
            if (row[0].L - w.R > near.Size * 1.2 || row.Count >= MaxLabelWords) break;
            row.Insert(0, w);
        }
        return Join(row);
    }

    private static string? RightLabel(List<Word> words, double right, double top, double bottom, double s)
    {
        double slack = Px(3, s);
        var near = words.Where(w => w.L >= right - slack && w.CenterY >= top - slack && w.CenterY <= bottom + slack && w.L - right <= Px(24, s))
            .OrderBy(w => w.L).FirstOrDefault();
        if (near is null) return null;
        var row = new List<Word> { near };
        foreach (var w in words.Where(w => w.L >= near.R && Math.Abs(w.CenterY - near.CenterY) <= near.Size * 0.4).OrderBy(w => w.L))
        {
            if (w.L - row[^1].R > near.Size * 1.2 || row.Count >= MaxLabelWords) break;
            row.Add(w);
        }
        return Join(row);
    }

    private static string? AboveLabel(List<Word> words, double left, double top, double right, double s)
    {
        var row = words.Where(w => w.B <= top + Px(2, s) && top - w.B <= Px(18, s) && w.L >= left - Px(4, s) && w.L < right)
            .OrderByDescending(w => w.B).ToList();
        if (row.Count == 0) return null;
        var first = row[0];
        var same = row.Where(w => Math.Abs(w.CenterY - first.CenterY) <= first.Size * 0.4).OrderBy(w => w.L).Take(MaxLabelWords).ToList();
        return Join(same);
    }

    /// <summary>Podpis pod linią (np. „data i podpis”): jeden ciągły napis w zakresie linii. Wiele kolumn pod linią to nagłówek tabeli.</summary>
    private static string? CaptionBelow(List<Word> words, PxBox line, double s, int pageWidth)
    {
        if (line.Width > pageWidth * 7 / 10) return null;
        var below = words.Where(w => w.T >= line.B - 1 && w.T - line.B <= Px(12, s)).OrderBy(w => w.T).ToList();
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
    /// z istniejącymi widżetami; nazwy są unikalne względem pól dokumentu i siebie nawzajem.
    /// </summary>
    public static async Task<IReadOnlyList<FieldProposal>> RecognizeAsync(byte[] pdf, IPdfRenderer renderer, IReadOnlyList<(int Page, PdfRect Rect)> existing,
        IEnumerable<string> existingNames, FieldGenericNames generic, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        using var doc = await renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false);
        var used = FieldNames.UsedFrom(existingNames);
        var result = new List<FieldProposal>();
        for (int page = 0; page < doc.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            var info = doc.Pages[page];
            var bitmap = await doc.RenderPageAsync(page, new RenderRequest(ScaleFor(info.Size), Flags: RenderFlags.None), ct).ConfigureAwait(false);
            var text = await doc.GetPageTextAsync(page, ct).ConfigureAwait(false);
            var found = await Task.Run(() => AnalyzePage(bitmap, text, info, ct), ct).ConfigureAwait(false);
            int index = page;
            foreach (var f in found.OrderByDescending(f => f.Rect.Top).ThenBy(f => f.Rect.Left))
            {
                if (existing.Any(e => e.Page == index && Overlaps(e.Rect, f.Rect))) continue;
                var name = FieldNames.Unique(f.Label, f.Kind == FormFieldKind.CheckBox ? generic.CheckBox : generic.Text, used);
                result.Add(new FieldProposal(page, f.Kind, f.Rect, name, f.Label, f.Source) { Multiline = f.Multiline });
            }
            progress?.Report(OperationProgress.Of(page + 1, doc.PageCount));
        }
        return result;
    }

    private static bool Overlaps(PdfRect a, PdfRect b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), h = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (w <= 0 || h <= 0) return false;
        double min = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return min > 0 && w * h / min > 0.3;
    }
}

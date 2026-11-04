namespace ZetPDF.Editing.Conversion;

/// <summary>Słowo strony: tekst, poziomy zakres, linia bazowa, rozmiar czcionki.</summary>
internal sealed record LayoutWord(string Text, double Left, double Right, double Baseline, double Size);

/// <summary>Blok strony (od góry): tabela (wiele kolumn) albo pojedyncze wiersze tekstu (jedna kolumna).</summary>
internal sealed record LayoutBlock(bool IsTable, IReadOnlyList<string[]> Rows);

/// <summary>
/// Wykrywanie tabel z układu tekstu, bez linii siatki: słowa łączone w wiersze po linii bazowej, wiersze dzielone na
/// segmenty po dużych odstępach, a kolejne wiersze z co najmniej dwoma segmentami tworzą kandydata na tabelę. Kolumny to
/// zakresy między pionowymi pasami pustego miejsca wspólnymi dla wierszy kandydata. Czysta logika, bez PDF (testy).
/// </summary>
internal static class TableDetector
{
    /// <summary>Odstęp między słowami (w ułamku rozmiaru czcionki), od którego zaczyna się nowy segment wiersza.</summary>
    private const double SegmentGap = 0.85;
    /// <summary>Największy odstęp między wierszami jednej tabeli (w ułamku rozmiaru czcionki).</summary>
    private const double MaxRowGap = 3.0;
    /// <summary>Tekst w dwóch kolumnach (łamanie gazetowe) ma w komórce średnio co najmniej tyle słów.</summary>
    private const double ProseWordsPerCell = 5;

    private sealed record Segment(string Text, double Left, double Right, int Words);

    private sealed record Row(double Baseline, double Size, List<Segment> Segments)
    {
        public string Text => string.Join(" ", Segments.Select(s => s.Text));
        public bool IsMulti => Segments.Count >= 2;
    }

    public static List<LayoutBlock> Analyze(IEnumerable<LayoutWord> words)
    {
        var rows = BuildRows(words);
        var blocks = new List<LayoutBlock>();
        var text = new List<string[]>();
        void FlushText()
        {
            if (text.Count == 0) return;
            blocks.Add(new LayoutBlock(false, text.ToList()));
            text.Clear();
        }

        for (int i = 0; i < rows.Count;)
        {
            int end = RegionEnd(rows, i);
            if (end > i && TryBuildTable(rows.GetRange(i, end - i + 1)) is { } table)
            {
                FlushText();
                blocks.Add(new LayoutBlock(true, table));
                i = end + 1;
                continue;
            }
            text.Add(new[] { rows[i].Text });
            i++;
        }
        FlushText();
        return blocks;
    }

    private static List<Row> BuildRows(IEnumerable<LayoutWord> words)
    {
        var rows = new List<(double Baseline, double Size, List<LayoutWord> Words)>();
        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(w => w.Baseline).ThenBy(w => w.Left))
        {
            var size = Math.Max(1, w.Size);
            if (rows.Count > 0 && Math.Abs(rows[^1].Baseline - w.Baseline) < Math.Max(size, rows[^1].Size) * 0.4) rows[^1].Words.Add(w);
            else rows.Add((w.Baseline, size, new List<LayoutWord> { w }));
        }
        return rows.Select(r => new Row(r.Baseline, r.Size, Segments(r.Words))).ToList();
    }

    private static List<Segment> Segments(List<LayoutWord> words)
    {
        var result = new List<Segment>();
        var current = new List<LayoutWord>();
        void Flush()
        {
            if (current.Count == 0) return;
            result.Add(new Segment(string.Join(" ", current.Select(c => c.Text)), current[0].Left, current.Max(c => c.Right), current.Count));
            current.Clear();
        }
        foreach (var w in words.OrderBy(w => w.Left))
        {
            if (current.Count > 0 && w.Left - current.Max(c => c.Right) > Math.Max(1, w.Size) * SegmentGap) Flush();
            current.Add(w);
        }
        Flush();
        return result;
    }

    /// <summary>Ostatni wiersz kandydata zaczynającego się od <paramref name="start"/>; zwraca start, gdy kandydata nie ma.</summary>
    private static int RegionEnd(List<Row> rows, int start)
    {
        if (!rows[start].IsMulti) return start;
        int end = start, multi = 1;
        for (int i = start + 1; i < rows.Count; i++)
        {
            if (rows[i - 1].Baseline - rows[i].Baseline > Math.Max(rows[i - 1].Size, rows[i].Size) * MaxRowGap) break;
            if (rows[i].IsMulti) { end = i; multi++; continue; }
            // Pojedynczy wiersz w środku (zawinięta komórka) zostaje tylko wtedy, gdy po nim tabela trwa dalej.
            if (i + 1 < rows.Count && rows[i + 1].IsMulti && rows[i].Baseline - rows[i + 1].Baseline <= rows[i].Size * MaxRowGap) continue;
            break;
        }
        return multi >= 2 ? end : start;
    }

    private static List<string[]>? TryBuildTable(List<Row> rows)
    {
        var multi = rows.Where(r => r.IsMulti).ToList();
        var columns = Columns(multi);
        if (columns.Count < 2) return null;
        var table = new List<string[]>();
        foreach (var row in rows)
        {
            var cells = new string[columns.Count];
            foreach (var seg in row.Segments)
            {
                int c = BestColumn(columns, seg);
                cells[c] = string.IsNullOrEmpty(cells[c]) ? seg.Text : cells[c] + " " + seg.Text;
            }
            for (int i = 0; i < cells.Length; i++) cells[i] ??= string.Empty;
            table.Add(cells);
        }
        // Tekst łamany na dwie kolumny to nie tabela.
        if (columns.Count == 2 && multi.SelectMany(r => r.Segments).Average(s => s.Words) >= ProseWordsPerCell) return null;
        return table;
    }

    /// <summary>Kolumny: zakresy poziome rozdzielone pasami, których nie przecina prawie żaden segment.</summary>
    private static List<(double Left, double Right)> Columns(List<Row> rows)
    {
        const double step = 0.5;
        double min = rows.Min(r => r.Segments.Min(s => s.Left)), max = rows.Max(r => r.Segments.Max(s => s.Right));
        int n = (int)Math.Ceiling((max - min) / step) + 1;
        if (n <= 1 || n > 100_000) return new();
        var cover = new int[n + 1];
        foreach (var seg in rows.SelectMany(r => r.Segments))
        {
            cover[(int)((seg.Left - min) / step)]++;
            cover[Math.Min(n, (int)Math.Ceiling((seg.Right - min) / step))]--;
        }
        // Przy co najmniej 4 wierszach jeden napis może biec w poprzek kolumn (np. nagłówek obejmujący dwie).
        int tolerance = rows.Count >= 4 ? Math.Max(1, rows.Count / 10) : 0;
        double minGutter = Math.Max(1, rows.Average(r => r.Size) * 0.3);
        var columns = new List<(double, double)>();
        int depth = 0;
        double? open = null;
        double gapStart = min;
        for (int i = 0; i < n; i++)
        {
            depth += cover[i];
            double x = min + i * step;
            if (depth > tolerance)
            {
                if (open is null) open = x;
                gapStart = x + step;
            }
            else if (open is not null && x - gapStart >= minGutter)
            {
                columns.Add((open.Value, gapStart));
                open = null;
            }
        }
        if (open is not null) columns.Add((open.Value, max));
        return columns;
    }

    private static int BestColumn(List<(double Left, double Right)> columns, Segment seg)
    {
        int best = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < columns.Count; i++)
        {
            double overlap = Math.Min(columns[i].Right, seg.Right) - Math.Max(columns[i].Left, seg.Left);
            if (overlap > bestScore + 1e-6) { bestScore = overlap; best = i; }
        }
        return best;
    }
}

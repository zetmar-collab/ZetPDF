using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content.Objects;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Editing.Content;
using ZetPDF.Editing.Pdf;
using ZetPDF.Ocr.Pdf;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Editing.Accessibility;

public enum AutoTagSkipReason { Unreadable, TooLarge, RenderChanged }

public sealed record AutoTagSkippedPage(int PageIndex, AutoTagSkipReason Reason);

public sealed record AutoTagOptions
{
    /// <summary>Język wpisywany do /Lang, gdy dokument go nie ma.</summary>
    public string? Language { get; init; }
}

public sealed record AutoTagResult(byte[] Pdf, int TaggedPages, int Headings, int Paragraphs, int Lists, int Figures, int Artifacts, IReadOnlyList<AutoTagSkippedPage> SkippedPages);

/// <summary>
/// Podstawowe oznaczanie struktury (Access.AutoTag). Znaczniki BDC/EMC z MCID wstawiane są do oryginalnych bajtów strumieni treści, reszta
/// treści nie jest przepisywana; słowniki strumieni (m.in. znaczniki oznaczeń ZetPDF) zostają. Budowane: /StructTreeRoot, /ParentTree,
/// /MarkInfo, /StructParents, /Lang. Z rendererem każda strona jest porównywana piksel po pikselu i przy różnicy zostaje bez tagów.
/// Bez tabel; kolejność odczytu to kolejność w strumieniu i wymaga sprawdzenia ręcznego (docs/DECYZJE.md D-40).
/// </summary>
public sealed class AutoTagService
{
    // Limity na dane z pliku; strona ponad limit jest pomijana i wymieniana w wyniku.
    public const int MaxContentBytes = 32 * 1024 * 1024;
    /// <summary>Budżety całego dokumentu: bajty treści odczytane, bajty treści przepisane, elementy modeli w pamięci.</summary>
    public const long MaxDocumentDecodedBytes = 1024L * 1024 * 1024;
    public const long MaxDocumentRewrittenBytes = 512L * 1024 * 1024;
    public const long MaxDocumentItems = 2_000_000;
    public const int MaxOpsPerPage = 500_000;
    public const int MaxGlyphsPerPage = 1_000_000;
    public const int MaxLinesPerPage = 20_000;
    public const int MaxBlocksPerPage = 20_000;
    private const int MaxLettersPerPage = 500_000;
    private const int MaxExaminedLetters = 2_000;
    private const int MaxFormDepth = 16;
    private const double MarginFraction = 0.08;

    private static readonly Regex NumberedItem = new(@"^\(?(\d{1,3}|[a-zA-Z])[.)]\s", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PageNumberOnly = new(@"^(strona|str\.?|page|p\.?)?\s*#(\s*(z|/|of|-)\s*#)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly string BulletChars = new(new[] { (char)0x2022, (char)0x25E6, (char)0x25AA, (char)0x2023, (char)0x00B7, (char)0x25CF, (char)0x25A0, (char)0x25CB });

    private readonly IPdfRenderer? _renderer;

    public AutoTagService(IPdfRenderer? renderer = null) => _renderer = renderer;

    /// <summary>Czy dokument ma już strukturę.</summary>
    public Task<bool> IsTaggedAsync(byte[] pdf, CancellationToken ct = default) => Task.Run(() =>
    {
        using var doc = PdfSharpEdit.OpenForImport(pdf);
        var catalog = doc.Internals.Catalog;
        return PdfObjects.Resolve(catalog.Elements["/StructTreeRoot"]) is PdfDictionary
            || (PdfObjects.Resolve(catalog.Elements["/MarkInfo"]) is PdfDictionary mi && mi.Elements.GetBoolean("/Marked"));
    }, ct);

    public async Task<AutoTagResult> TagAsync(byte[] pdf, AutoTagOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var skipped = new Dictionary<int, AutoTagSkipReason>();
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(() => Tag(pdf, options, skipped, progress, ct), ct).ConfigureAwait(false);
            if (result.TaggedPages == 0) return result with { Pdf = pdf };
            if (_renderer is null) return result;
            var changed = await ChangedPagesAsync(pdf, result.Pdf, _renderer, skipped, ct).ConfigureAwait(false);
            if (changed.Count == 0) return result;
            if (attempt >= 2) throw new InvalidOperationException("Tagging changed the page rendering.").WithMessageKey("Edit.AutoTag.Error.Render");
            foreach (var page in changed) skipped[page] = AutoTagSkipReason.RenderChanged;
        }
    }

    private static async Task<List<int>> ChangedPagesAsync(byte[] before, byte[] after, IPdfRenderer renderer, Dictionary<int, AutoTagSkipReason> skipped, CancellationToken ct)
    {
        using var a = await renderer.OpenAsync(before, null, ct).ConfigureAwait(false);
        using var b = await renderer.OpenAsync(after, null, ct).ConfigureAwait(false);
        var changed = new List<int>();
        for (int page = 0; page < a.PageCount && page < b.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (skipped.ContainsKey(page)) continue;
            var size = a.Pages[page].DisplaySize;
            double scale = Math.Min(1.0, 2000.0 / Math.Max(Math.Max(size.Width, size.Height), 1));
            var left = await a.RenderPageAsync(page, new RenderRequest(scale), ct).ConfigureAwait(false);
            var right = await b.RenderPageAsync(page, new RenderRequest(scale), ct).ConfigureAwait(false);
            if (!left.Bgra.AsSpan().SequenceEqual(right.Bgra)) changed.Add(page);
        }
        return changed;
    }

    // ------------------------------------------------------------------ model strony

    private enum Role { P, H1, H2, H3, H4, H5, H6, LI, Figure }

    private sealed record TextOp(int Start, int End, PdfRect Box, double Size, bool Bold, int Glyphs, (long Frame, long Block, int Stream) Scope);

    private sealed class Line
    {
        public List<TextOp> Ops { get; } = new();
        public string Text { get; set; } = string.Empty;
        public PdfRect Box;
        public double Size;
        public bool Bold;
        public int Glyphs;
        public bool Artifact;
    }

    private sealed class Block
    {
        public Role Role = Role.P;
        public List<Line> Lines { get; } = new();
        public (int Start, int End) FigureRange;
        public PdfRect Box;
    }

    private sealed class PageModel
    {
        public int Index;
        public required PdfPage Page;
        /// <summary>Bajtów całej treści; same bajty nie są trzymane między analizą a zapisem.</summary>
        public required int DataLength;
        public required int[] StreamStarts;
        public List<(int Start, int Length)> Removals { get; } = new();
        /// <summary>Elementy strony w kolejności strumienia: linie tekstu albo figury.</summary>
        public List<object> Items { get; } = new();
        /// <summary>Zakresy bajtów do oznaczenia jako artefakt.</summary>
        public List<(int Start, int End)> ArtifactRanges { get; } = new();
        public List<Block> Blocks { get; } = new();
    }

    private sealed class Counters
    {
        public int Tagged, Headings, Paragraphs, Lists, Figures, Artifacts;
    }

    private AutoTagResult Tag(byte[] pdf, AutoTagOptions options, Dictionary<int, AutoTagSkipReason> skipped, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var counters = new Counters();
        var bytes = PdfSharpEdit.Modify(pdf, doc =>
        {
            var models = new List<PageModel>();
            using var pig = TryOpenPig(pdf);
            long decoded = 0, items = 0;
            var knownStreamSize = new Dictionary<PdfDictionary, long>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < doc.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(i, doc.PageCount * 2));
                if (skipped.ContainsKey(i)) continue;
                try
                {
                    // Strumień współdzielony przez strony: znany rozmiar liczy się do budżetu przed ponownym dekodowaniem.
                    var streams = PageContent.Streams(doc.Pages[i]);
                    long known = streams.Sum(s => knownStreamSize.GetValueOrDefault(s));
                    if (decoded + known > MaxDocumentDecodedBytes) { skipped[i] = AutoTagSkipReason.TooLarge; continue; }
                    var data = PageContent.ReadAll(doc.Pages[i], MaxContentBytes, out var streamStarts);
                    foreach (var s in streams) knownStreamSize.TryAdd(s, data.Length / Math.Max(1, streams.Count));
                    decoded += data.Length;
                    if (decoded > MaxDocumentDecodedBytes) { skipped[i] = AutoTagSkipReason.TooLarge; continue; }
                    var model = Analyze(doc.Pages[i], i, data, streamStarts, pig, ct);
                    if (model is null) { skipped[i] = AutoTagSkipReason.TooLarge; continue; }
                    items += model.Items.Count + model.ArtifactRanges.Count + model.Removals.Count + model.Items.OfType<Line>().Sum(l => l.Ops.Count);
                    if (items > MaxDocumentItems) { skipped[i] = AutoTagSkipReason.TooLarge; continue; }
                    models.Add(model);
                }
                catch (PdfContentLimitException)
                {
                    skipped[i] = AutoTagSkipReason.TooLarge;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    skipped[i] = AutoTagSkipReason.Unreadable;
                }
            }
            Classify(models, ct);
            WriteStructure(doc, models, options, counters, skipped, progress, ct);
        });
        return new AutoTagResult(bytes, counters.Tagged, counters.Headings, counters.Paragraphs, counters.Lists, counters.Figures, counters.Artifacts,
            skipped.OrderBy(kv => kv.Key).Select(kv => new AutoTagSkippedPage(kv.Key, kv.Value)).ToList());
    }

    private static PigDocument? TryOpenPig(byte[] pdf)
    {
        try { return PigDocument.Open(pdf); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    // ------------------------------------------------------------------ analiza

    /// <summary>Null, gdy strona przekracza limit linii lub operatorów.</summary>
    private static PageModel? Analyze(PdfPage page, int index, byte[] data, int[] starts, PigDocument? pig, CancellationToken ct)
    {
        List<OpToken> tokens;
        ContentWalker walker;
        try
        {
            tokens = ContentTokenizer.Scan(data, MaxOpsPerPage, ct);
            walker = new ContentWalker(page, MaxGlyphsPerPage, data); // inny wyjątek parsera: strona nieczytelna
        }
        catch (ContentTokenException ex) when (ex.IsLimit) { return null; }
        var seqToToken = MapOperators(walker.Sequence, tokens);

        var model = new PageModel { Page = page, Index = index, DataLength = data.Length, StreamStarts = starts };
        (int Start, int End) Range(int firstSeq, int lastSeq) => (tokens[seqToToken[firstSeq]].Start, tokens[seqToToken[lastSeq]].End);
        var fonts = new Dictionary<string, bool>();
        var glyphsByOp = walker.Glyphs.GroupBy(g => g.OpIndex).ToDictionary(g => g.Key, g => g.ToList());
        var objectAt = walker.Objects.Where(o => o.Kind != PageObjectKind.Text).GroupBy(o => o.Start).ToDictionary(g => g.Key, g => g.First());
        var removeBdc = new HashSet<int>();

        // Stos znaczników: id ramki, czy artefakt, czy do usunięcia.
        var stack = new Stack<(long Id, bool Artifact, bool Remove)>();
        long nextFrame = 1, frame = 0, block = 0;
        int artifactDepth = 0;
        Line? line = null;
        int seqCount = walker.Sequence.Count;

        for (int i = 0; i < seqCount; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (walker.Sequence[i] is not COperator op) continue;
            var name = op.OpCode.Name;
            switch (name)
            {
                case "BDC":
                case "BMC":
                {
                    var operands = MarkOperands(walker.Sequence, i, op);
                    bool artifact = operands.Count > 0 && operands[0] is CName { Name: "/Artifact" };
                    bool mcid = name == "BDC" && HasMcid(page, operands);
                    stack.Push((frame, artifact, mcid));
                    frame = nextFrame++;
                    if (artifact) artifactDepth++;
                    if (mcid) model.Removals.Add(TokenRange(tokens, seqToToken, i));
                    continue;
                }
                case "EMC":
                {
                    if (stack.Count == 0) throw new ContentTokenException("Unbalanced EMC.");
                    var top = stack.Pop();
                    frame = top.Id;
                    if (top.Artifact) artifactDepth--;
                    if (top.Remove) model.Removals.Add(TokenRange(tokens, seqToToken, i));
                    continue;
                }
                case "BT":
                    block = i + 1;
                    line = null;
                    continue;
                case "ET":
                    block = 0;
                    continue;
            }

            if (objectAt.TryGetValue(i, out var obj))
            {
                if (artifactDepth > 0) continue;
                if (obj.Kind == PageObjectKind.Shape)
                {
                    // ścieżki to ozdoba: tabela, ramka, wykres wektorowy (ograniczenie wersji podstawowej)
                    if (!CrossesFrame(walker.Sequence, i, obj.End)) model.ArtifactRanges.Add(Range(i, obj.End));
                }
                else
                {
                    if (model.Items.Count >= MaxLinesPerPage) return null;
                    model.Items.Add(new Block { Role = Role.Figure, FigureRange = Range(i, i), Box = obj.Bounds });
                    line = null;
                }
                continue;
            }
            if (name is "sh" or "EI" or "BI" or "ID")
            {
                if (artifactDepth == 0 && seqToToken[i] >= 0 && name is "sh" or "EI") model.ArtifactRanges.Add(Range(i, i));
                continue;
            }
            if (name is not ("Tj" or "TJ" or "'" or "\"")) continue;
            if (artifactDepth > 0) continue;

            if (!glyphsByOp.TryGetValue(i, out var glyphs) || glyphs.Count == 0 || glyphs.All(g => g.Code == 32 && !g.TwoByte))
            {
                // same spacje: w środku wiersza zostają w zakresie jego znacznika, poza wierszem to artefakt
                if (line is null) model.ArtifactRanges.Add(Range(i, i));
                continue;
            }
            var visible = glyphs.Where(g => g.Code != 32 || g.TwoByte).ToList();
            var box = visible.Select(g => g.Box).Aggregate((a, b) => a.Union(b));
            double size = visible.Max(g => g.Box.Height);
            var scope = (frame, block, StreamOf(starts, tokens[seqToToken[i]].End - 1));
            var (opStart, opEnd) = Range(i, i);
            var text = new TextOp(opStart, opEnd, box, size, IsBold(page, visible[0].Font, fonts), visible.Count, scope);

            bool sameLine = line is not null && Math.Abs(line.Box.Bottom - box.Bottom) < 0.5 * Math.Max(line.Size, size)
                && box.Left - line.Box.Right < 3 * Math.Max(line.Size, size) && box.Right > line.Box.Left;
            if (!sameLine)
            {
                if (model.Items.Count >= MaxLinesPerPage) return null;
                line = new Line { Box = box, Size = size, Bold = true };
                model.Items.Add(line);
            }
            line!.Ops.Add(text);
            line.Box = line.Box.Union(box);
            line.Size = Math.Max(line.Size, size);
            line.Glyphs += visible.Count;
            line.Bold &= text.Bold;
        }
        if (stack.Count != 0) throw new ContentTokenException("Unbalanced BDC.");
        ReadLineTexts(model, pig, ct);
        return model;
    }

    private static bool CrossesFrame(CSequence seq, int first, int last)
    {
        for (int i = first; i <= last; i++)
            if (seq[i] is COperator { OpCode.Name: "BDC" or "BMC" or "EMC" or "BT" or "ET" }) return true;
        return false;
    }

    /// <summary>Indeks operatora PDFsharp → token w bajtach. Niezgodność nazw (np. dane obrazu w treści odczytane jak operatory) oznacza stronę nieczytelną.</summary>
    private static int[] MapOperators(CSequence seq, List<OpToken> tokens)
    {
        var map = new int[seq.Count];
        int t = 0;
        for (int i = 0; i < seq.Count; i++)
        {
            map[i] = -1;
            if (seq[i] is not COperator op) continue;
            var name = op.OpCode.Name;
            if (name == "Dictionary") continue; // słownik w treści: operand następnego BDC
            if (t < tokens.Count && tokens[t].InlineImage)
            {
                if (name is "BI" or "ID") continue;
                if (name == "EI") { map[i] = t++; continue; }
                throw new ContentTokenException("Inline image mismatch.");
            }
            if (t >= tokens.Count || tokens[t].Name != name) throw new ContentTokenException("Operator mismatch.");
            map[i] = t++;
        }
        if (t != tokens.Count) throw new ContentTokenException("Operator count mismatch.");
        return map;
    }

    private static (int Start, int Length) TokenRange(List<OpToken> tokens, int[] map, int seqIndex)
    {
        var t = tokens[map[seqIndex]];
        return (t.Start, t.End - t.Start);
    }

    private static int StreamOf(IReadOnlyList<int> starts, int offset)
    {
        int lo = 0, hi = starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (starts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Operandy BDC. PDFsharp 6.2.4 przy słowniku w treści tworzy osobny operator o nazwie „Dictionary” z operandami, a BDC zostaje bez operandów
    /// (sprawdzone uruchomieniem).
    /// </summary>
    private static CSequence MarkOperands(CSequence seq, int index, COperator op) =>
        op.Operands.Count == 0 && index > 0 && seq[index - 1] is COperator { OpCode.Name: "Dictionary" } prev ? prev.Operands : op.Operands;

    /// <summary>Czy BDC niesie MCID: słownik w treści albo nazwa z /Properties zasobów.</summary>
    private static bool HasMcid(PdfPage page, CSequence operands)
    {
        if (operands.Count < 2) return false;
        if (operands[1] is CString s) return s.Value.Contains("/MCID", StringComparison.Ordinal);
        if (operands[1] is CName n && PdfObjects.Resolve(page.Elements["/Resources"]) is PdfDictionary res
            && PdfObjects.Resolve(res.Elements["/Properties"]) is PdfDictionary props && PdfObjects.Resolve(props.Elements[n.Name]) is PdfDictionary d)
            return d.Elements.ContainsKey("/MCID");
        return false;
    }

    private static bool IsBold(PdfPage page, string? font, Dictionary<string, bool> cache)
    {
        if (font is null) return false;
        if (cache.TryGetValue(font, out var known)) return known;
        bool bold = false;
        if (PdfObjects.Resolve(page.Elements["/Resources"]) is PdfDictionary res && PdfObjects.Resolve(res.Elements["/Font"]) is PdfDictionary fonts
            && PdfObjects.Resolve(fonts.Elements[font]) is PdfDictionary f)
        {
            var name = f.Elements.GetName("/BaseFont");
            bold = name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase) || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
            var descriptor = PdfObjects.Resolve(f.Elements["/FontDescriptor"]) as PdfDictionary;
            if (descriptor is null && PdfObjects.Resolve(f.Elements["/DescendantFonts"]) is PdfArray { Elements.Count: > 0 } df && PdfObjects.Resolve(df.Elements[0]) is PdfDictionary cid)
                descriptor = PdfObjects.Resolve(cid.Elements["/FontDescriptor"]) as PdfDictionary;
            if (descriptor is not null)
                bold |= PdfObjects.Number(descriptor.Elements["/FontWeight"]) >= 700 || ((int)PdfObjects.Number(descriptor.Elements["/Flags"]) & (1 << 18)) != 0;
        }
        cache[font] = bold;
        return bold;
    }

    /// <summary>Tekst linii z PdfPig (kubełki po Y, ograniczony przegląd). Bez PdfPig tekst pusty: nie ma list ani powtarzanych nagłówków.</summary>
    private static void ReadLineTexts(PageModel model, PigDocument? pig, CancellationToken ct)
    {
        if (pig is null) return;
        IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters;
        try { letters = pig.GetPage(model.Index + 1).Letters; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        if (letters.Count == 0 || letters.Count > MaxLettersPerPage) return;
        const double bucket = 12;
        var byY = new Dictionary<int, List<UglyToad.PdfPig.Content.Letter>>();
        foreach (var l in letters)
        {
            double cy = (l.BoundingBox.Bottom + l.BoundingBox.Top) / 2;
            if (!double.IsFinite(cy)) continue;
            int key = (int)Math.Clamp(cy / bucket, -100_000, 100_000);
            if (!byY.TryGetValue(key, out var list)) byY[key] = list = new();
            list.Add(l);
        }
        foreach (var line in model.Items.OfType<Line>())
        {
            ct.ThrowIfCancellationRequested();
            var found = new List<UglyToad.PdfPig.Content.Letter>();
            int examined = 0;
            int from = (int)Math.Clamp(line.Box.Bottom / bucket, -100_000, 100_000), to = Math.Min(from + 20, (int)Math.Clamp(line.Box.Top / bucket, -100_000, 100_000));
            for (int key = from; key <= to && examined < MaxExaminedLetters; key++)
            {
                if (!byY.TryGetValue(key, out var list)) continue;
                foreach (var l in list)
                {
                    if (++examined > MaxExaminedLetters) break;
                    double cx = (l.BoundingBox.Left + l.BoundingBox.Right) / 2, cy = (l.BoundingBox.Bottom + l.BoundingBox.Top) / 2;
                    if (cx >= line.Box.Left - 1 && cx <= line.Box.Right + 1 && cy >= line.Box.Bottom && cy <= line.Box.Top) found.Add(l);
                }
            }
            var sb = new StringBuilder();
            double lastRight = double.NaN;
            foreach (var l in found.OrderBy(l => l.BoundingBox.Left))
            {
                if (sb.Length >= 200) break;
                if (!double.IsNaN(lastRight) && l.BoundingBox.Left - lastRight > 0.2 * Math.Max(line.Size, 1)) sb.Append(' ');
                sb.Append(l.Value);
                lastRight = l.BoundingBox.Right;
            }
            line.Text = sb.ToString().Trim();
        }
    }

    // ------------------------------------------------------------------ klasyfikacja

    private static string RunningKey(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsDigit(c))
            {
                if (sb.Length == 0 || sb[^1] != '#') sb.Append('#');
            }
            else sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Trim();
    }

    private static void Classify(List<PageModel> models, CancellationToken ct)
    {
        // 1. Nagłówki i stopki: powtarzające się teksty w marginesach albo sam numer strony.
        var seen = new Dictionary<string, int>();
        var inMargin = new List<(Line Line, string Key)>();
        foreach (var m in models)
        {
            ct.ThrowIfCancellationRequested();
            var box = m.Page.MediaBox;
            double y0 = Math.Min(box.Y1, box.Y2), y1 = Math.Max(box.Y1, box.Y2), margin = (y1 - y0) * MarginFraction;
            var here = new HashSet<string>();
            foreach (var line in m.Items.OfType<Line>())
            {
                if (line.Text.Length == 0 || (line.Box.Top < y1 - margin && line.Box.Bottom > y0 + margin)) continue;
                var key = RunningKey(line.Text);
                inMargin.Add((line, key));
                if (here.Add(key)) seen[key] = seen.GetValueOrDefault(key) + 1;
            }
        }
        int repeatNeed = Math.Max(2, (int)Math.Ceiling(models.Count * 0.5));
        foreach (var (line, key) in inMargin)
        {
            bool pageNumber = key.Contains('#', StringComparison.Ordinal) && SafeMatch(PageNumberOnly, key);
            if (pageNumber || seen[key] >= repeatNeed) line.Artifact = true;
        }

        // 2. Rozmiar tekstu głównego: najczęstszy, ważony liczbą glifów.
        var sizes = new Dictionary<double, int>();
        int boldGlyphs = 0, allGlyphs = 0;
        foreach (var line in models.SelectMany(m => m.Items.OfType<Line>()).Where(l => !l.Artifact))
        {
            double key = Math.Round(line.Size * 2) / 2;
            sizes[key] = sizes.GetValueOrDefault(key) + line.Glyphs;
            allGlyphs += line.Glyphs;
            if (line.Bold) boldGlyphs += line.Glyphs;
        }
        double body = sizes.Count == 0 ? 0 : sizes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        bool mostlyBold = allGlyphs > 0 && boldGlyphs * 2 > allGlyphs;
        var headingSizes = sizes.Keys.Where(k => k >= body * 1.15).OrderByDescending(k => k).ToList();
        Role HeadingFor(double size)
        {
            int rank = headingSizes.IndexOf(Math.Round(size * 2) / 2);
            return Role.H1 + Math.Clamp(rank < 0 ? headingSizes.Count : rank, 0, 5);
        }

        // 3. Bloki.
        foreach (var m in models)
        {
            ct.ThrowIfCancellationRequested();
            Block? cur = null;
            Line? prev = null;
            var lines = m.Items.OfType<Line>().Where(l => !l.Artifact).ToList();
            var startsList = new HashSet<Line>(lines.Where(l => IsListStart(l.Text)));
            for (int i = 0; i < m.Items.Count; i++)
            {
                if (m.Blocks.Count >= MaxBlocksPerPage) break;
                if (m.Items[i] is Block figure)
                {
                    m.Blocks.Add(figure);
                    cur = null;
                    prev = null;
                    continue;
                }
                var line = (Line)m.Items[i];
                if (line.Artifact) continue;
                bool isHeading = body > 0 && Math.Round(line.Size * 2) / 2 >= body * 1.15;
                bool boldHeading = !isHeading && !mostlyBold && line.Bold && body > 0 && line.Glyphs <= 80 && Math.Round(line.Size * 2) / 2 >= body;
                bool listStart = startsList.Contains(line);
                Role role = isHeading ? HeadingFor(line.Size) : listStart ? Role.LI : Role.P;

                bool continues = cur is not null && prev is not null && !listStart && !boldHeading
                    && prev.Box.Bottom - line.Box.Top < 1.2 * Math.Max(line.Size, prev.Size) && line.Box.Top <= prev.Box.Top
                    && (cur.Role == Role.LI ? !isHeading : role == cur.Role && Math.Abs(line.Size - prev.Size) < 0.75 && line.Bold == prev.Bold);
                if (continues)
                {
                    cur!.Lines.Add(line);
                }
                else
                {
                    cur = new Block { Role = role };
                    if (boldHeading)
                    {
                        // Pogrubiony wiersz tej samej wielkości co tekst: nagłówek najniższego poziomu, gdy następny wiersz nie jest pogrubiony.
                        var next = NextLine(m.Items, i);
                        bool single = next is null || !next.Bold || prev is null;
                        if (single) cur.Role = (Role)Math.Min((int)Role.H6, (int)Role.H1 + headingSizes.Count);
                    }
                    cur.Lines.Add(line);
                    m.Blocks.Add(cur);
                }
                prev = line;
            }
            // Pojedynczy punkt listy bez sąsiada to zwykły akapit.
            for (int i = 0; i < m.Blocks.Count; i++)
            {
                if (m.Blocks[i].Role != Role.LI) continue;
                bool neighbour = (i > 0 && m.Blocks[i - 1].Role == Role.LI) || (i + 1 < m.Blocks.Count && m.Blocks[i + 1].Role == Role.LI);
                if (!neighbour) m.Blocks[i].Role = Role.P;
            }
        }
    }

    private static Line? NextLine(List<object> items, int i)
    {
        for (int k = i + 1; k < items.Count; k++)
            if (items[k] is Line l && !l.Artifact) return l;
        return null;
    }

    private static bool SafeMatch(Regex regex, string text)
    {
        try { return regex.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool IsListStart(string text)
    {
        if (text.Length < 2) return false;
        char c = text[0];
        if (c is '-' or '*' || c == (char)0x2013 || c == (char)0x2014) return char.IsWhiteSpace(text[1]);
        return BulletChars.Contains(c) || SafeMatch(NumberedItem, text);
    }

    // ------------------------------------------------------------------ zapis

    private static void WriteStructure(PdfDocument doc, List<PageModel> models, AutoTagOptions options, Counters counters, Dictionary<int, AutoTagSkipReason> skipped, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        long rewritten = 0;
        var catalog = doc.Internals.Catalog;
        var root = new PdfDictionary(doc);
        doc.Internals.AddObject(root);
        root.Elements["/Type"] = new PdfName("/StructTreeRoot");
        var document = NewElement(doc, "/Document", root, null);
        var documentKids = new PdfArray(doc);
        var nums = new PdfArray(doc);
        int nextKey = 0;

        foreach (var page in doc.Pages)
        {
            ct.ThrowIfCancellationRequested();
            ClearOldStructureKeys(page);
        }

        foreach (var m in models)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(doc.PageCount + m.Index, doc.PageCount * 2));
            // Każda strona dostaje własną kopię zmienionych strumieni, także współdzielonych.
            rewritten += m.DataLength;
            if (rewritten > MaxDocumentRewrittenBytes)
            {
                skipped[m.Index] = AutoTagSkipReason.TooLarge;
                continue;
            }
            var edits = new List<(int Offset, string Insert, int RemoveLength)>();
            foreach (var (start, length) in m.Removals) edits.Add((start, string.Empty, length));
            var parents = new PdfArray(doc);
            int mcid = 0;

            void Mark(string tag, int start, int end, PdfDictionary element, PdfArray kids)
            {
                edits.Add((start, $"{tag} <</MCID {mcid.ToString(CultureInfo.InvariantCulture)}>> BDC\n", 0));
                edits.Add((end, "\nEMC\n", 0));
                kids.Elements.Add(new PdfInteger(mcid));
                parents.Elements.Add(element.Reference!);
                mcid++;
            }

            PdfDictionary? currentList = null;
            PdfArray? currentListKids = null;
            foreach (var block in m.Blocks)
            {
                if (block.Role == Role.Figure)
                {
                    currentList = null;
                    if (IsPageBackground(m.Page, block, m.Items))
                    {
                        // skan pod warstwą tekstu: tło, nie ilustracja
                        m.ArtifactRanges.Add(block.FigureRange);
                        continue;
                    }
                    var fig = NewElement(doc, "/Figure", document, m.Page);
                    var figKids = new PdfArray(doc);
                    Mark("/Figure", block.FigureRange.Start, block.FigureRange.End, fig, figKids);
                    fig.Elements["/K"] = figKids;
                    var attrs = new PdfDictionary(doc);
                    attrs.Elements["/O"] = new PdfName("/Layout");
                    attrs.Elements["/BBox"] = PdfObjects.RectArray(doc, block.Box);
                    fig.Elements["/A"] = attrs;
                    documentKids.Elements.Add(fig.Reference!);
                    counters.Figures++;
                    continue;
                }

                string type = block.Role switch { Role.LI => "/LBody", Role.P => "/P", var r => "/" + r };
                PdfDictionary holder;
                if (block.Role == Role.LI)
                {
                    if (currentList is null)
                    {
                        currentList = NewElement(doc, "/L", document, m.Page);
                        currentListKids = new PdfArray(doc);
                        currentList.Elements["/K"] = currentListKids;
                        documentKids.Elements.Add(currentList.Reference!);
                        counters.Lists++;
                    }
                    var item = NewElement(doc, "/LI", currentList, m.Page);
                    holder = NewElement(doc, "/LBody", item, m.Page);
                    var itemKids = new PdfArray(doc);
                    itemKids.Elements.Add(holder.Reference!);
                    item.Elements["/K"] = itemKids;
                    currentListKids!.Elements.Add(item.Reference!);
                }
                else
                {
                    currentList = null;
                    holder = NewElement(doc, type, document, m.Page);
                    documentKids.Elements.Add(holder.Reference!);
                    if (block.Role == Role.P) counters.Paragraphs++;
                    else counters.Headings++;
                }

                var kids = new PdfArray(doc);
                foreach (var line in block.Lines)
                {
                    int i = 0;
                    while (i < line.Ops.Count)
                    {
                        int j = i;
                        while (j + 1 < line.Ops.Count && line.Ops[j + 1].Scope == line.Ops[i].Scope) j++;
                        Mark(type, line.Ops[i].Start, line.Ops[j].End, holder, kids);
                        i = j + 1;
                    }
                }
                holder.Elements["/K"] = kids;
            }

            // Tekst odrzucony z bloków (powtarzane nagłówki, stopki) i ścieżki: artefakty.
            foreach (var line in m.Items.OfType<Line>().Where(l => l.Artifact))
            {
                foreach (var op in line.Ops) m.ArtifactRanges.Add((op.Start, op.End));
            }
            foreach (var (start, end) in m.ArtifactRanges)
            {
                edits.Add((start, "/Artifact BMC\n", 0));
                edits.Add((end, "\nEMC\n", 0));
                counters.Artifacts++;
            }

            ApplyEdits(doc, m, edits);
            m.Page.Elements["/StructParents"] = new PdfInteger(nextKey);
            m.Page.Elements["/Tabs"] = new PdfName("/S");
            nums.Elements.Add(new PdfInteger(nextKey));
            doc.Internals.AddObject(parents);
            nums.Elements.Add(parents.Reference!);
            nextKey++;
            counters.Tagged++;
        }

        document.Elements["/K"] = documentKids;
        var rootKids = new PdfArray(doc);
        rootKids.Elements.Add(document.Reference!);
        root.Elements["/K"] = rootKids;
        var parentTree = new PdfDictionary(doc);
        doc.Internals.AddObject(parentTree);
        parentTree.Elements["/Nums"] = nums;
        root.Elements["/ParentTree"] = parentTree.Reference!;
        root.Elements["/ParentTreeNextKey"] = new PdfInteger(nextKey);
        catalog.Elements["/StructTreeRoot"] = root.Reference!;
        var markInfo = new PdfDictionary(doc);
        markInfo.Elements["/Marked"] = new PdfBoolean(true);
        catalog.Elements["/MarkInfo"] = markInfo;
        if (string.IsNullOrWhiteSpace(catalog.Elements.GetString("/Lang")) && !string.IsNullOrWhiteSpace(options.Language))
            catalog.Elements["/Lang"] = new PdfString(options.Language);
    }

    /// <summary>Obraz na co najmniej 80% strony przy tekście na stronie: skan z warstwą OCR.</summary>
    private static bool IsPageBackground(PdfPage page, Block block, List<object> items)
    {
        if (!items.OfType<Line>().Any()) return false;
        var m = page.MediaBox;
        double pageArea = Math.Abs(m.X2 - m.X1) * Math.Abs(m.Y2 - m.Y1);
        return pageArea > 0 && block.Box.Width * block.Box.Height >= pageArea * 0.8;
    }

    private static PdfDictionary NewElement(PdfDocument doc, string type, PdfDictionary parent, PdfPage? page)
    {
        var e = new PdfDictionary(doc);
        doc.Internals.AddObject(e);
        e.Elements["/Type"] = new PdfName("/StructElem");
        e.Elements["/S"] = new PdfName(type);
        e.Elements["/P"] = parent.Reference!;
        if (page is not null) e.Elements["/Pg"] = page.Reference!;
        return e;
    }

    /// <summary>Stare klucze wskazujące drzewo struktury: zastępowane drzewo ma nowe numery. Formularze z limitem głębokości i ochroną przed cyklem.</summary>
    private static void ClearOldStructureKeys(PdfPage page)
    {
        page.Elements.Remove("/StructParents");
        foreach (var annot in PdfObjects.Annotations(page)) annot.Elements.Remove("/StructParent");
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Resources, int Depth)>();
        if (PdfObjects.Resolve(page.Elements["/Resources"]) is PdfDictionary res) stack.Push((res, 0));
        while (stack.Count > 0)
        {
            var (resources, depth) = stack.Pop();
            if (depth >= MaxFormDepth || PdfObjects.Resolve(resources.Elements["/XObject"]) is not PdfDictionary xo) continue;
            foreach (var key in xo.Elements.Keys.Take(10_000))
            {
                if (PdfObjects.Resolve(xo.Elements[key]) is not PdfDictionary form || !visited.Add(form)) continue;
                form.Elements.Remove("/StructParents");
                form.Elements.Remove("/StructParent");
                if (PdfObjects.Resolve(form.Elements["/Resources"]) is PdfDictionary inner) stack.Push((inner, depth + 1));
            }
        }
    }

    /// <summary>
    /// Wstawienia do oryginalnych bajtów, strumień po strumieniu. Zmieniony strumień staje się nowym obiektem (strumień bywa współdzielony
    /// przez kilka stron), ze skopiowanymi kluczami słownika; bez zmian zostaje ten sam obiekt.
    /// </summary>
    private static void ApplyEdits(PdfDocument doc, PageModel m, List<(int Offset, string Insert, int RemoveLength)> edits)
    {
        if (edits.Count == 0) return;
        var streams = PageContent.Streams(m.Page);
        var data = PageContent.ReadAll(m.Page, MaxContentBytes);
        // Zamknięcia przed otwarciami w tym samym miejscu: sąsiednie zakresy nie mogą się przeplatać.
        var ordered = edits.Select((e, i) => (e.Offset, e.Insert, e.RemoveLength, i))
            .OrderBy(e => e.Offset).ThenBy(e => e.Insert.StartsWith('\n') ? 0 : 1).ThenBy(e => e.i).ToList();
        var result = new PdfArray(doc);
        int cursor = 0;
        for (int s = 0; s < streams.Count; s++)
        {
            int start = m.StreamStarts[s];
            int end = s + 1 < streams.Count ? m.StreamStarts[s + 1] - 1 : data.Length - 1;
            int firstEdit = cursor;
            while (cursor < ordered.Count && ordered[cursor].Offset <= end) cursor++;
            var here = ordered.GetRange(firstEdit, cursor - firstEdit);
            if (here.Count == 0)
            {
                result.Elements.Add(streams[s].Reference is not null ? streams[s].Reference! : streams[s]);
                continue;
            }
            using var ms = new MemoryStream(end - start + here.Count * 24);
            int pos = start;
            foreach (var e in here)
            {
                if (e.Offset > pos) ms.Write(data, pos, e.Offset - pos);
                pos = Math.Max(pos, e.Offset);
                if (e.Insert.Length > 0) ms.Write(Encoding.ASCII.GetBytes(e.Insert));
                pos += e.RemoveLength;
            }
            if (end > pos) ms.Write(data, pos, end - pos);
            var copy = new PdfDictionary(doc);
            foreach (var key in streams[s].Elements.Keys)
                if (key is not ("/Filter" or "/DecodeParms" or "/Length" or "/DL" or "/F" or "/FFilter" or "/FDecodeParms"))
                    copy.Elements[key] = streams[s].Elements[key];
            copy.CreateStream(ms.ToArray());
            doc.Internals.AddObject(copy);
            result.Elements.Add(copy.Reference!);
        }
        m.Page.Elements["/Contents"] = result;
    }
}

using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using ZetPDF.Core.Contracts;
using ZetPDF.Security.Compare;
using CompareOptions = ZetPDF.Core.Contracts.CompareOptions;

namespace ZetPDF.Security;

/// <summary>
/// Porównanie dwóch PDF: słowa z pozycjami (PdfPig) z całych dokumentów → różnice słowo po słowie (Myers,
/// własna implementacja) → zmiany (wstawione/usunięte/zmienione) z prostokątami po obu stronach, dopasowanie
/// stron (wg liczby wspólnych słów). Opcjonalnie różnice graficzne z porównania renderów (IPdfRenderer).
/// </summary>
public sealed class CompareService : ICompareService
{
    private readonly IServiceProvider? _services;

    public CompareService() { }

    public CompareService(IServiceProvider services) => _services = services;

    internal IPdfRenderer? Renderer { get; set; }

    private IPdfRenderer? ResolveRenderer() => Renderer ?? _services?.GetService<IPdfRenderer>();

    internal sealed record Word(string Text, string Key, int Page, PdfRect Box);

    public async Task<CompareResult> CompareAsync(byte[] pdfA, string nameA, byte[] pdfB, string nameB, CompareOptions options,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdfA);
        ArgumentNullException.ThrowIfNull(pdfB);
        options ??= new CompareOptions();
        var (wordsA, countA, sizesA) = await Task.Run(() => ExtractWords(pdfA, options, ct), ct).ConfigureAwait(false);
        progress?.Report(new OperationProgress(0.25));
        var (wordsB, countB, sizesB) = await Task.Run(() => ExtractWords(pdfB, options, ct), ct).ConfigureAwait(false);
        progress?.Report(new OperationProgress(0.5));

        var (changes, pairCounts) = await Task.Run(() => ComputeChanges(wordsA, wordsB, ct), ct).ConfigureAwait(false);
        var pageMap = BuildPageMap(countA, countB, pairCounts);
        progress?.Report(new OperationProgress(0.7));

        if (options.CompareGraphics && ResolveRenderer() is { } renderer)
        {
            var graphic = await CompareGraphicsAsync(renderer, pdfA, pdfB, pageMap, sizesA, sizesB, changes, progress, ct).ConfigureAwait(false);
            changes.AddRange(graphic);
            changes.Sort((x, y) => Order(x).CompareTo(Order(y)));
        }
        var numbered = changes.Select((c, i) => c with { Index = i }).ToList();
        progress?.Report(new OperationProgress(1));
        return new CompareResult(nameA, nameB, countA, countB, pageMap, numbered);
    }

    private static (int, double, int, double) Order(CompareChange c)
    {
        int page = c.PageA >= 0 ? c.PageA : c.PageB;
        var r = c.RectsA.Count > 0 ? c.RectsA[0] : c.RectsB.Count > 0 ? c.RectsB[0] : default;
        return (page, -r.Top, c.PageB, r.Left);
    }

    internal static (List<Word> Words, int PageCount, List<PdfSize> Sizes) ExtractWords(byte[] pdf, CompareOptions options, CancellationToken ct)
    {
        var words = new List<Word>();
        var sizes = new List<PdfSize>();
        using var doc = PdfDocument.Open(pdf);
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = doc.GetPage(p);
            sizes.Add(new PdfSize(page.Width, page.Height));
            foreach (var w in page.GetWords())
            {
                var text = w.Text;
                if (options.IgnoreWhitespace) text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
                if (string.IsNullOrEmpty(text)) continue;
                var key = options.IgnoreCase ? text.ToLower(CultureInfo.InvariantCulture) : text;
                var b = w.BoundingBox;
                words.Add(new Word(text, key, p - 1, new PdfRect(b.Left, b.Bottom, b.Right, b.Top)));
            }
        }
        return (words, doc.NumberOfPages, sizes);
    }

    internal static (List<CompareChange> Changes, Dictionary<(int, int), int> PairCounts) ComputeChanges(IReadOnlyList<Word> a, IReadOnlyList<Word> b, CancellationToken ct)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string k) { if (!ids.TryGetValue(k, out var v)) { v = ids.Count; ids[k] = v; } return v; }
        var ia = a.Select(w => Id(w.Key)).ToArray();
        var ib = b.Select(w => Id(w.Key)).ToArray();
        var edits = MyersDiff.Diff(ia, ib, ct);

        var pairs = new Dictionary<(int, int), int>();
        var changes = new List<CompareChange>();
        var del = new List<int>();
        var ins = new List<int>();
        int lastA = -1, lastB = -1;

        void Flush()
        {
            if (del.Count == 0 && ins.Count == 0) return;
            var kind = del.Count > 0 && ins.Count > 0 ? CompareChangeKind.Changed : del.Count > 0 ? CompareChangeKind.Deleted : CompareChangeKind.Inserted;
            int pageA = del.Count > 0 ? a[del[0]].Page : lastA >= 0 ? a[lastA].Page : (a.Count > 0 ? a[0].Page : -1);
            int pageB = ins.Count > 0 ? b[ins[0]].Page : lastB >= 0 ? b[lastB].Page : (b.Count > 0 ? b[0].Page : -1);
            var rectsA = MergeLines(del.Select(i => a[i]));
            var rectsB = MergeLines(ins.Select(i => b[i]));
            // wstawienie/usunięcie: po drugiej stronie wskazujemy miejsce (słowo przed zmianą), żeby widok obok siebie miał punkt odniesienia
            if (rectsA.Count == 0 && lastA >= 0) rectsA = new List<PdfRect> { Caret(a[lastA].Box) };
            if (rectsB.Count == 0 && lastB >= 0) rectsB = new List<PdfRect> { Caret(b[lastB].Box) };
            changes.Add(new CompareChange(changes.Count, kind, pageA, pageB,
                string.Join(" ", del.Select(i => a[i].Text)), string.Join(" ", ins.Select(i => b[i].Text)), rectsA, rectsB));
            del.Clear();
            ins.Clear();
        }

        foreach (var e in edits)
        {
            switch (e.Op)
            {
                case DiffOp.Equal:
                    Flush();
                    lastA = e.IndexA; lastB = e.IndexB;
                    var key = (a[e.IndexA].Page, b[e.IndexB].Page);
                    pairs[key] = pairs.TryGetValue(key, out var n) ? n + 1 : 1;
                    break;
                case DiffOp.Delete:
                    // zmiana strony w środku bloku → osobne zmiany na każdej stronie
                    if (del.Count > 0 && a[del[^1]].Page != a[e.IndexA].Page) Flush();
                    del.Add(e.IndexA);
                    break;
                case DiffOp.Insert:
                    if (ins.Count > 0 && b[ins[^1]].Page != b[e.IndexB].Page) Flush();
                    ins.Add(e.IndexB);
                    break;
            }
        }
        Flush();
        return (changes, pairs);
    }

    private static PdfRect Caret(PdfRect r) => new(r.Right, r.Bottom, r.Right + 2, r.Top);

    /// <summary>Prostokąty słów scalone w wiersze.</summary>
    internal static List<PdfRect> MergeLines(IEnumerable<Word> words)
    {
        var result = new List<PdfRect>();
        foreach (var w in words)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                double overlap = Math.Min(last.Top, w.Box.Top) - Math.Max(last.Bottom, w.Box.Bottom);
                if (overlap > 0.5 * Math.Min(last.Height, w.Box.Height) && w.Box.Left >= last.Left - 1)
                {
                    result[^1] = last.Union(w.Box);
                    continue;
                }
            }
            result.Add(w.Box);
        }
        return result;
    }

    internal static IReadOnlyList<(int PageA, int PageB)> BuildPageMap(int countA, int countB, Dictionary<(int, int), int> pairs)
    {
        var map = new List<(int, int)>();
        var usedB = new HashSet<int>();
        int lastB = -1;
        for (int pa = 0; pa < countA; pa++)
        {
            var best = pairs.Where(p => p.Key.Item1 == pa && p.Key.Item2 > lastB && !usedB.Contains(p.Key.Item2))
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key.Item2).Select(p => (int?)p.Key.Item2).FirstOrDefault();
            if (best is { } pb)
            {
                map.Add((pa, pb));
                usedB.Add(pb);
                lastB = pb;
            }
            else map.Add((pa, -1));
        }
        for (int pb = 0; pb < countB; pb++)
            if (!usedB.Contains(pb)) map.Add((-1, pb));
        // strony bez słów (np. puste) dopasowujemy wg kolejności, jeśli oba dokumenty mają tyle samo stron
        if (countA == countB)
        {
            for (int pa = 0; pa < countA; pa++)
            {
                if (usedB.Contains(pa)) continue;
                int i = map.IndexOf((pa, -1));
                if (i < 0) continue;
                map[i] = (pa, pa);
                usedB.Add(pa);
                map.Remove((-1, pa));
            }
        }
        return map;
    }

    private static async Task<List<CompareChange>> CompareGraphicsAsync(IPdfRenderer renderer, byte[] pdfA, byte[] pdfB,
        IReadOnlyList<(int PageA, int PageB)> map, List<PdfSize> sizesA, List<PdfSize> sizesB, List<CompareChange> textChanges,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        const double scale = 0.5; // 36 DPI – wystarcza do wykrycia zmian grafiki
        const int cell = 6;
        var result = new List<CompareChange>();
        using var docA = await renderer.OpenAsync(pdfA, null, ct).ConfigureAwait(false);
        using var docB = await renderer.OpenAsync(pdfB, null, ct).ConfigureAwait(false);
        var pairs = map.Where(m => m.PageA >= 0 && m.PageB >= 0).ToList();
        int done = 0;
        foreach (var (pa, pb) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(0.7 + 0.3 * done++ / Math.Max(1, pairs.Count)));
            var infoA = docA.Pages[pa];
            var infoB = docB.Pages[pb];
            if (infoA.Rotation != 0 || infoB.Rotation != 0) continue; // przeliczenie współrzędnych tylko dla stron bez /Rotate
            if (Math.Abs(infoA.Size.Width - infoB.Size.Width) > 1 || Math.Abs(infoA.Size.Height - infoB.Size.Height) > 1) continue;
            var bmpA = await docA.RenderPageAsync(pa, new RenderRequest(scale, Flags: RenderFlags.Annotations), ct).ConfigureAwait(false);
            var bmpB = await docB.RenderPageAsync(pb, new RenderRequest(scale, Flags: RenderFlags.Annotations), ct).ConfigureAwait(false);
            if (bmpA.Width != bmpB.Width || bmpA.Height != bmpB.Height) continue;
            var cells = new List<PdfRect>();
            for (int cy = 0; cy < bmpA.Height; cy += cell)
            {
                for (int cx = 0; cx < bmpA.Width; cx += cell)
                {
                    long diff = 0; int n = 0;
                    for (int y = cy; y < Math.Min(cy + cell, bmpA.Height); y++)
                    {
                        int row = y * bmpA.Stride;
                        for (int x = cx; x < Math.Min(cx + cell, bmpA.Width); x++)
                        {
                            int i = row + x * 4;
                            diff += Math.Abs(bmpA.Bgra[i] - bmpB.Bgra[i]) + Math.Abs(bmpA.Bgra[i + 1] - bmpB.Bgra[i + 1]) + Math.Abs(bmpA.Bgra[i + 2] - bmpB.Bgra[i + 2]);
                            n++;
                        }
                    }
                    if (n > 0 && diff / (3.0 * n) > 20)
                    {
                        double left = infoA.CropOrigin.X + cx / scale, right = infoA.CropOrigin.X + Math.Min(cx + cell, bmpA.Width) / scale;
                        double top = infoA.CropOrigin.Y + infoA.Size.Height - cy / scale, bottom = infoA.CropOrigin.Y + infoA.Size.Height - Math.Min(cy + cell, bmpA.Height) / scale;
                        cells.Add(new PdfRect(left, bottom, right, top));
                    }
                }
            }
            foreach (var region in Cluster(cells))
            {
                bool coveredByText = textChanges.Any(t =>
                    (t.PageA == pa && t.RectsA.Any(r => r.Inflate(4).IntersectsWith(region))) ||
                    (t.PageB == pb && t.RectsB.Any(r => r.Inflate(4).IntersectsWith(region))));
                if (coveredByText) continue;
                result.Add(new CompareChange(0, CompareChangeKind.Changed, pa, pb, string.Empty, string.Empty, new[] { region }, new[] { region }));
            }
        }
        return result;
    }

    private static List<PdfRect> Cluster(List<PdfRect> cells)
    {
        var regions = new List<PdfRect>();
        foreach (var c in cells)
        {
            var merged = c;
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = regions.Count - 1; i >= 0; i--)
                {
                    if (regions[i].Inflate(1).IntersectsWith(merged))
                    {
                        merged = merged.Union(regions[i]);
                        regions.RemoveAt(i);
                        changed = true;
                    }
                }
            }
            regions.Add(merged);
        }
        return regions;
    }

    // ------------------------------------------------------------------ raport

    public Task ExportReportAsync(CompareResult result, string path, CompareReportFormat format, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var culture = _services?.GetService<ILocalizationService>()?.CurrentCulture ?? CultureInfo.CurrentUICulture;
        var text = format == CompareReportFormat.Html ? BuildHtml(result, culture) : BuildText(result, culture);
        return WriteAsync(path, text, ct);
    }

    private static async Task WriteAsync(string path, string text, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(true), ct).ConfigureAwait(false);
        File.Move(tmp, full, overwrite: true);
    }

    private static string S(CultureInfo c, string key) => SecurityModule.Strings.GetString(key, c) ?? key;

    internal static string KindText(CompareChangeKind kind, CultureInfo c) => S(c, kind switch
    {
        CompareChangeKind.Inserted => "Security.Compare.Kind.Inserted",
        CompareChangeKind.Deleted => "Security.Compare.Kind.Deleted",
        _ => "Security.Compare.Kind.Changed",
    });

    internal static string PageText(int page) => page >= 0 ? (page + 1).ToString(CultureInfo.InvariantCulture) : "–";

    internal static string BuildText(CompareResult r, CultureInfo c)
    {
        var sb = new StringBuilder();
        sb.AppendLine(S(c, "Security.Compare.Report.Title"));
        sb.AppendLine(string.Format(c, S(c, "Security.Compare.Report.DocA"), r.NameA, r.PageCountA));
        sb.AppendLine(string.Format(c, S(c, "Security.Compare.Report.DocB"), r.NameB, r.PageCountB));
        sb.AppendLine(string.Format(c, S(c, "Security.Compare.Report.Summary"), r.Changes.Count,
            r.Changes.Count(x => x.Kind == CompareChangeKind.Inserted), r.Changes.Count(x => x.Kind == CompareChangeKind.Deleted), r.Changes.Count(x => x.Kind == CompareChangeKind.Changed)));
        sb.AppendLine(new string('-', 60));
        foreach (var ch in r.Changes)
        {
            sb.AppendLine(string.Format(c, S(c, "Security.Compare.Report.Item"), ch.Index + 1, KindText(ch.Kind, c), PageText(ch.PageA), PageText(ch.PageB)));
            if (ch.TextA.Length == 0 && ch.TextB.Length == 0) sb.AppendLine("    " + S(c, "Security.Compare.Graphic"));
            if (ch.TextA.Length > 0) sb.AppendLine("    - " + ch.TextA);
            if (ch.TextB.Length > 0) sb.AppendLine("    + " + ch.TextB);
        }
        return sb.ToString();
    }

    internal static string BuildHtml(CompareResult r, CultureInfo c)
    {
        static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(c.TwoLetterISOLanguageName).Append("\"><head><meta charset=\"utf-8\"><title>")
          .Append(E(S(c, "Security.Compare.Report.Title"))).Append("</title>\n<style>")
          // kolory raportu (dokument wyjściowy, nie interfejs programu)
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #bbb;padding:4px 8px;vertical-align:top}")
          .Append("del{background:#fdd;text-decoration:line-through}ins{background:#dfd;text-decoration:none}.k{white-space:nowrap}")
          .Append("</style></head><body>\n<h1>").Append(E(S(c, "Security.Compare.Report.Title"))).Append("</h1>\n<p>")
          .Append(E(string.Format(c, S(c, "Security.Compare.Report.DocA"), r.NameA, r.PageCountA))).Append("<br>")
          .Append(E(string.Format(c, S(c, "Security.Compare.Report.DocB"), r.NameB, r.PageCountB))).Append("<br>")
          .Append(E(string.Format(c, S(c, "Security.Compare.Report.Summary"), r.Changes.Count,
              r.Changes.Count(x => x.Kind == CompareChangeKind.Inserted), r.Changes.Count(x => x.Kind == CompareChangeKind.Deleted), r.Changes.Count(x => x.Kind == CompareChangeKind.Changed))))
          .Append("</p>\n<table><tr><th>#</th><th>").Append(E(S(c, "Security.Compare.Column.Kind"))).Append("</th><th>")
          .Append(E(S(c, "Security.Compare.Column.PageA"))).Append("</th><th>").Append(E(S(c, "Security.Compare.Column.PageB"))).Append("</th><th>")
          .Append(E(S(c, "Security.Compare.Column.Text"))).Append("</th></tr>\n");
        foreach (var ch in r.Changes)
        {
            sb.Append("<tr><td>").Append(ch.Index + 1).Append("</td><td class=\"k\">").Append(E(KindText(ch.Kind, c))).Append("</td><td>")
              .Append(PageText(ch.PageA)).Append("</td><td>").Append(PageText(ch.PageB)).Append("</td><td>");
            if (ch.TextA.Length == 0 && ch.TextB.Length == 0) sb.Append(E(S(c, "Security.Compare.Graphic")));
            if (ch.TextA.Length > 0) sb.Append("<del>").Append(E(ch.TextA)).Append("</del> ");
            if (ch.TextB.Length > 0) sb.Append("<ins>").Append(E(ch.TextB)).Append("</ins>");
            sb.Append("</td></tr>\n");
        }
        sb.Append("</table></body></html>\n");
        return sb.ToString();
    }
}

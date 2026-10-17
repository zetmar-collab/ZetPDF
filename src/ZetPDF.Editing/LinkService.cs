using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Pdf;
using ZetPDF.Editing.Text;

namespace ZetPDF.Editing;

/// <summary>Łącza (adnotacje /Link z akcją /URI lub /GoTo) i zakładki z nagłówków (PdfPig – rozmiar/pogrubienie czcionki).</summary>
public sealed class LinkService : ILinkService
{
    private static readonly Regex UrlRegex = new(
        @"(?<url>(?:https?://|www\.)[^\s<>""'()\[\]{}]+)|(?<mail>[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<IReadOnlyList<PdfLinkInfo>> GetLinksAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PdfLinkInfo>>(() => PdfSharpEdit.Read(pdf, doc =>
        {
            var list = new List<PdfLinkInfo>();
            for (int i = 0; i < doc.PageCount; i++)
            {
                if (pageIndex is { } p && p != i) continue;
                list.AddRange(Links(doc, i).Select(l => l.Info));
            }
            return list;
        }), ct);

    private static List<(PdfDictionary Annot, PdfLinkInfo Info)> Links(PdfDocument doc, int pageIndex)
    {
        var result = new List<(PdfDictionary, PdfLinkInfo)>();
        foreach (var a in PdfObjects.Annotations(doc.Pages[pageIndex]))
        {
            if (PdfObjects.Subtype(a) != "/Link") continue;
            var rect = PdfObjects.Rect(a.Elements["/Rect"]) ?? default;
            PdfLinkKind kind = PdfLinkKind.Other;
            string? uri = null;
            int? target = null;
            PdfPoint? point = null;
            var action = PdfObjects.Resolve(a.Elements["/A"]) as PdfDictionary;
            PdfItem? dest = a.Elements["/Dest"];
            if (action is not null)
            {
                var s = action.Elements.GetName("/S");
                if (s == "/URI")
                {
                    kind = PdfLinkKind.Uri;
                    uri = (PdfObjects.Resolve(action.Elements["/URI"]) as PdfString)?.Value;
                }
                else if (s == "/GoTo") dest = action.Elements["/D"];
            }
            if (kind != PdfLinkKind.Uri && PdfObjects.Resolve(dest) is PdfArray d && d.Elements.Count > 0)
            {
                int idx = PdfObjects.PageIndexOf(doc, d.Elements[0]);
                if (idx >= 0)
                {
                    kind = PdfLinkKind.GoToPage;
                    target = idx;
                    if (d.Elements.Count >= 4 && d.Elements.GetName(1) == "/XYZ")
                        point = new PdfPoint(PdfObjects.Number(d.Elements[2]), PdfObjects.Number(d.Elements[3]));
                }
            }
            result.Add((a, new PdfLinkInfo(pageIndex, rect, kind, uri, target, point)));
        }
        return result;
    }

    public Task<byte[]> AddLinkAsync(byte[] pdf, int pageIndex, PdfRect rect, LinkTarget target, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            Validate(doc, pageIndex, rect, target);
            PdfObjects.AddAnnotation(doc.Pages[pageIndex], CreateLink(doc, rect, target));
        }, ct);

    public Task<byte[]> UpdateLinkAsync(byte[] pdf, int pageIndex, int linkIndex, PdfRect rect, LinkTarget target, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            Validate(doc, pageIndex, rect, target);
            var links = Links(doc, pageIndex);
            if (linkIndex < 0 || linkIndex >= links.Count) throw new ArgumentOutOfRangeException(nameof(linkIndex));
            var a = links[linkIndex].Annot;
            a.Elements.Remove("/Dest");
            a.Elements["/Rect"] = PdfObjects.RectArray(doc, rect);
            a.Elements["/A"] = Action(doc, target);
        }, ct);

    public Task<byte[]> DeleteLinkAsync(byte[] pdf, int pageIndex, int linkIndex, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var links = Links(doc, pageIndex);
            if (linkIndex < 0 || linkIndex >= links.Count) throw new ArgumentOutOfRangeException(nameof(linkIndex));
            var target = links[linkIndex].Annot;
            PdfObjects.RemoveAnnotations(doc.Pages[pageIndex], a => ReferenceEquals(a, target));
        }, ct);

    public Task<byte[]> RemoveAllLinksAsync(byte[] pdf, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            foreach (var page in doc.Pages) PdfObjects.RemoveAnnotations(page, a => PdfObjects.Subtype(a) == "/Link");
        }, ct);

    public Task<(byte[] Pdf, int Created)> CreateLinksFromUrlsAsync(byte[] pdf, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var found = new List<(int Page, PdfRect Rect, string Uri)>();
            using (var doc = UglyToad.PdfPig.PdfDocument.Open(pdf))
            {
                for (int p = 0; p < doc.NumberOfPages; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(OperationProgress.Of(p, doc.NumberOfPages));
                    foreach (var word in doc.GetPage(p + 1).GetWords())
                    {
                        foreach (Match m in UrlRegex.Matches(word.Text))
                        {
                            var value = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                            if (value.Length < 5) continue;
                            var letters = word.Letters.Skip(m.Index).Take(value.Length).ToList();
                            if (letters.Count == 0) continue;
                            var rect = letters.Select(l => TextLine.R(l.GlyphRectangle)).Aggregate((a, b) => a.Union(b)).Inflate(1);
                            var uri = m.Groups["mail"].Success ? "mailto:" + value
                                : value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + value : value;
                            found.Add((p, rect, uri));
                        }
                    }
                }
            }
            if (found.Count == 0) return (pdf, 0);
            int created = 0;
            var output = PdfSharpEdit.Modify(pdf, doc =>
            {
                foreach (var (p, rect, uri) in found)
                {
                    var existing = Links(doc, p).Select(l => l.Info.Rect).ToList();
                    if (existing.Any(r => r.IntersectsWith(rect))) continue;
                    PdfObjects.AddAnnotation(doc.Pages[p], CreateLink(doc, rect, new LinkTarget(Uri: uri)));
                    created++;
                }
            });
            return (created == 0 ? pdf : output, created);
        }, ct);

    private static void Validate(PdfDocument doc, int pageIndex, PdfRect rect, LinkTarget target)
    {
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (rect.IsEmpty) throw new ArgumentException("Empty rectangle.", nameof(rect));
        if (string.IsNullOrWhiteSpace(target.Uri) && target.PageIndex is not { } t) throw new ArgumentException("Link needs URI or page.", nameof(target));
        if (target.PageIndex is { } pi && (pi < 0 || pi >= doc.PageCount)) throw new ArgumentOutOfRangeException(nameof(target));
    }

    private static PdfDictionary CreateLink(PdfDocument doc, PdfRect rect, LinkTarget target)
    {
        var a = new PdfDictionary(doc);
        a.Elements["/Type"] = new PdfName("/Annot");
        a.Elements["/Subtype"] = new PdfName("/Link");
        a.Elements["/Rect"] = PdfObjects.RectArray(doc, rect);
        a.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
        a.Elements["/F"] = new PdfInteger(4);
        a.Elements["/A"] = Action(doc, target);
        return a;
    }

    private static PdfDictionary Action(PdfDocument doc, LinkTarget target)
    {
        var act = new PdfDictionary(doc);
        if (!string.IsNullOrWhiteSpace(target.Uri))
        {
            act.Elements["/S"] = new PdfName("/URI");
            act.Elements["/URI"] = new PdfString(target.Uri.Trim());
            return act;
        }
        var page = doc.Pages[target.PageIndex!.Value];
        act.Elements["/S"] = new PdfName("/GoTo");
        var dest = new PdfArray(doc);
        dest.Elements.Add(page.Reference!);
        if (target.Point is { } pt)
        {
            dest.Elements.Add(new PdfName("/XYZ"));
            dest.Elements.Add(new PdfReal(pt.X));
            dest.Elements.Add(new PdfReal(pt.Y));
            dest.Elements.Add(PdfNull.Value);
        }
        else dest.Elements.Add(new PdfName("/Fit"));
        act.Elements["/D"] = dest;
        return act;
    }

    public Task<IReadOnlyList<BookmarkNode>> GenerateBookmarksFromHeadingsAsync(byte[] pdf, HeadingDetectionOptions options, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<BookmarkNode>>(() =>
        {
            ArgumentNullException.ThrowIfNull(options);
            var all = new List<TextLine>();
            using (var doc = UglyToad.PdfPig.PdfDocument.Open(pdf))
                for (int p = 0; p < doc.NumberOfPages; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    all.AddRange(TextLines.Build(doc.GetPage(p + 1), p));
                }
            if (all.Count == 0) return Array.Empty<BookmarkNode>();
            return Headings.Build(all, options);
        }, ct);
}

/// <summary>Wykrywanie nagłówków i budowa drzewa zakładek.</summary>
internal static class Headings
{
    public static IReadOnlyList<BookmarkNode> Build(IReadOnlyList<TextLine> lines, HeadingDetectionOptions options)
    {
        // Rozmiar podstawowy = najczęstszy rozmiar ważony liczbą znaków.
        double body = lines.SelectMany(l => l.Words.SelectMany(w => w.Letters)).GroupBy(l => Math.Round(l.PointSize, 1))
            .OrderByDescending(g => g.Count()).First().Key;
        var candidates = new List<(TextLine Line, double Size, bool Bold)>();
        foreach (var l in lines)
        {
            var text = l.Text.Trim();
            if (text.Length < 2 || text.Length > 150 || !text.Any(char.IsLetter)) continue;
            double size = Math.Round(l.FontSize, 1);
            bool bigger = size >= body * options.MinSizeRatio;
            bool boldBody = options.TreatBoldAsHeading && l.Bold && size >= body * 0.95 && text.Length <= 100;
            if (bigger || boldBody) candidates.Add((l, size, l.Bold));
        }
        if (candidates.Count == 0) return Array.Empty<BookmarkNode>();
        // Poziomy: malejąco wg (rozmiar, pogrubienie).
        var levels = candidates.Select(c => (c.Size, c.Bold)).Distinct().OrderByDescending(k => k.Size).ThenByDescending(k => k.Bold).ToList();
        int max = Math.Max(1, options.MaxLevels);

        // Sklejanie wieloliniowych nagłówków (ten sam poziom, ta sama strona, bezpośrednio pod sobą).
        var merged = new List<(string Title, int Page, PdfPoint Target, int Level, PdfRect Last, int LineIndex)>();
        foreach (var c in candidates)
        {
            int level = levels.IndexOf((c.Size, c.Bold));
            if (level >= max) continue;
            var b = c.Line.Bounds;
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                bool adjacent = prev.Page == c.Line.PageIndex && prev.LineIndex == c.Line.Index - 1 && prev.Last.Bottom - b.Top < c.Size * 0.8;
                if (prev.Level == level && adjacent)
                {
                    merged[^1] = (prev.Title + " " + c.Line.Text.Trim(), prev.Page, prev.Target, level, b, c.Line.Index);
                    continue;
                }
            }
            merged.Add((c.Line.Text.Trim(), c.Line.PageIndex, new PdfPoint(b.Left, b.Top), level, b, c.Line.Index));
        }

        // Drzewo (stos poziomów).
        var roots = new List<Node>();
        var stack = new Stack<Node>();
        foreach (var m in merged)
        {
            var node = new Node(m.Title.Length > 200 ? m.Title[..200] : m.Title, m.Page, m.Target, m.Level);
            while (stack.Count > 0 && stack.Peek().Level >= m.Level) stack.Pop();
            if (stack.Count == 0) roots.Add(node);
            else stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        return roots.Select(r => r.ToBookmark()).ToList();
    }

    private sealed class Node
    {
        public Node(string title, int page, PdfPoint target, int level) { Title = title; Page = page; Target = target; Level = level; }
        public string Title { get; }
        public int Page { get; }
        public PdfPoint Target { get; }
        public int Level { get; }
        public List<Node> Children { get; } = new();
        public BookmarkNode ToBookmark() => new(Title, Page, Children.Select(c => c.ToBookmark()).ToList(), Target, IsOpen: Level == 0);
    }
}

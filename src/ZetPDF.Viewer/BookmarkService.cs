using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Viewer;

/// <summary>
/// Odczyt/zapis drzewa zakładek przez PDFsharp 6.2.4 (DECYZJE D-16). Metody „bajty → bajty”, bez efektów ubocznych
/// (wejście niezmieniane), więc nadają się do poleceń z Cofnij/Ponów i do wywołań wsadowych (np. „Zakładki z nagłówków”).
/// Cel zakładki: strona (0-based) i opcjonalny punkt (/XYZ left top); PageIndex = -1 → zakładka bez celu.
/// </summary>
public sealed class BookmarkService : IBookmarkService
{
    /// <summary>Maksymalna głębokość drzewa (ochrona przed cyklami w uszkodzonych plikach).</summary>
    public const int MaxDepth = 64;

    public Task<IReadOnlyList<BookmarkNode>> GetBookmarksAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var doc = PdfSharpEdit.OpenForModify(pdf);
            var pages = PageMap(doc);
            return Read(doc.Outlines, pages, 0, ct);
        }, ct);
    }

    public Task<byte[]> SetBookmarksAsync(byte[] pdf, IReadOnlyList<BookmarkNode> bookmarks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(bookmarks);
        return PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            doc.Outlines.Clear();
            Write(doc, doc.Outlines, bookmarks, 0);
            if (bookmarks.Count == 0)
            {
                // bez pustego słownika /Outlines (część czytników pokazuje wtedy pusty panel)
                doc.Internals.Catalog.Elements.Remove("/Outlines");
                if (doc.PageMode == PdfPageMode.UseOutlines) doc.PageMode = PdfPageMode.UseNone;
            }
        }, ct);
    }

    private static Dictionary<int, int> PageMap(PdfDocument doc)
    {
        var map = new Dictionary<int, int>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var r = doc.Pages[i].Reference;
            if (r is not null) map[r.ObjectNumber] = i;
        }
        return map;
    }

    private static IReadOnlyList<BookmarkNode> Read(PdfOutlineCollection outlines, Dictionary<int, int> pages, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth || outlines.Count == 0) return BookmarkNode.NoChildren;
        var list = new List<BookmarkNode>(outlines.Count);
        foreach (var o in outlines)
        {
            ct.ThrowIfCancellationRequested();
            int pageIndex = -1;
            PdfPoint? target = null;
            try
            {
                var page = o.DestinationPage;
                if (page?.Reference is { } r && pages.TryGetValue(r.ObjectNumber, out int idx)) pageIndex = idx;
                if (pageIndex >= 0 && o.PageDestinationType == PdfPageDestinationType.Xyz && (o.Left.HasValue || o.Top.HasValue))
                    target = new PdfPoint(o.Left ?? 0, o.Top ?? 0);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or NullReferenceException or ArgumentException)
            {
                pageIndex = -1; // cel nieobsługiwany (np. nazwany cel, akcja zewnętrzna)
            }
            var children = o.HasChildren ? Read(o.Outlines, pages, depth + 1, ct) : BookmarkNode.NoChildren;
            // PDFsharp przy odczycie nie odtwarza Opened z /Count – czytamy znak /Count wprost (dodatni = rozwinięta)
            bool open = children.Count > 0 && (o.Elements.GetInteger("/Count") > 0 || (o.Opened && !o.Elements.ContainsKey("/Count")));
            list.Add(new BookmarkNode(o.Title ?? string.Empty, pageIndex, children, target, open,
                o.Style is PdfOutlineStyle.Bold or PdfOutlineStyle.BoldItalic,
                o.Style is PdfOutlineStyle.Italic or PdfOutlineStyle.BoldItalic, ReadColor(o)));
        }
        return list;
    }

    /// <summary>Kolor tytułu (/C, RGB 0..1). Czarny i brak koloru traktujemy tak samo – bez wpisu.</summary>
    internal static PdfColor? ReadColor(PdfDictionary outline)
    {
        var c = outline.Elements["/C"] is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : outline.Elements["/C"];
        if (c is not PdfArray { Elements.Count: 3 } arr) return null;
        var v = new double[3];
        for (int i = 0; i < 3; i++)
        {
            v[i] = arr.Elements[i] switch { PdfInteger n => n.Value, PdfReal d => d.Value, _ => double.NaN };
            if (!double.IsFinite(v[i])) return null;
        }
        static byte B(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        var color = new PdfColor(255, B(v[0]), B(v[1]), B(v[2]));
        return color == PdfColor.Black ? null : color;
    }

    internal static void WriteColor(PdfDictionary outline, PdfColor? color)
    {
        if (color is not { } c || c == PdfColor.Black) { outline.Elements.Remove("/C"); return; }
        var arr = new PdfArray();
        foreach (var b in new[] { c.R, c.G, c.B }) arr.Elements.Add(new PdfReal(Math.Round(b / 255.0, 4)));
        outline.Elements["/C"] = arr;
    }

    private static void Write(PdfDocument doc, PdfOutlineCollection target, IReadOnlyList<BookmarkNode> nodes, int depth)
    {
        if (depth > MaxDepth) return;
        foreach (var n in nodes)
        {
            var style = n.Bold && n.Italic ? PdfOutlineStyle.BoldItalic : n.Bold ? PdfOutlineStyle.Bold : n.Italic ? PdfOutlineStyle.Italic : PdfOutlineStyle.Regular;
            var title = string.IsNullOrEmpty(n.Title) ? " " : n.Title;
            PdfOutline o;
            if (n.PageIndex >= 0 && n.PageIndex < doc.PageCount)
            {
                o = target.Add(title, doc.Pages[n.PageIndex], n.IsOpen && n.Children.Count > 0, style);
                if (n.Target is { } p)
                {
                    o.PageDestinationType = PdfPageDestinationType.Xyz;
                    o.Left = p.X;
                    o.Top = p.Y;
                }
            }
            else
            {
                o = new PdfOutline { Title = title, Opened = n.IsOpen && n.Children.Count > 0, Style = style };
                target.Add(o);
            }
            WriteColor(o, n.Color);
            if (n.Children.Count > 0)
            {
                Write(doc, o.Outlines, n.Children, depth + 1);
                o.Opened = n.IsOpen;
                // PDFsharp 6.2.4 nie zapisuje /Count elementów (każdy wychodzi jako zwinięty) – ustawiamy go jawnie:
                // dodatni = rozwinięty (liczba bezpośrednich dzieci; czytniki i tak przeliczają widoczne), ujemny = zwinięty.
                o.Elements.SetInteger("/Count", n.IsOpen ? n.Children.Count : -n.Children.Count);
            }
        }
    }
}

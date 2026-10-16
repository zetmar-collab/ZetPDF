using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZetPDF.Core.Contracts;
using ZetPDF.Rendering;
using ZetPDF.Viewer.Bookmarks;
using ZetPDF.Viewer.Printing;
using ZetPDF.Viewer.Text;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tests;

/// <summary>Czysta logika (bez PDFium): układ stron, zaznaczanie, wyszukiwanie, zakładki, druk, pamięć podręczna.</summary>
public class LogicTests
{
    private static PageInfo A4(int i, int rot = 0) => new(i, PdfSize.A4, rot, new PdfPoint(0, 0));

    [Fact]
    public void Continuous_layout_stacks_pages_and_fit_width_fills_viewport()
    {
        var pages = Enumerable.Range(0, 500).Select(i => A4(i)).ToArray();
        var vp = new Size(800, 600);
        double zoom = PageLayout.ComputeZoom(ZoomMode.FitWidth, pages, PageLayoutMode.Continuous, 0, 0, vp, 1);
        var layout = PageLayout.Build(pages, PageLayoutMode.Continuous, 0, zoom, 0, vp);
        Assert.Equal(500, layout.PlacedPages.Count);
        var r0 = layout.GetPageRect(0);
        var r1 = layout.GetPageRect(1);
        Assert.Equal(r0.Bottom + PageLayout.Gap, r1.Top, 3);
        Assert.InRange(r0.Width, 800 - 2 * PageLayout.Margin - 30, 800 - 2 * PageLayout.Margin);
        Assert.True(layout.Extent.Height > 499 * r0.Height);
        Assert.Equal(zoom * 96 / 72, layout.Scale, 6);
    }

    [Fact]
    public void Single_and_two_page_modes_place_only_current_spread()
    {
        var pages = Enumerable.Range(0, 7).Select(i => A4(i)).ToArray();
        var single = PageLayout.Build(pages, PageLayoutMode.SinglePage, 0, 1, 3, new Size(800, 600));
        Assert.Equal(new[] { 3 }, single.PlacedPages);
        var two = PageLayout.Build(pages, PageLayoutMode.TwoPages, 0, 1, 3, new Size(800, 600));
        Assert.Equal(new[] { 2, 3 }, two.PlacedPages);
        Assert.True(two.GetPageRect(3).Left > two.GetPageRect(2).Right);
        var twoCont = PageLayout.Build(pages, PageLayoutMode.TwoPagesContinuous, 0, 1, 0, new Size(800, 600));
        Assert.Equal(7, twoCont.PlacedPages.Count);
        Assert.Equal(twoCont.GetPageRect(0).Top, twoCont.GetPageRect(1).Top, 3);
    }

    [Fact]
    public void Fit_page_shows_whole_page_and_rotation_swaps_sizes()
    {
        var pages = new[] { A4(0), A4(1, 90) };
        var vp = new Size(1000, 500);
        double z = PageLayout.ComputeZoom(ZoomMode.FitPage, pages, PageLayoutMode.SinglePage, 0, 0, vp, 1);
        var l = PageLayout.Build(pages, PageLayoutMode.SinglePage, 0, z, 0, vp);
        Assert.True(l.GetPageRect(0).Height <= vp.Height - 2 * PageLayout.Margin + 0.01);
        var s = PageLayout.DisplaySizePoints(pages[1], 0);
        Assert.Equal(PdfSize.A4.Height, s.Width, 3);
        var s2 = PageLayout.DisplaySizePoints(pages[1], 90);
        Assert.Equal(PdfSize.A4.Width, s2.Width, 3);
    }

    [Fact]
    public void Zoom_steps_go_up_and_down()
    {
        Assert.Equal(1.25, PageLayout.NextZoomStep(1.0, true));
        Assert.Equal(0.75, PageLayout.NextZoomStep(1.0, false));
        Assert.Equal(PageLayout.MaxZoom, PageLayout.NextZoomStep(64, true));
        Assert.True(ViewerBar.TryParsePercent("125%", out var z) && Math.Abs(z - 1.25) < 1e-9);
        Assert.True(ViewerBar.TryParsePercent(" 66,5 ", out z) && Math.Abs(z - 0.665) < 1e-9);
        Assert.False(ViewerBar.TryParsePercent("abc", out _));
    }

    private static PageText Line(int page, string text, double y = 700, double x0 = 50, double w = 10)
    {
        var chars = text.Select((c, i) => new TextChar(c, c == ' ' ? new PdfRect(x0 + i * w, y, x0 + i * w + w, y + 12) : new PdfRect(x0 + i * w, y, x0 + i * w + w, y + 12), 12, false)).ToArray();
        return new PageText(page, text, chars);
    }

    [Fact]
    public void Text_selection_across_pages_builds_spans_quads_and_text()
    {
        var p0 = Line(0, "Zażółć gęślą");
        var p1 = Line(1, "jaźń koniec");
        PageText? Get(int i) => i == 0 ? p0 : i == 1 ? p1 : null;
        int start = TextGeometry.HitCaret(p0, new PdfPoint(50 + 7 * 10 + 1, 705)); // przed "g"
        Assert.Equal(7, start);
        int end = TextGeometry.HitCaret(p1, new PdfPoint(50 + 3 * 10 + 8, 705)); // za "ń"
        Assert.Equal(4, end);
        var sel = TextGeometry.BuildSelection(new TextCaret(1, end), new TextCaret(0, start), Get)!; // odwrócona kolejność
        Assert.Equal(2, sel.Spans.Count);
        Assert.Equal("gęślą\r\njaźń", sel.Text);
        Assert.Single(sel.Spans[0].Quads);
        Assert.Equal(new PdfRect(120, 700, 170, 712), sel.Spans[0].Quads[0].Bounds);
        Assert.Equal((7, 12), TextGeometry.WordAt(p0.Text, 9));
    }

    [Fact]
    public void Line_quads_split_on_new_line()
    {
        var a = Line(0, "abc", 700);
        var b = Line(0, "def", 680);
        var chars = a.Chars.Concat(new[] { new TextChar('\r', default, 0, true), new TextChar('\n', default, 0, true) }).Concat(b.Chars).ToArray();
        var text = new PageText(0, "abc\r\ndef", chars);
        var quads = TextGeometry.LineQuads(text, 0, text.Text.Length);
        Assert.Equal(2, quads.Count);
    }

    [Theory]
    [InlineData("Zażółć gęślą jaźń – ZAŻÓŁĆ GĘŚLĄ JAŹŃ", "gęślą", false, false, 2)]
    [InlineData("Zażółć gęślą jaźń – ZAŻÓŁĆ GĘŚLĄ JAŹŃ", "gęślą", true, false, 1)]
    [InlineData("Zażółć gęślą jaźń – ZAŻÓŁĆ GĘŚLĄ JAŹŃ", "jaź", false, true, 0)]
    [InlineData("Zażółć gęślą jaźń – ZAŻÓŁĆ GĘŚLĄ JAŹŃ", "jaźń", false, true, 2)]
    [InlineData("koniec wiersza\r\nnowy wiersz", "wiersza nowy", false, false, 1)]
    public void Text_matcher_handles_polish_case_words_and_line_breaks(string text, string query, bool matchCase, bool whole, int expected)
    {
        var hits = TextMatcher.FindAll(text, query, matchCase, whole);
        Assert.Equal(expected, hits.Count);
        foreach (var (s, c) in hits)
            Assert.Equal(query.Replace(" ", string.Empty), text.Substring(s, c).Replace("\r\n", string.Empty).Replace(" ", string.Empty), ignoreCase: !matchCase);
    }

    private static readonly IReadOnlyList<BookmarkNode> Sample = new[]
    {
        new BookmarkNode("A", 0, new[] { new BookmarkNode("A1", 1, BookmarkNode.NoChildren), new BookmarkNode("A2", 2, BookmarkNode.NoChildren) }),
        new BookmarkNode("B", 3, BookmarkNode.NoChildren),
        new BookmarkNode("C", 4, BookmarkNode.NoChildren),
    };

    [Fact]
    public void Bookmark_tree_insert_rename_remove()
    {
        var t = BookmarkTree.Insert(Sample, new[] { 1 }, 0, new BookmarkNode("B1", 3, BookmarkNode.NoChildren));
        Assert.Equal("B1", BookmarkTree.Get(t, new[] { 1, 0 }).Title);
        Assert.True(t[1].IsOpen);
        t = BookmarkTree.Rename(t, new[] { 0, 1 }, "A2 – zmieniona");
        Assert.Equal("A2 – zmieniona", t[0].Children[1].Title);
        t = BookmarkTree.Remove(t, new[] { 0, 0 });
        Assert.Single(t[0].Children);
        Assert.Equal(2, Sample[0].Children.Count); // wejście niezmienione
        Assert.Equal(3, Sample.Count);
    }

    [Fact]
    public void Bookmark_tree_move_handles_index_shift_nesting_and_cycles()
    {
        // C przed A (najwyższy poziom)
        var m = BookmarkTree.Move(Sample, new[] { 2 }, Array.Empty<int>(), 0)!.Value;
        Assert.Equal(new[] { "C", "A", "B" }, m.Tree.Select(n => n.Title));
        Assert.Equal(new[] { 0 }, m.NewPath);
        // A na koniec (indeks liczony przed usunięciem)
        m = BookmarkTree.Move(Sample, new[] { 0 }, Array.Empty<int>(), 3)!.Value;
        Assert.Equal(new[] { "B", "C", "A" }, m.Tree.Select(n => n.Title));
        Assert.Equal(new[] { 2 }, m.NewPath);
        // B jako dziecko C (rodzic za źródłem – ścieżka korygowana)
        m = BookmarkTree.Move(Sample, new[] { 1 }, new[] { 2 }, 0)!.Value;
        Assert.Equal(new[] { "A", "C" }, m.Tree.Select(n => n.Title));
        Assert.Equal("B", m.Tree[1].Children[0].Title);
        Assert.Equal(new[] { 1, 0 }, m.NewPath);
        // A1 na najwyższy poziom za B
        m = BookmarkTree.Move(Sample, new[] { 0, 0 }, Array.Empty<int>(), 2)!.Value;
        Assert.Equal(new[] { "A", "B", "A1", "C" }, m.Tree.Select(n => n.Title));
        // do własnego poddrzewa – niedozwolone
        Assert.Null(BookmarkTree.Move(Sample, new[] { 0 }, new[] { 0, 1 }, 0));
    }

    [Fact]
    public void Print_layout_sheets_grid_and_fit()
    {
        var pages = Enumerable.Range(0, 10).ToArray();
        Assert.Equal(3, PrintLayout.Sheets(pages, 4).Count);
        Assert.Equal(2, PrintLayout.Sheets(pages, 9).Count);
        Assert.Equal(10, PrintLayout.Sheets(pages, 3).Count); // nieobsługiwane → 1
        var portrait = new Rect(0, 0, 700, 1000);
        Assert.Equal((1, 2), PrintLayout.Grid(2, portrait.Size));
        Assert.Equal((2, 3), PrintLayout.Grid(6, portrait.Size));
        Assert.Equal((3, 2), PrintLayout.Grid(6, new Size(1000, 700)));
        Assert.Equal(9, PrintLayout.Cells(9, portrait).Count);

        var a4 = new Size(PdfSize.A4.Width, PdfSize.A4.Height);
        var fit = PrintLayout.Place(0, a4, portrait, PrintScaling.FitToPaper, 100, true, false);
        Assert.False(fit.Rotate90);
        Assert.Equal(700, fit.Destination.Width, 1);
        var actual = PrintLayout.Place(0, a4, portrait, PrintScaling.ActualSize, 100, true, false);
        Assert.Equal(PdfSize.A4.Width * 96 / 72, actual.Destination.Width, 1);
        var custom = PrintLayout.Place(0, a4, portrait, PrintScaling.Custom, 50, true, false);
        Assert.Equal(actual.Destination.Width / 2, custom.Destination.Width, 1);
        // strona pozioma na komórce pionowej → obrót
        var landscape = PrintLayout.Place(0, new Size(842, 595), portrait, PrintScaling.FitToPaper, 100, true, false);
        Assert.True(landscape.Rotate90);
        // 2 na arkuszu w pionie: komórki poziome, strony A4 pionowe → obrót
        var two = PrintLayout.LayoutSheet(new[] { 0, 1 }, _ => a4, portrait, new PrintOptions(null, "", PagesPerSheet: 2));
        Assert.All(two, p => Assert.True(p.Rotate90));
        Assert.True(two[1].Destination.Top >= two[0].Destination.Bottom);
        // 700 DIP = 7,29 cala; 600 DPI drukarki ograniczone do 300 → 2187 px na 595 pt
        Assert.Equal(700 / 96.0 * 300 / PdfSize.A4.Width, PrintLayout.PixelScale(fit, a4, 600), 6);
        Assert.Equal(700 / 96.0 * 150 / PdfSize.A4.Width, PrintLayout.PixelScale(fit, a4, 150), 6);
    }

    [Fact]
    public void Render_cache_evicts_least_recently_used_by_bytes()
    {
        static PageBitmap Bmp(int page, int px)
        {
            var src = BitmapSource.Create(px, px, 96, 96, PixelFormats.Pbgra32, null, new byte[px * px * 4], px * 4);
            src.Freeze();
            return new PageBitmap(page, 0, 1, new PixelRect(0, 0, px, px), px, px, src);
        }
        var cache = new RenderCache(3 * 100 * 100 * 4);
        cache.Put(0, 1, Bmp(0, 100));
        cache.Put(1, 1, Bmp(1, 100));
        cache.Put(2, 1, Bmp(2, 100));
        Assert.NotNull(cache.Get(0, 1)); // 0 świeży
        cache.Put(3, 1, Bmp(3, 100));    // wypycha 1
        Assert.Null(cache.Peek(1, 1));
        Assert.NotNull(cache.Peek(0, 1));
        Assert.True(cache.UsedBytes <= cache.BudgetBytes);
        cache.Put(4, 1, Bmp(4, 400)); // większy niż budżet – zostaje sam
        Assert.Equal(1, cache.Count);
    }
}

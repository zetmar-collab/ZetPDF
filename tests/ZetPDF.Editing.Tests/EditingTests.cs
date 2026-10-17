using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class TextEditTests
{
    private readonly TextEditService _svc = new();

    [Fact]
    public async Task Blocks_are_lines_with_font_info()
    {
        var blocks = await _svc.GetTextBlocksAsync(SamplePdfFactory.Text(1), 0);
        var header = blocks.First(b => b.Text == "Strona 1");
        Assert.True(header.Bold);
        Assert.Equal(24, header.FontSize, 1);
        Assert.Contains("Arial", header.FontName);
        Assert.Contains(blocks, b => b.Text.StartsWith(SamplePdfFactory.PolishPangram, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replace_block_removes_old_text_from_stream_and_adds_polish_text()
    {
        var pdf = SamplePdfFactory.Text(1);
        var block = (await _svc.GetTextBlocksAsync(pdf, 0)).First(b => b.Text.StartsWith("The quick", StringComparison.Ordinal));
        var output = await _svc.ReplaceTextBlockAsync(pdf, 0, block.Id, "Źrebię łasi się – ąęćńóśżź", new TextStyle { FontFamily = "Times New Roman", FontSize = 13, Color = PdfColor.Red });
        var text = await TestEnv.PdfiumText(output);
        Assert.DoesNotContain("quick", text);
        Assert.DoesNotContain("quick", TestEnv.PdfPigText(output));
        Assert.Contains("Źrebię łasi się – ąęćńóśżź", text);
        Assert.Contains("Strona 1", text); // reszta strony nietknięta
        var nb = (await _svc.GetTextBlocksAsync(output, 0)).First(b => b.Text.StartsWith("Źrebię", StringComparison.Ordinal));
        Assert.Equal(block.Bounds.Left, nb.Bounds.Left, 0);
        Assert.Equal(13, nb.FontSize, 1);
        Assert.Equal(PdfColor.Red.R, nb.Color.R);
    }

    [Fact]
    public async Task Partial_glyph_removal_keeps_neighbours_in_place()
    {
        // Linia pangramu i linia angielska – zastępujemy tylko angielską; pozycja pangramu bez zmian.
        var pdf = SamplePdfFactory.Text(1);
        var before = (await _svc.GetTextBlocksAsync(pdf, 0)).First(b => b.Text.StartsWith("Zażółć", StringComparison.Ordinal));
        var target = (await _svc.GetTextBlocksAsync(pdf, 0)).First(b => b.Text.StartsWith("The quick", StringComparison.Ordinal));
        var output = await _svc.ReplaceTextBlockAsync(pdf, 0, target.Id, "Nowa", new TextStyle());
        var after = (await _svc.GetTextBlocksAsync(output, 0)).First(b => b.Text.StartsWith("Zażółć", StringComparison.Ordinal));
        Assert.Equal(before.Text, after.Text);
        Assert.Equal(before.Bounds.Left, after.Bounds.Left, 1);
        Assert.Equal(before.Bounds.Right, after.Bounds.Right, 1);
    }

    [Fact]
    public async Task Add_text_with_wrapping_is_searchable()
    {
        var pdf = SamplePdfFactory.Text(1);
        var output = await _svc.AddTextAsync(pdf, 0, new PdfPoint(56, 400), "Dodany tekst z polskimi znakami żółć i zawijaniem bardzo długiej linii tekstu", new TextStyle { FontSize = 12 }, maxWidth: 150);
        var all = await TestEnv.PdfiumText(output);
        Assert.True(await TestEnv.PdfiumSearchCount(output, "znakami żółć") == 1, all);
        var blocks = await _svc.GetTextBlocksAsync(output, 0);
        Assert.True(blocks.Count(b => b.Bounds.Top < 420 && b.Bounds.Bottom > 300) >= 2, "zawinięte w kilka linii");
    }

    [Fact]
    public async Task Find_replace_replaces_on_all_pages()
    {
        var pdf = SamplePdfFactory.Text(3);
        var r = await _svc.FindReplaceAsync(pdf, "lazy dog", "leniwy pies", TextSearchOptions.None);
        Assert.Equal(3, r.Replaced);
        var text = await TestEnv.PdfiumText(r.Pdf);
        Assert.DoesNotContain("lazy", text);
        Assert.Equal(3, await TestEnv.PdfiumSearchCount(r.Pdf, "leniwy pies"));
        var none = await _svc.FindReplaceAsync(pdf, "nieistniejące", "x", TextSearchOptions.None);
        Assert.Equal(0, none.Replaced);
        Assert.Same(pdf, none.Pdf);
    }

    [Fact]
    public async Task Find_replace_respects_whole_word_and_case()
    {
        var pdf = SamplePdfFactory.Text(1, "kot kotek Kot");
        var r = await _svc.FindReplaceAsync(pdf, "kot", "pies", TextSearchOptions.WholeWord | TextSearchOptions.MatchCase);
        Assert.Equal(1, r.Replaced);
        Assert.Contains("pies kotek Kot", await TestEnv.PdfiumText(r.Pdf));
    }
}

public class ObjectEditTests
{
    private readonly ObjectEditService _svc = new();

    private static byte[] Png(int w, int h, bool alpha) => TestImages.Png(w, h, alpha);

    [Fact]
    public async Task Detects_images_and_shapes_and_moves_image()
    {
        var pdf = await _svc.AddImageAsync(SamplePdfFactory.Text(1), 0, Png(40, 20, alpha: true), new PdfRect(100, 100, 300, 200));
        pdf = await _svc.AddShapeAsync(pdf, 0, new ShapeSpec { Kind = ShapeKind.Ellipse, Rect = new PdfRect(300, 300, 400, 350), Fill = PdfColor.Yellow });
        var objs = await _svc.GetObjectsAsync(pdf, 0);
        var img = Assert.Single(objs, o => o.Kind == PageObjectKind.Image);
        Assert.Equal(100, img.Bounds.Left, 1);
        Assert.Equal(200, img.Bounds.Top, 1);
        var shape = objs.Single(o => o.Kind == PageObjectKind.Shape && o.Bounds.Left > 299);
        Assert.Equal(400, shape.Bounds.Right, 1);
        Assert.Contains(objs, o => o.Kind == PageObjectKind.Text);

        var moved = await _svc.TransformObjectAsync(pdf, 0, img.Id, PdfMatrix.Translation(50, -20));
        var img2 = (await _svc.GetObjectsAsync(moved, 0)).Single(o => o.Kind == PageObjectKind.Image);
        Assert.Equal(150, img2.Bounds.Left, 1);
        Assert.Equal(180, img2.Bounds.Top, 1);

        // Skala wokół lewego dolnego rogu i obrót o 90°.
        var scaled = await _svc.TransformObjectAsync(moved, 0, img2.Id,
            PdfMatrix.Translation(-150, -80).Then(PdfMatrix.Scaling(2, 2)).Then(PdfMatrix.Translation(150, 80)));
        var img3 = (await _svc.GetObjectsAsync(scaled, 0)).Single(o => o.Kind == PageObjectKind.Image);
        Assert.Equal(550, img3.Bounds.Right, 1);
        var rotated = await _svc.TransformObjectAsync(scaled, 0, img3.Id, PdfMatrix.Rotation(90));
        var img4 = (await _svc.GetObjectsAsync(rotated, 0)).Single(o => o.Kind == PageObjectKind.Image);
        Assert.Equal(img3.Bounds.Width, img4.Bounds.Height, 1);
        // Tekst strony bez zmian.
        Assert.Contains("Strona 1", await TestEnv.PdfiumText(rotated));
    }

    [Fact]
    public async Task Delete_and_replace_image()
    {
        var pdf = await _svc.AddImageAsync(SamplePdfFactory.Text(1), 0, Png(40, 40, false), new PdfRect(100, 100, 200, 200));
        var img = (await _svc.GetObjectsAsync(pdf, 0)).Single(o => o.Kind == PageObjectKind.Image);
        var replaced = await _svc.ReplaceImageAsync(pdf, 0, img.Id, Png(80, 40, false));
        var r = (await _svc.GetObjectsAsync(replaced, 0)).Single(o => o.Kind == PageObjectKind.Image);
        Assert.Equal(100, r.Bounds.Width, 1);
        Assert.Equal(50, r.Bounds.Height, 1); // proporcje zachowane
        var deleted = await _svc.DeleteObjectAsync(replaced, 0, r.Id);
        Assert.DoesNotContain(await _svc.GetObjectsAsync(deleted, 0), o => o.Kind == PageObjectKind.Image);
        Assert.Contains("Strona 1", await TestEnv.PdfiumText(deleted));
    }

    [Fact]
    public async Task Shapes_render_with_colors()
    {
        var pdf = await _svc.AddShapeAsync(SamplePdfFactory.Text(1), 0, new ShapeSpec { Kind = ShapeKind.Rectangle, Rect = new PdfRect(0, 0, 100, 100), Fill = PdfColor.Red, Stroke = null });
        pdf = await _svc.AddShapeAsync(pdf, 0, new ShapeSpec { Kind = ShapeKind.Line, Rect = new PdfRect(200, 200, 300, 300), LineWidth = 3 });
        using var doc = await TestEnv.Renderer.OpenAsync(pdf);
        var bmp = await doc.RenderPageAsync(0, RenderRequest.ForDpi(72));
        int o = (bmp.Height - 50) * bmp.Stride + 50 * 4;
        Assert.True(bmp.Bgra[o + 2] > 200 && bmp.Bgra[o + 1] < 60, "czerwony prostokąt w lewym dolnym rogu");
    }
}

public class LinkTests
{
    private readonly LinkService _svc = new();

    [Fact]
    public async Task Add_update_delete_links()
    {
        var pdf = SamplePdfFactory.Text(3);
        pdf = await _svc.AddLinkAsync(pdf, 0, new PdfRect(50, 50, 150, 70), new LinkTarget(Uri: "https://example.com/ą"));
        pdf = await _svc.AddLinkAsync(pdf, 0, new PdfRect(50, 100, 150, 120), new LinkTarget(PageIndex: 2, Point: new PdfPoint(0, 800)));
        var links = await _svc.GetLinksAsync(pdf, 0);
        Assert.Equal(2, links.Count);
        Assert.Equal(PdfLinkKind.Uri, links[0].Kind);
        Assert.Equal(PdfLinkKind.GoToPage, links[1].Kind);
        Assert.Equal(2, links[1].TargetPageIndex);

        // PDFium widzi te same łącza.
        using (var doc = await TestEnv.Renderer.OpenAsync(pdf))
        {
            var pl = await doc.GetLinksAsync(0);
            Assert.Equal(2, pl.Count);
            Assert.Contains(pl, l => l.Kind == PdfLinkKind.GoToPage && l.TargetPageIndex == 2);
        }

        pdf = await _svc.UpdateLinkAsync(pdf, 0, 0, new PdfRect(60, 60, 160, 80), new LinkTarget(PageIndex: 1));
        links = await _svc.GetLinksAsync(pdf, 0);
        Assert.Equal(1, links[0].TargetPageIndex);
        Assert.Equal(60, links[0].Rect.Left, 1);

        pdf = await _svc.DeleteLinkAsync(pdf, 0, 1);
        Assert.Single(await _svc.GetLinksAsync(pdf));
        pdf = await _svc.RemoveAllLinksAsync(pdf);
        Assert.Empty(await _svc.GetLinksAsync(pdf));
    }

    [Fact]
    public async Task Creates_links_from_urls_and_emails()
    {
        var pdf = Samples.Structured();
        var (output, created) = await _svc.CreateLinksFromUrlsAsync(pdf);
        Assert.Equal(6, created); // 3 na stronę × 2 strony
        var links = await _svc.GetLinksAsync(output);
        Assert.Contains(links, l => l.Uri == "http://www.example.com");
        Assert.Contains(links, l => l.Uri == "https://zetpdf.example.org/strona");
        Assert.Contains(links, l => l.Uri == "mailto:kontakt@example.com");
        // Drugie wywołanie nie dubluje.
        var (_, again) = await _svc.CreateLinksFromUrlsAsync(output);
        Assert.Equal(0, again);
    }

    [Fact]
    public async Task Bookmarks_from_headings_build_tree()
    {
        var tree = await _svc.GenerateBookmarksFromHeadingsAsync(Samples.Structured(), new HeadingDetectionOptions());
        Assert.Equal(2, tree.Count);
        Assert.Equal("Rozdział 1. Wstęp", tree[0].Title);
        Assert.Equal(0, tree[0].PageIndex);
        Assert.Equal(new[] { "1.1 Podrozdział pierwszy", "1.2 Podrozdział drugi" }, tree[0].Children.Select(c => c.Title));
        Assert.Equal(1, tree[1].PageIndex);
        var flat = await _svc.GenerateBookmarksFromHeadingsAsync(Samples.Structured(), new HeadingDetectionOptions { MaxLevels = 1 });
        Assert.All(flat, n => Assert.Empty(n.Children));
    }
}

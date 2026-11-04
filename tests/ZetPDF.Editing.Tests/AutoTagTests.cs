using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Accessibility;
using ZetPDF.Editing.Content;
using ZetPDF.Ocr.Pdf;
using ZetPDF.Rendering;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Editing.Tests;

/// <summary>Oznaczanie struktury: render bez zmian, tekst czytelny, drzewo zgodne z treścią, artefakty, strony nieczytelne, limity.</summary>
public class AutoTagTests
{
    private static readonly PdfiumRenderer Renderer = new();
    private static AutoTagService Service() => new(Renderer);
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public AutoTagTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private static readonly AutoTagOptions Options = new() { Language = "pl-PL" };

    private static byte[] Png1x1()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
    }

    private static byte[] Report(int pages = 3)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var body = new XFont("Arial", 11);
        var bold = new XFont("Arial", 11, XFontStyleEx.Bold);
        var h1 = new XFont("Arial", 22, XFontStyleEx.Bold);
        var h2 = new XFont("Arial", 15, XFontStyleEx.Bold);
        for (int p = 0; p < pages; p++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString("Raport roczny firmy", body, XBrushes.Black, 50, 30);
            g.DrawString("Tytuł rozdziału " + (p + 1), h1, XBrushes.Black, 50, 90);
            g.DrawString("Podrozdział zażółć", h2, XBrushes.Black, 50, 125);
            for (int i = 0; i < 4; i++)
                g.DrawString("Akapit tekstu głównego wiersz " + i + " zawiera zwykłe zdania raportu.", body, XBrushes.Black, 50, 150 + i * 14);
            g.DrawString("Wyróżnienie", bold, XBrushes.Black, 50, 225);
            g.DrawString("Tekst po wyróżnieniu, zwykły.", body, XBrushes.Black, 50, 245);
            g.DrawString("• pierwszy punkt listy", body, XBrushes.Black, 60, 280);
            g.DrawString("• drugi punkt listy", body, XBrushes.Black, 60, 296);
            g.DrawString("1. numerowany jeden", body, XBrushes.Black, 60, 330);
            g.DrawString("2. numerowany dwa", body, XBrushes.Black, 60, 346);
            g.DrawLine(new XPen(XColors.Gray, 1), 50, 370, 545, 370);
            using (var ms = new MemoryStream(Png1x1()))
            using (var image = XImage.FromStream(ms))
                g.DrawImage(image, 50, 390, 120, 90);
            g.DrawString("Tekst pod obrazem.", body, XBrushes.Black, 50, 510);
            g.DrawString("Strona " + (p + 1) + " z " + pages, body, XBrushes.Black, 270, 815);
        }
        return PdfSharpEdit.Save(doc);
    }

    private static async Task AssertSameRender(byte[] a, byte[] b, double dpi = 110)
    {
        using var da = await Renderer.OpenAsync(a);
        using var db = await Renderer.OpenAsync(b);
        Assert.Equal(da.PageCount, db.PageCount);
        for (int i = 0; i < da.PageCount; i++)
        {
            var left = await da.RenderPageAsync(i, RenderRequest.ForDpi(dpi));
            var right = await db.RenderPageAsync(i, RenderRequest.ForDpi(dpi));
            Assert.Equal(left.Width, right.Width);
            Assert.True(left.Bgra.AsSpan().SequenceEqual(right.Bgra), "strona " + i + " ma inne piksele");
        }
    }

    private sealed record TreeLeaf(string Role, int PageIndex, List<int> Mcids, PdfDictionary Element);

    /// <summary>Niezależny odczyt drzewa przez PDFsharp, z ochroną przed cyklem.</summary>
    private static List<TreeLeaf> ReadTree(PdfDocument doc)
    {
        var root = (PdfDictionary)Resolve(doc.Internals.Catalog.Elements["/StructTreeRoot"])!;
        Assert.Equal("/StructTreeRoot", root.Elements.GetName("/Type"));
        var leaves = new List<TreeLeaf>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem>();
        stack.Push(root.Elements["/K"]!);
        var pageIndex = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < doc.PageCount; i++) pageIndex[doc.Pages[i]] = i;
        while (stack.Count > 0)
        {
            var item = Resolve(stack.Pop());
            if (item is PdfArray arr)
            {
                foreach (var e in arr.Elements.Reverse()) stack.Push(e);
                continue;
            }
            if (item is not PdfDictionary d || !visited.Add(d)) continue;
            var kids = Resolve(d.Elements["/K"]);
            var ints = (kids as PdfArray)?.Elements.Select(Resolve).OfType<PdfInteger>().Select(x => x.Value).ToList() ?? new List<int>();
            if (ints.Count > 0)
            {
                var pg = (PdfDictionary)Resolve(d.Elements["/Pg"])!;
                leaves.Add(new TreeLeaf(d.Elements.GetName("/S"), pageIndex[pg], ints, d));
            }
            else if (kids is not null) stack.Push(kids);
        }
        return leaves;
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    private static string Content(PdfPage page) => Encoding.Latin1.GetString(PageContent.ReadAll(page));

    [Fact]
    public async Task Render_is_pixel_identical_and_text_stays_readable()
    {
        var pdf = Report();
        var r = await Service().TagAsync(pdf, Options);
        Assert.Empty(r.SkippedPages);
        Assert.Equal(3, r.TaggedPages);
        await AssertSameRender(pdf, r.Pdf);

        using (var pig = PigDocument.Open(r.Pdf))
        using (var before = PigDocument.Open(pdf))
            for (int i = 1; i <= 3; i++)
                Assert.Equal(before.GetPage(i).Text, pig.GetPage(i).Text);
        using var doc = await Renderer.OpenAsync(r.Pdf);
        var text = await doc.GetPageTextAsync(0);
        Assert.Contains("zażółć", text.Text);
    }

    [Fact]
    public async Task Structure_tree_matches_content_and_parent_tree()
    {
        var r = await Service().TagAsync(Report(), Options);
        PdfSharpEdit.Read(r.Pdf, doc =>
        {
            var catalog = doc.Internals.Catalog;
            Assert.True(((PdfDictionary)Resolve(catalog.Elements["/MarkInfo"])!).Elements.GetBoolean("/Marked"));
            Assert.Equal("pl-PL", catalog.Elements.GetString("/Lang"));
            var leaves = ReadTree(doc);
            var roles = leaves.Select(l => l.Role).ToList();
            Assert.Contains("/H1", roles);
            Assert.Contains("/H2", roles);
            Assert.Contains("/P", roles);
            Assert.Contains("/LBody", roles);
            Assert.Contains("/Figure", roles);

            var root = (PdfDictionary)Resolve(catalog.Elements["/StructTreeRoot"])!;
            var nums = (PdfArray)Resolve(((PdfDictionary)Resolve(root.Elements["/ParentTree"])!).Elements["/Nums"])!;
            Assert.Equal(doc.PageCount, root.Elements.GetInteger("/ParentTreeNextKey"));
            for (int p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                int key = page.Elements.GetInteger("/StructParents");
                Assert.Equal(p, key);
                var parents = (PdfArray)Resolve(nums.Elements[key * 2 + 1])!;
                var mcidsInContent = System.Text.RegularExpressions.Regex.Matches(Content(page), @"/(\w+) <</MCID (\d+)>> BDC").Select(m => (Tag: m.Groups[1].Value, Id: int.Parse(m.Groups[2].Value))).ToList();
                Assert.Equal(parents.Elements.Count, mcidsInContent.Count);
                Assert.Equal(Enumerable.Range(0, mcidsInContent.Count), mcidsInContent.Select(m => m.Id));
                foreach (var (tag, id) in mcidsInContent)
                {
                    var element = (PdfDictionary)Resolve(parents.Elements[id])!;
                    Assert.Equal("/" + tag, element.Elements.GetName("/S"));
                    var leaf = leaves.Single(l => ReferenceEquals(l.Element, element));
                    Assert.Contains(id, leaf.Mcids);
                    Assert.Equal(p, leaf.PageIndex);
                }
                var c = Content(page);
                Assert.Equal(System.Text.RegularExpressions.Regex.Matches(c, @"\bBDC\b|\bBMC\b").Count, System.Text.RegularExpressions.Regex.Matches(c, @"\bEMC\b").Count);
            }
            return 0;
        });
    }

    [Fact]
    public async Task Headers_footers_and_page_numbers_are_artifacts()
    {
        var r = await Service().TagAsync(Report(), Options);
        Assert.True(r.Artifacts >= 6, r.Artifacts.ToString());
        PdfSharpEdit.Read(r.Pdf, doc =>
        {
            var page = doc.Pages[1];
            using var pig = PigDocument.Open(r.Pdf);
            var content = Content(page);
            Assert.Contains("/Artifact BMC", content);
            var leaves = ReadTree(doc);
            Assert.Equal(3, leaves.Count(l => l.Role == "/H1"));
            return 0;
        });
    }

    [Fact]
    public async Task Single_page_number_is_artifact_but_text_in_margin_is_not_without_repeat()
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        var f = new XFont("Arial", 11);
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("Tekst na górze strony jest jednorazowy", f, XBrushes.Black, 50, 30);
            g.DrawString("Treść strony", f, XBrushes.Black, 50, 400);
            g.DrawString("7", f, XBrushes.Black, 290, 820);
        }
        var r = await Service().TagAsync(PdfSharpEdit.Save(doc), Options);
        Assert.Equal(1, r.Artifacts); // numer strony
        Assert.Equal(2, r.Paragraphs);
    }

    [Fact]
    public async Task ZetPdf_marks_stay_artifacts_with_their_stream_keys()
    {
        var pdf = PdfSharpEdit.Modify(Report(1), doc =>
        {
            var page = doc.Pages[0];
            var dict = new PdfDictionary(doc);
            dict.Elements["/ZetPdfMark"] = new PdfName("/Watermark");
            dict.CreateStream(Encoding.ASCII.GetBytes("/Artifact <</Type /Pagination /Subtype /Watermark>> BDC q 0.5 g 100 100 50 50 re f Q EMC\n"));
            doc.Internals.AddObject(dict);
            var old = PageContent.Streams(page).Select(s => (PdfItem)s.Reference!).ToArray();
            page.Elements["/Contents"] = new PdfArray(doc, old.Append(dict.Reference!).ToArray());
        });
        var r = await Service().TagAsync(pdf, Options);
        Assert.Empty(r.SkippedPages);
        await AssertSameRender(pdf, r.Pdf);
        PdfSharpEdit.Read(r.Pdf, doc =>
        {
            var streams = PageContent.Streams(doc.Pages[0]);
            var mark = streams.Single(s => s.Elements.ContainsKey("/ZetPdfMark"));
            var body = Encoding.Latin1.GetString(PageContent.Decoded(mark));
            Assert.DoesNotContain("MCID", body);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"\bBDC\b"));
            return 0;
        });
    }

    [Fact]
    public async Task Already_tagged_document_is_detected_and_replaced_without_stale_mcids()
    {
        var svc = Service();
        var pdf = Report(2);
        Assert.False(await svc.IsTaggedAsync(pdf));
        var first = await svc.TagAsync(pdf, Options);
        Assert.True(await svc.IsTaggedAsync(first.Pdf));
        var second = await svc.TagAsync(first.Pdf, Options);
        Assert.Empty(second.SkippedPages);
        Assert.NotEqual(first.Pdf, second.Pdf);
        await AssertSameRender(pdf, second.Pdf);
        PdfSharpEdit.Read(second.Pdf, doc =>
        {
            var leaves = ReadTree(doc);
            var c = Content(doc.Pages[0]);
            var mcids = System.Text.RegularExpressions.Regex.Matches(c, @"/MCID (\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
            Assert.Equal(mcids.Count, mcids.Distinct().Count());
            Assert.Equal(leaves.Where(l => l.PageIndex == 0).SelectMany(l => l.Mcids).Order(), mcids.Order());
            return 0;
        });
        Assert.Equal(first.Headings, second.Headings);
    }

    private static byte[] WithRawPage(string content) => PdfSharpEdit.Modify(Report(1), doc =>
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        PageContent.Replace(page, Encoding.Latin1.GetBytes(content));
    });

    [Theory]
    [InlineData("q 1 0 0 1 0 0 cm /P BMC 10 10 m Q")] // BMC bez EMC
    [InlineData("EMC 0 0 10 10 re f")] // EMC bez BMC
    [InlineData("BT (urwany napis")] // urwany napis
    public async Task Unreadable_page_is_skipped_and_reported_without_failing(string content)
    {
        var pdf = WithRawPage(content);
        var r = await Service().TagAsync(pdf, Options);
        Assert.Equal(new[] { 1 }, r.SkippedPages.Select(p => p.PageIndex));
        Assert.Equal(AutoTagSkipReason.Unreadable, r.SkippedPages[0].Reason);
        Assert.Equal(1, r.TaggedPages);
        await AssertSameRender(pdf, r.Pdf);
    }

    [Fact]
    public async Task Deep_array_nesting_is_skipped_not_a_stack_overflow()
    {
        var pdf = WithRawPage(new string('[', 2_000_000) + "(x)" + new string(']', 2_000_000) + " TJ");
        var r = await Service().TagAsync(pdf, Options);
        Assert.Equal(AutoTagSkipReason.Unreadable, r.SkippedPages.Single().Reason);
    }

    [Fact]
    public async Task Too_many_operators_is_skipped_with_reason_and_quickly()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < AutoTagService.MaxOpsPerPage + 10; i++) sb.Append("q\nQ\n");
        var pdf = WithRawPage(sb.ToString());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Service().TagAsync(pdf, Options);
        Assert.Equal(AutoTagSkipReason.TooLarge, r.SkippedPages.Single().Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task Too_many_glyphs_and_long_line_of_operators_finish_quickly()
    {
        var pdf = WithRawPage("BT /F1 1 Tf (" + new string('a', AutoTagService.MaxGlyphsPerPage * 2 + 10) + ") Tj ET");
        pdf = PdfSharpEdit.Modify(pdf, d => d.Pages[1].Resources.Elements["/Font"] = d.Pages[0].Resources.Elements["/Font"]);
        var r = await Service().TagAsync(pdf, Options);
        Assert.Contains(r.SkippedPages, p => p.PageIndex == 1);

        var sb = new StringBuilder("BT /F0 1 Tf 10 10 Td ");
        for (int i = 0; i < 200_000; i++) sb.Append("(a) Tj\n");
        sb.Append("ET");
        var longLine = PdfSharpEdit.Modify(WithRawPage(sb.ToString()), d => d.Pages[1].Resources.Elements["/Font"] = d.Pages[0].Resources.Elements["/Font"]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new AutoTagService().TagAsync(longLine, Options);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task Inline_image_keeps_rendering_or_page_is_reported()
    {
        // obraz w treści z bajtami przypominającymi operatory
        const string content = "q 100 0 0 100 50 500 cm BI /W 2 /H 2 /CS /G /BPC 8 ID \u0000\u0080ETÿ EI Q BT /F1 12 Tf 50 400 Td ET";
        var pdf = WithRawPage(content);
        var r = await Service().TagAsync(pdf, Options);
        await AssertSameRender(pdf, r.Pdf);
        Assert.Equal(2, r.TaggedPages + r.SkippedPages.Count);
        _output.WriteLine("Obraz w treści: oznaczone " + r.TaggedPages + ", pominięte " + string.Join(",", r.SkippedPages));
    }

    [Fact]
    public async Task Cancellation_stops_tagging()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().TagAsync(Report(), Options, null, cts.Token));
        using var cts2 = new CancellationTokenSource(5);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            for (int i = 0; i < 200; i++) await Service().TagAsync(Report(), Options, null, cts2.Token);
        });
    }

    [Fact]
    public async Task Accessibility_check_passes_tagging_rules_after_tagging_and_undo_works()
    {
        var pdf = Report();
        var check = new AccessibilityService();
        var before = await check.CheckAsync(pdf);
        Assert.Equal(AccessCheckStatus.Failed, before.Results.Single(x => x.Rule == AccessRule.Tagged).Status);

        using var session = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(pdf), PdfSecurityInfo.None, DocumentPermissions.All, new SnapshotStorageOptions(TestPaths.NewDirectory()));
        var svc = Service();
        await session.ExecuteAsync(new DelegateDocumentCommand("Edit.Undo.AutoTag", async (b, p, ct) => (await svc.TagAsync(b, Options, p, ct)).Pdf, DocumentChangeKind.PageContent, DocumentPermissions.ModifyContent, null));
        var after = await check.CheckAsync(session.Current.GetBytes());
        Assert.Equal(AccessCheckStatus.Passed, after.Results.Single(x => x.Rule == AccessRule.Tagged).Status);
        Assert.Equal(AccessCheckStatus.Passed, after.Results.Single(x => x.Rule == AccessRule.StructureTree).Status);
        Assert.Equal(AccessCheckStatus.Passed, after.Results.Single(x => x.Rule == AccessRule.Language).Status);
        // figury bez tekstu alternatywnego: uczciwy błąd w raporcie
        Assert.Equal(AccessCheckStatus.Failed, after.Results.Single(x => x.Rule == AccessRule.FigureAltText).Status);
        await session.History.UndoAsync();
        Assert.Equal(pdf, session.Current.GetBytes());
        await session.History.RedoAsync();
        Assert.True(await svc.IsTaggedAsync(session.Current.GetBytes()));
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Scanned")]
    [InlineData("WithAnnotations")]
    [InlineData("WithForm")]
    public async Task Sample_documents_keep_render(string kind)
    {
        var pdf = kind switch
        {
            "Text" => SamplePdfFactory.Text(2),
            "Scanned" => SamplePdfFactory.Scanned(),
            "WithAnnotations" => SamplePdfFactory.WithAnnotations(),
            _ => SamplePdfFactory.WithForm(),
        };
        var r = await Service().TagAsync(pdf, Options);
        Assert.Empty(r.SkippedPages);
        await AssertSameRender(pdf, r.Pdf);
    }

    [Fact]
    public void Tokenizer_finds_operators_in_original_bytes()
    {
        var data = Encoding.Latin1.GetBytes("q %komentarz Tj\n1 0 0 1 5 5 cm BT /F1 12 Tf (a\\)Tj(b)) Tj [(x) -5 <41>] TJ ET /P <</MCID 3/A[1 2]>> BDC EMC Q");
        var ops = ContentTokenizer.Scan(data, 100);
        Assert.Equal(new[] { "q", "cm", "BT", "Tf", "Tj", "TJ", "ET", "BDC", "EMC", "Q" }, ops.Select(o => o.Name));
        var tj = ops[4];
        Assert.StartsWith("(a", Encoding.Latin1.GetString(data, tj.Start, tj.End - tj.Start));
        Assert.EndsWith("Tj", Encoding.Latin1.GetString(data, tj.Start, tj.End - tj.Start));
        Assert.Throws<ContentTokenException>(() => ContentTokenizer.Scan(Encoding.ASCII.GetBytes(new string('[', 65)), 100));
        Assert.Throws<ContentTokenException>(() => ContentTokenizer.Scan(Encoding.ASCII.GetBytes("q Q q"), 2));
    }
}

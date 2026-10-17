using System.Text;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

public class AnnotationServiceTests
{
    private readonly AnnotationService _svc = new();

    private static readonly PdfRect Blank = new(100, 300, 300, 400);

    public static IEnumerable<object[]> Kinds() => new[]
    {
        new object[] { AnnotationKind.Highlight }, new object[] { AnnotationKind.Underline }, new object[] { AnnotationKind.StrikeOut },
        new object[] { AnnotationKind.Squiggly }, new object[] { AnnotationKind.AreaHighlight }, new object[] { AnnotationKind.Note },
        new object[] { AnnotationKind.FreeText }, new object[] { AnnotationKind.Typewriter }, new object[] { AnnotationKind.Callout },
        new object[] { AnnotationKind.Square }, new object[] { AnnotationKind.Circle }, new object[] { AnnotationKind.Line },
        new object[] { AnnotationKind.Arrow }, new object[] { AnnotationKind.Polygon }, new object[] { AnnotationKind.Polyline },
        new object[] { AnnotationKind.Ink }, new object[] { AnnotationKind.Stamp },
    };

    internal static AnnotationSpec SpecFor(AnnotationKind kind, PdfRect r, int page = 0)
    {
        var mid = new PdfPoint((r.Left + r.Right) / 2, (r.Bottom + r.Top) / 2);
        return new AnnotationSpec
        {
            Kind = kind,
            PageIndex = page,
            Rect = r,
            Quads = kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly
                ? new[] { PdfQuad.FromRect(new PdfRect(r.Left, mid.Y - 8, r.Right, mid.Y + 8)) } : null,
            Points = kind switch
            {
                AnnotationKind.Line or AnnotationKind.Arrow => new[] { new PdfPoint(r.Left, r.Bottom), new PdfPoint(r.Right, r.Top) },
                AnnotationKind.Polygon or AnnotationKind.Polyline => new[] { new PdfPoint(r.Left, r.Bottom), new PdfPoint(mid.X, r.Top), new PdfPoint(r.Right, r.Bottom) },
                AnnotationKind.Callout => new[] { new PdfPoint(r.Left, r.Bottom), new PdfPoint(mid.X, mid.Y) },
                _ => null,
            },
            InkStrokes = kind == AnnotationKind.Ink ? new[] { new[] { new PdfPoint(r.Left, r.Bottom), new PdfPoint(mid.X, r.Top), new PdfPoint(r.Right, mid.Y) } } : null,
            Contents = kind is AnnotationKind.FreeText or AnnotationKind.Typewriter or AnnotationKind.Callout ? "Zażółć gęślą jaźń" : "Komentarz",
            Color = kind is AnnotationKind.Highlight or AnnotationKind.AreaHighlight or AnnotationKind.Note ? PdfColor.Yellow : PdfColor.Red,
            LineWidth = 2,
            StampName = kind == AnnotationKind.Stamp ? "Approved" : null,
            Author = "Tester",
        };
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Added_annotation_has_appearance_and_is_rendered_by_pdfium(AnnotationKind kind)
    {
        var pdf = SamplePdfFactory.Text(1);
        var area = kind == AnnotationKind.Callout ? new PdfRect(180, 350, 300, 400) : Blank;
        Assert.Equal(0, await RenderCheck.NonWhitePixels(pdf, 0, area.Inflate(10)));
        var (result, id) = await _svc.AddAsync(pdf, SpecFor(kind, area));

        PdfSharpEdit.Read(result, doc =>
        {
            var annots = doc.Pages[0].Elements.GetArray("/Annots")!;
            var dict = annots.Elements.Select(e => (e as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary ?? e as PdfDictionary)
                .First(d => d!.Elements.GetString("/NM") == id.Name)!;
            Assert.NotNull(dict.Elements.GetDictionary("/AP"));
            Assert.NotNull(dict.Elements.GetDictionary("/AP")!.Elements["/N"]);
            return 0;
        });
        var list = await _svc.GetAnnotationsAsync(result);
        var info = Assert.Single(list, a => a.Id == id);
        Assert.Equal(kind, info.Kind);
        Assert.Equal("Tester", info.Author);
        Assert.True(await RenderCheck.NonWhitePixels(result, 0, info.Rect) > 20, "PDFium render shows nothing for " + kind);
    }

    [Fact]
    public async Task Reply_and_state_are_stored_with_irt()
    {
        var (pdf, id) = await _svc.AddAsync(SamplePdfFactory.Text(1), SpecFor(AnnotationKind.Note, new PdfRect(100, 400, 100, 400)));
        var (pdf2, replyId) = await _svc.AddReplyAsync(pdf, id, "Odpowiedź – ąę", "Recenzent");
        var pdf3 = await _svc.UpdateAsync(pdf2, id, new AnnotationUpdate { State = AnnotationReviewState.Accepted });
        var list = await _svc.GetAnnotationsAsync(pdf3);
        var reply = Assert.Single(list, a => a.Id == replyId);
        Assert.Equal(id, reply.InReplyTo);
        Assert.Equal("Odpowiedź – ąę", reply.Contents);
        Assert.Equal("Recenzent", reply.Author);
        Assert.Equal(AnnotationReviewState.Accepted, list.Single(a => a.Id == id).State);
        Assert.Equal(2, list.Count); // stan nie jest osobnym komentarzem
        var raw = Encoding.Latin1.GetString(pdf3);
        Assert.Contains("/IRT", raw);
        Assert.Contains("/StateModel", raw);

        // usunięcie rodzica usuwa odpowiedź, stan i okienko
        var pdf4 = await _svc.DeleteAsync(pdf3, new[] { id });
        Assert.Empty(await _svc.GetAnnotationsAsync(pdf4));
    }

    [Fact]
    public async Task Update_moves_resizes_and_recolors()
    {
        var (pdf, id) = await _svc.AddAsync(SamplePdfFactory.Text(1), SpecFor(AnnotationKind.Square, Blank));
        var target = new PdfRect(300, 500, 450, 600);
        var moved = await _svc.UpdateAsync(pdf, id, new AnnotationUpdate { Rect = target, Color = new PdfColor(255, 0, 0, 255), Opacity = 0.5, Contents = "nowa" });
        var info = (await _svc.GetAnnotationsAsync(moved)).Single();
        Assert.Equal(target, info.Rect);
        Assert.Equal(new PdfColor(255, 0, 0, 255), info.Color);
        Assert.Equal(0.5, info.Opacity, 3);
        Assert.Equal("nowa", info.Contents);
        Assert.True(await RenderCheck.NonWhitePixels(moved, 0, target) > 20);
        Assert.Equal(0, await RenderCheck.NonWhitePixels(moved, 0, Blank.Inflate(-5)));
    }

    [Fact]
    public async Task Hidden_flag_hides_annotation_in_render()
    {
        var (pdf, _) = await _svc.AddAsync(SamplePdfFactory.Text(1), SpecFor(AnnotationKind.Square, Blank));
        var hidden = await _svc.SetAllHiddenAsync(pdf, true);
        Assert.True((await _svc.GetAnnotationsAsync(hidden)).Single().IsHidden);
        Assert.Equal(0, await RenderCheck.NonWhitePixels(hidden, 0, Blank));
        var shown = await _svc.SetAllHiddenAsync(hidden, false);
        Assert.True(await RenderCheck.NonWhitePixels(shown, 0, Blank) > 20);
    }

    [Fact]
    public async Task DeleteAll_keeps_links_and_widgets()
    {
        var pdf = SamplePdfFactory.WithForm();
        var (a, _) = await _svc.AddAsync(pdf, SpecFor(AnnotationKind.Circle, Blank));
        var cleared = await _svc.DeleteAllAsync(a);
        var left = await _svc.GetAnnotationsAsync(cleared);
        Assert.All(left, x => Assert.Equal(AnnotationKind.Widget, x.Kind));
        Assert.NotEmpty(left);
    }

    [Fact]
    public async Task Existing_annotations_from_other_producer_are_read()
    {
        var list = await _svc.GetAnnotationsAsync(SamplePdfFactory.WithAnnotations());
        Assert.Contains(list, a => a.Kind == AnnotationKind.Note && a.Contents == "Notatka testowa – ąęś" && a.Author == "Tester");
        Assert.Contains(list, a => a.Kind == AnnotationKind.Stamp && a.Id.PageIndex == 1);
    }

    [Fact]
    public async Task Xfdf_export_import_roundtrip_with_dedup()
    {
        var pdf = SamplePdfFactory.Text(2);
        var kinds = new[] { AnnotationKind.Highlight, AnnotationKind.Note, AnnotationKind.FreeText, AnnotationKind.Ink, AnnotationKind.Line, AnnotationKind.Polygon, AnnotationKind.Stamp, AnnotationKind.Callout };
        AnnotationId noteId = default;
        foreach (var k in kinds)
        {
            var (next, id) = await _svc.AddAsync(pdf, SpecFor(k, Blank, k == AnnotationKind.Ink ? 1 : 0));
            pdf = next;
            if (k == AnnotationKind.Note) noteId = id;
        }
        (pdf, _) = await _svc.AddReplyAsync(pdf, noteId, "Re: ok", "B");
        pdf = await _svc.UpdateAsync(pdf, noteId, new AnnotationUpdate { State = AnnotationReviewState.Rejected });
        var source = await _svc.GetAnnotationsAsync(pdf);

        using var ms = new MemoryStream();
        await _svc.ExportXfdfAsync(pdf, ms, "dokument.pdf");
        var xml = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("http://ns.adobe.com/xfdf/", xml);
        Assert.Contains("<highlight", xml);
        Assert.Contains("inreplyto=", xml);
        Assert.Contains("statemodel=\"Review\"", xml);

        var clean = SamplePdfFactory.Text(2);
        var imported = await _svc.ImportCommentsAsync(clean, new MemoryStream(ms.ToArray()));
        var got = await _svc.GetAnnotationsAsync(imported);
        Assert.Equal(source.Count, got.Count);
        foreach (var s in source)
        {
            var g = Assert.Single(got, x => x.Id.Name == s.Id.Name);
            Assert.Equal(s.Kind, g.Kind);
            Assert.Equal(s.Contents, g.Contents);
            Assert.Equal(s.Author, g.Author);
            Assert.Equal(s.Id.PageIndex, g.Id.PageIndex);
            Assert.Equal(s.InReplyTo?.Name, g.InReplyTo?.Name);
            Assert.Equal(s.State, g.State);
        }
        // widoczne po imporcie
        var hl = got.First(x => x.Kind == AnnotationKind.Highlight);
        Assert.True(await RenderCheck.NonWhitePixels(imported, 0, hl.Rect) > 20);

        // ponowny import – bez duplikatów
        var again = await _svc.ImportCommentsAsync(imported, new MemoryStream(ms.ToArray()));
        Assert.Equal(got.Count, (await _svc.GetAnnotationsAsync(again)).Count);
    }

    [Fact]
    public async Task Fdf_export_import_roundtrip()
    {
        var (pdf, _) = await _svc.AddAsync(SamplePdfFactory.Text(1), SpecFor(AnnotationKind.FreeText, Blank));
        (pdf, _) = await _svc.AddAsync(pdf, SpecFor(AnnotationKind.Square, new PdfRect(300, 500, 400, 600)));
        using var ms = new MemoryStream();
        await _svc.ExportFdfAsync(pdf, ms, "a.pdf");
        Assert.StartsWith("%FDF-1.2", Encoding.ASCII.GetString(ms.ToArray(), 0, 8));
        var imported = await _svc.ImportCommentsAsync(SamplePdfFactory.Text(1), new MemoryStream(ms.ToArray()));
        var got = await _svc.GetAnnotationsAsync(imported);
        Assert.Equal(2, got.Count);
        Assert.Contains(got, g => g.Kind == AnnotationKind.FreeText && g.Contents == "Zażółć gęślą jaźń");
        Assert.True(await RenderCheck.NonWhitePixels(imported, 0, new PdfRect(300, 500, 400, 600)) > 20);
        var again = await _svc.ImportCommentsAsync(imported, new MemoryStream(ms.ToArray()));
        Assert.Equal(2, (await _svc.GetAnnotationsAsync(again)).Count);
    }

    [Fact]
    public async Task Image_stamp_and_flatten_keep_render()
    {
        var png = TestImages.RedPng(60, 30);
        var spec = new AnnotationSpec { Kind = AnnotationKind.Stamp, PageIndex = 0, Rect = Blank, ImageData = png };
        var (pdf, _) = await _svc.AddAsync(SamplePdfFactory.Text(1), spec);
        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, Blank) > 1000);
        var flat = await _svc.FlattenAsync(pdf, includeFormFields: true);
        Assert.Empty(await _svc.GetAnnotationsAsync(flat));
        Assert.True(await RenderCheck.NonWhitePixels(flat, 0, Blank) > 1000);
    }

    [Fact]
    public async Task Polish_path_save_roundtrip()
    {
        var (pdf, _) = await _svc.AddAsync(SamplePdfFactory.Text(1), SpecFor(AnnotationKind.Typewriter, Blank));
        var path = TestPaths.WriteSample(pdf);
        var reread = await File.ReadAllBytesAsync(path);
        var info = (await _svc.GetAnnotationsAsync(reread)).Single();
        Assert.Equal(AnnotationKind.Typewriter, info.Kind);
        Assert.Equal("Zażółć gęślą jaźń", info.Contents);
    }
}

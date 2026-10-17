using System.Text;
using System.Xml.Linq;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.IntegrationTests;

/// <summary>XFDF/FDF/CSV w obie strony (pliki w polskich ścieżkach), OCR na skanie.</summary>
public class CommentsFormsOcrTests
{
    private static AnnotationSpec[] Specs() => new[]
    {
        new AnnotationSpec { Kind = AnnotationKind.Highlight, PageIndex = 0, Quads = new[] { PdfQuad.FromRect(new PdfRect(56, 715, 300, 735)) }, Contents = "Wyróżnij – zażółć", Author = "Ąna Ęl", Color = PdfColor.Yellow },
        new AnnotationSpec { Kind = AnnotationKind.Underline, PageIndex = 0, Quads = new[] { PdfQuad.FromRect(new PdfRect(56, 685, 300, 700)) }, Contents = "Podkreśl", Author = "Ąna Ęl" },
        new AnnotationSpec { Kind = AnnotationKind.StrikeOut, PageIndex = 0, Quads = new[] { PdfQuad.FromRect(new PdfRect(56, 660, 300, 675)) }, Contents = "Przekreśl", Author = "Ąna Ęl" },
        new AnnotationSpec { Kind = AnnotationKind.Note, PageIndex = 0, Rect = new PdfRect(450, 700, 470, 720), Contents = "Notatka gęślą jaźń", Author = "Ździsław" },
        new AnnotationSpec { Kind = AnnotationKind.FreeText, PageIndex = 0, Rect = new PdfRect(56, 500, 300, 540), Contents = "Pole tekstowe ŻÓŁĆ", Color = PdfColor.Red, TextStyle = new TextStyle { FontSize = 14 } },
        new AnnotationSpec { Kind = AnnotationKind.Typewriter, PageIndex = 0, Rect = new PdfRect(56, 450, 300, 480), Contents = "Maszyna ąę" },
        new AnnotationSpec { Kind = AnnotationKind.Square, PageIndex = 1, Rect = new PdfRect(100, 300, 200, 400), Color = PdfColor.Red, Contents = "Prostokąt" },
        new AnnotationSpec { Kind = AnnotationKind.Circle, PageIndex = 1, Rect = new PdfRect(250, 300, 350, 400), Color = PdfColor.Red },
        new AnnotationSpec { Kind = AnnotationKind.Arrow, PageIndex = 1, Points = new[] { new PdfPoint(100, 200), new PdfPoint(300, 250) }, Color = PdfColor.Red },
        new AnnotationSpec { Kind = AnnotationKind.Line, PageIndex = 1, Points = new[] { new PdfPoint(100, 150), new PdfPoint(300, 150) } },
        new AnnotationSpec { Kind = AnnotationKind.Ink, PageIndex = 1, InkStrokes = new[] { (IReadOnlyList<PdfPoint>)new[] { new PdfPoint(400, 100), new PdfPoint(450, 150), new PdfPoint(500, 100) } } },
        new AnnotationSpec { Kind = AnnotationKind.Stamp, PageIndex = 1, Rect = new PdfRect(300, 600, 450, 650), StampName = "Approved" },
    };

    [Fact]
    public async Task Xfdf_export_to_polish_path_and_import_into_clean_document_restores_all_comments()
    {
        using var host = new TestHost();
        var svc = host.Get<IAnnotationService>();
        var pdf = SamplePdfFactory.Text(2);
        foreach (var spec in Specs()) pdf = (await svc.AddAsync(pdf, spec)).Pdf;
        var note = (await svc.GetAnnotationsAsync(pdf)).Single(a => a.Kind == AnnotationKind.Note);
        pdf = (await svc.AddReplyAsync(pdf, note.Id, "Odpowiedź źdźbło", "Bożena")).Pdf;
        pdf = await svc.UpdateAsync(pdf, note.Id, new AnnotationUpdate { State = AnnotationReviewState.Accepted });
        var source = await svc.GetAnnotationsAsync(pdf);

        var xfdfPath = Path.Combine(TestPaths.NewDirectory(), "komentarze zażółć.xfdf");
        await using (var fs = File.Create(xfdfPath)) await svc.ExportXfdfAsync(pdf, fs, "źródło gęślą.pdf");

        // poprawny XML XFDF, UTF-8, polskie znaki bez zniekształceń
        var xml = XDocument.Load(xfdfPath);
        XNamespace ns = "http://ns.adobe.com/xfdf/";
        Assert.Equal(ns + "xfdf", xml.Root!.Name);
        Assert.NotNull(xml.Root.Element(ns + "annots"));
        Assert.Contains("Notatka gęślą jaźń", File.ReadAllText(xfdfPath, Encoding.UTF8));
        Assert.Contains("Ździsław", File.ReadAllText(xfdfPath, Encoding.UTF8));

        var clean = SamplePdfFactory.Text(2);
        byte[] imported;
        await using (var fs = File.OpenRead(xfdfPath)) imported = await svc.ImportCommentsAsync(clean, fs);
        var got = await svc.GetAnnotationsAsync(imported);
        Assert.Equal(source.Count, got.Count);
        foreach (var s in source)
        {
            var g = got.Single(x => x.Id.Name == s.Id.Name);
            Assert.Equal(s.Kind, g.Kind);
            Assert.Equal(s.Id.PageIndex, g.Id.PageIndex);
            Assert.Equal(s.Contents, g.Contents);
            Assert.Equal(s.Author, g.Author);
            Assert.Equal(s.InReplyTo?.Name, g.InReplyTo?.Name);
            Assert.Equal(s.State, g.State);
            Assert.True(Math.Abs(s.Rect.Left - g.Rect.Left) < 1 && Math.Abs(s.Rect.Top - g.Rect.Top) < 1 && Math.Abs(s.Rect.Right - g.Rect.Right) < 1 && Math.Abs(s.Rect.Bottom - g.Rect.Bottom) < 1,
                $"{s.Kind}: {s.Rect} vs {g.Rect}");
            if (s.Color is { } c) Assert.Equal(c, g.Color);
        }
        // w pliku: słownik /Annots z właściwymi podtypami, widoczne w renderze PDFium
        var subtypes = Inspect.Annotations(imported).Select(a => a.Subtype).ToHashSet();
        foreach (var st in new[] { "/Highlight", "/Underline", "/StrikeOut", "/Text", "/FreeText", "/Square", "/Circle", "/Line", "/Ink", "/Stamp" })
            Assert.Contains(st, subtypes);
        var (_, color, _) = await Inspect.RenderStats(imported, 1, 72);
        Assert.True(color > 0.002, "zaimportowane kształty niewidoczne");

        // ponowny import tego samego XFDF nie duplikuje komentarzy
        byte[] again;
        await using (var fs = File.OpenRead(xfdfPath)) again = await svc.ImportCommentsAsync(imported, fs);
        Assert.Equal(source.Count, (await svc.GetAnnotationsAsync(again)).Count);
    }

    [Fact]
    public async Task Xfdf_import_merges_with_existing_comments_and_from_other_producer()
    {
        using var host = new TestHost();
        var svc = host.Get<IAnnotationService>();
        var foreign = SamplePdfFactory.WithAnnotations(); // adnotacje PDFsharp (bez /NM)
        var ms = new MemoryStream();
        await svc.ExportXfdfAsync(foreign, ms, null);
        var target = (await svc.AddAsync(SamplePdfFactory.Text(2), Specs()[3])).Pdf;
        var merged = await svc.ImportCommentsAsync(target, new MemoryStream(ms.ToArray()));
        var list = await svc.GetAnnotationsAsync(merged);
        Assert.Contains(list, a => a.Contents == "Notatka testowa – ąęś");
        Assert.Contains(list, a => a.Kind == AnnotationKind.Stamp);
        Assert.Contains(list, a => a.Contents == "Notatka gęślą jaźń");
    }

    [Theory]
    [InlineData(FormDataFormat.Xfdf, "dane formularza ż.xfdf")]
    [InlineData(FormDataFormat.Fdf, "dane formularza ż.fdf")]
    [InlineData(FormDataFormat.Csv, "dane formularza ż.csv")]
    public async Task Form_data_export_and_import_roundtrip_with_polish_values(FormDataFormat format, string fileName)
    {
        using var host = new TestHost();
        var forms = host.Get<IFormService>();
        var fields = await forms.GetFieldsAsync(SamplePdfFactory.WithForm());
        var radioValue = fields.Single(f => f.FullName == "plec").ExportValues.First(v => v != "Off");
        var values = new Dictionary<string, string> { ["imie"] = "Grzegorz Brzęczyszczykiewicz; \"cytat\"", ["miasto"] = "Gdańsk", ["plec"] = radioValue };
        var filled = await forms.SetValuesAsync(SamplePdfFactory.WithForm(), values);

        var path = Path.Combine(TestPaths.NewDirectory(), fileName);
        await using (var fs = File.Create(path)) await forms.ExportDataAsync(filled, fs, format);
        if (format == FormDataFormat.Xfdf) Assert.Contains("Brzęczyszczykiewicz", File.ReadAllText(path, Encoding.UTF8));

        byte[] imported;
        await using (var fs = File.OpenRead(path)) imported = await forms.ImportDataAsync(SamplePdfFactory.WithForm(), fs, format);
        var got = (await forms.GetFieldsAsync(imported)).ToDictionary(f => f.FullName, f => f.Value);
        foreach (var (k, v) in values) Assert.Equal(v, got[k]);
        // niezależnie: PdfPig widzi wartość pola tekstowego
        using var pig = PigDocument.Open(imported);
        Assert.True(pig.TryGetForm(out var form));
        var text = form.Fields.OfType<UglyToad.PdfPig.AcroForms.Fields.AcroTextField>().Single(f => f.Information.PartialName == "imie");
        Assert.Equal(values["imie"], text.Value);
    }

    [Fact]
    public async Task Ocr_on_scan_adds_invisible_searchable_layer_with_expected_words()
    {
        using var host = new TestHost();
        var ocr = host.Get<IOcrService>();
        Assert.True(await ocr.IsAvailableAsync());
        var scan = SamplePdfFactory.Scanned();
        Assert.True(string.IsNullOrWhiteSpace(Inspect.PigText(scan)));
        var before = await Inspect.RenderStats(scan, 0, 72);

        var result = await ocr.RecognizeAsync(scan, new OcrOptions { Languages = "pol+eng" });
        var words = result.Pages.Single().Words.Select(w => w.Text).ToList();
        Assert.Contains("Faktura", words);
        Assert.Contains("12345", words);
        Assert.Contains(words, w => w.Contains("gęślą"));

        var pdf = await ocr.ApplyTextLayerAsync(scan, result);
        var pig = Inspect.PigText(pdf);
        var pdfium = await Inspect.PdfiumText(pdf);
        foreach (var w in new[] { "Faktura", "12345", "gęślą", "16.10.2026" })
        {
            Assert.Contains(w, pig);
            Assert.Contains(w, pdfium);
        }
        // warstwa niewidoczna: render praktycznie bez zmian
        var after = await Inspect.RenderStats(pdf, 0, 72);
        Assert.True(Math.Abs(before.MeanLuma - after.MeanLuma) < 0.5, $"{before.MeanLuma} vs {after.MeanLuma}");
        // wyszukiwanie PDFium znajduje słowo
        using var doc = await Inspect.Renderer.OpenAsync(pdf);
        int hits = 0;
        await foreach (var _ in doc.SearchAsync("faktura", TextSearchOptions.None)) hits++;
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task Ocr_skips_text_pages_and_only_adds_layer_to_scanned_page_of_mixed_document()
    {
        using var host = new TestHost();
        var mixed = await host.Get<IPageOperations>().InsertFromBytesAsync(SamplePdfFactory.Text(1), SamplePdfFactory.Scanned(), null, 1);
        Assert.Equal(2, Inspect.PageCount(mixed));
        var res = await host.Get<IOcrService>().RecognizeAsync(mixed, new OcrOptions { SkipPagesWithText = true });
        Assert.True(res.Pages.Single(p => p.PageIndex == 0).Skipped);
        Assert.False(res.Pages.Single(p => p.PageIndex == 1).Skipped);
        var pdf = await host.Get<IOcrService>().ApplyTextLayerAsync(mixed, res);
        Assert.Contains("Faktura", Inspect.PigPageText(pdf, 1));
        Assert.Equal(1, Operations.CountOf(Inspect.PigPageText(pdf, 0), "Strona 1"));
    }
}

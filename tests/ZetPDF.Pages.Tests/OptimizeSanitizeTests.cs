using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Pages.Engine;
using ZetPDF.Pages.Optimizer;
using ZetPDF.Pages.Sanitize;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Pages.Tests;

public class OptimizeSanitizeTests
{
    [Fact]
    public async Task Optimize_reduces_size_by_recompressing_and_downsampling_images()
    {
        var src = Pdf.WithImage(1600, 1600, noise: true); // 1600 px na 200 pt = 576 DPI
        var res = await new OptimizerService().OptimizeAsync(src, new OptimizeOptions { JpegQuality = 60, MaxImageDpi = 150 });
        Assert.Equal(src.LongLength, res.SizeBefore);
        Assert.True(res.SizeAfter < res.SizeBefore / 2, $"{res.SizeBefore} → {res.SizeAfter}");
        Assert.Equal(1, res.ImagesRecompressed);
        Assert.Contains("Czerwony tekst", Pdf.Text(res.Pdf, 0));
        int width = Pdf.Read(res.Pdf, d => d.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(o => PdfIo.GetName(o, "/Subtype") == "/Image" && o.Elements.ContainsKey("/ColorSpace")).Select(o => (int)PdfIo.GetNumber(o.Elements["/Width"])!).Max());
        Assert.InRange(width, 380, 460); // ≈ 150 DPI × 200/72 cala
        using var doc = await new PdfiumRenderer().OpenAsync(res.Pdf); // plik czytelny dla PDFium
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public async Task Optimize_removes_metadata_and_thumbnails_when_requested()
    {
        var src = PdfSharpEdit.Modify(SamplePdfFactory.Text(2), d =>
        {
            d.Info.Author = "Jan Kowalski";
            var thumb = new PdfDictionary(d);
            thumb.CreateStream(new byte[] { 1, 2, 3 });
            d.Internals.AddObject(thumb);
            d.Pages[0].Elements["/Thumb"] = thumb.Reference!;
        });
        var res = await new OptimizerService().OptimizeAsync(src, new OptimizeOptions { RemoveMetadata = true });
        Assert.DoesNotContain("Kowalski", Pdf.Deep(res.Pdf));
        Assert.False(Pdf.Read(res.Pdf, d => d.Pages[0].Elements.ContainsKey("/Thumb")));
    }

    [Fact]
    public async Task Grayscale_converts_content_colors_and_images()
    {
        var src = Pdf.WithImage(300, 300, noise: false);
        var r = await new OptimizerService().ConvertToGrayscaleAsync(src, rasterize: false);
        var deep = Pdf.Deep(r);
        Assert.DoesNotContain(" rg", deep.Replace("0 0 0 rg", string.Empty));
        Assert.Contains("Czerwony tekst", Pdf.Text(r, 0));
        var spaces = Pdf.Read(r, d => d.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(o => PdfIo.GetName(o, "/Subtype") == "/Image" && o.Elements.ContainsKey("/ColorSpace") && PdfIo.GetName(o, "/ColorSpace") is not null)
            .Select(o => PdfIo.GetName(o, "/ColorSpace")).ToList());
        Assert.All(spaces, s => Assert.Equal("/DeviceGray", s));
        using var doc = await new PdfiumRenderer().OpenAsync(r);
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(0.5));
        for (int i = 0; i < bmp.Bgra.Length; i += 4 * 97)
            Assert.True(Math.Abs(bmp.Bgra[i] - bmp.Bgra[i + 2]) <= 3 && Math.Abs(bmp.Bgra[i + 1] - bmp.Bgra[i + 2]) <= 3, $"kolor w pikselu {i / 4}");
    }

    [Fact]
    public async Task Grayscale_by_rasterization_produces_image_pages()
    {
        var r = await new OptimizerService(new PdfiumRenderer()).ConvertToGrayscaleAsync(SamplePdfFactory.Text(2), rasterize: true);
        Assert.Equal(2, Pdf.PageCount(r));
        Assert.DoesNotContain("Strona", Pdf.AllText(r));
        Assert.Contains("/DeviceGray", Pdf.Deep(r));
    }

    private static PdfSharp.Pdf.PdfDictionary Js(PdfSharp.Pdf.PdfDocument doc, bool disguised)
    {
        var a = new PdfSharp.Pdf.PdfDictionary(doc);
        a.Elements["/S"] = new PdfSharp.Pdf.PdfName("/JavaScript");
        a.Elements["/JS"] = new PdfSharp.Pdf.PdfString("zlosliwy()");
        if (disguised)
        {
            a.Elements["/P"] = new PdfSharp.Pdf.PdfInteger(1);
            a.Elements["/K"] = new PdfSharp.Pdf.PdfInteger(1);
            a.Elements["/Type"] = new PdfSharp.Pdf.PdfName("/StructElem");
        }
        doc.Internals.AddObject(a);
        return a;
    }

    private static PdfSharp.Pdf.PdfDictionary Goto(PdfSharp.Pdf.PdfDocument doc)
    {
        var a = new PdfSharp.Pdf.PdfDictionary(doc);
        a.Elements["/S"] = new PdfSharp.Pdf.PdfName("/GoTo");
        a.Elements["/D"] = new PdfSharp.Pdf.PdfArray(doc, doc.Pages[0].Reference!, new PdfSharp.Pdf.PdfName("/Fit"));
        doc.Internals.AddObject(a);
        return a;
    }

    /// <summary>Skrypty z dopisanymi kluczami w /OpenAction, w polu i w /AA pola, oraz daleko w łańcuchu /Next (także w cyklu).</summary>
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(true, 40)]
    public async Task Sanitize_removes_javascript_regardless_of_keys_and_chain_length(bool disguised, int chainLength)
    {
        var input = ZetPDF.Core.Documents.PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var head = Goto(doc);
            var current = head;
            for (int i = 0; i < chainLength; i++)
            {
                var next = Goto(doc);
                current.Elements["/Next"] = next.Reference!;
                current = next;
            }
            current.Elements["/Next"] = new PdfSharp.Pdf.PdfArray(doc, Js(doc, disguised).Reference!, head.Reference!);
            doc.Internals.Catalog.Elements["/OpenAction"] = head.Reference!;

            var field = new PdfSharp.Pdf.PdfDictionary(doc);
            field.Elements["/T"] = new PdfSharp.Pdf.PdfString("pole");
            field.Elements["/A"] = Js(doc, disguised).Reference!;
            var trigger = new PdfSharp.Pdf.PdfDictionary(doc);
            var deep = Goto(doc);
            var tail = deep;
            for (int i = 0; i < chainLength; i++)
            {
                var next = Goto(doc);
                tail.Elements["/Next"] = next.Reference!;
                tail = next;
            }
            tail.Elements["/Next"] = Js(doc, disguised).Reference!;
            trigger.Elements["/K"] = deep.Reference!;
            field.Elements["/AA"] = trigger;
            doc.Internals.AddObject(field);
            var acro = new PdfSharp.Pdf.PdfDictionary(doc);
            acro.Elements["/Fields"] = new PdfSharp.Pdf.PdfArray(doc, field.Reference!);
            doc.Internals.Catalog.Elements["/AcroForm"] = acro;
        });
        var result = await new SanitizeService().SanitizeAsync(input, new SanitizeOptions());
        var text = System.Text.Encoding.Latin1.GetString(result.Pdf);
        Assert.DoesNotContain("/JavaScript", text);
        Assert.DoesNotContain("zlosliwy", text);
    }

    [Fact]
    public async Task Sanitize_removes_metadata_attachments_javascript_and_comments()
    {
        var src = PdfSharpEdit.Modify(SamplePdfFactory.WithMetadataAttachmentAndJavaScript(), d =>
        {
            d.Info.Author = "Autor Poufny";
            d.Pages[0].Annotations.Add(new PdfTextAnnotation { Contents = "komentarz poufny", Rectangle = new PdfRectangle(new XRect(10, 10, 20, 20)) });
            // łącze z JavaScript i łącze do pliku zewnętrznego
            var js = new PdfDictionary(d);
            js.Elements["/S"] = new PdfName("/JavaScript");
            js.Elements["/JS"] = new PdfString("this.print();");
            var link = new PdfDictionary(d);
            link.Elements["/Type"] = new PdfName("/Annot");
            link.Elements["/Subtype"] = new PdfName("/Link");
            link.Elements["/Rect"] = new PdfRectangle(new XRect(100, 100, 50, 20));
            link.Elements["/A"] = js;
            d.Internals.AddObject(link);
            var launch = new PdfDictionary(d);
            launch.Elements["/S"] = new PdfName("/Launch");
            launch.Elements["/F"] = new PdfString("C:\\Windows\\notepad.exe");
            var link2 = new PdfDictionary(d);
            link2.Elements["/Type"] = new PdfName("/Annot");
            link2.Elements["/Subtype"] = new PdfName("/Link");
            link2.Elements["/Rect"] = new PdfRectangle(new XRect(200, 100, 50, 20));
            link2.Elements["/A"] = launch;
            d.Internals.AddObject(link2);
            var annots = d.Pages[0].Elements.GetArray("/Annots")!;
            annots.Elements.Add(link.Reference!);
            annots.Elements.Add(link2.Reference!);
            var aa = new PdfDictionary(d);
            aa.Elements["/O"] = js;
            d.Pages[0].Elements["/AA"] = aa;
        });
        Assert.Contains("app.alert", Pdf.Deep(src));

        var res = await new SanitizeService().SanitizeAsync(src, new SanitizeOptions());
        var deep = Pdf.Deep(res.Pdf);
        Assert.DoesNotContain("app.alert", deep);
        Assert.DoesNotContain("this.print", deep);
        Assert.DoesNotContain("notepad", deep);
        Assert.DoesNotContain("/EmbeddedFiles", deep);
        Assert.DoesNotContain("/JavaScript", deep);
        Assert.DoesNotContain("Treść załącznika", deep);
        Assert.DoesNotContain("komentarz poufny", deep);
        Assert.DoesNotContain("Poufny", deep);
        Assert.DoesNotContain("Tajny", deep);
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(res.Pdf))
        {
            Assert.True(string.IsNullOrEmpty(pig.Information.Title));
            Assert.True(string.IsNullOrEmpty(pig.Information.Author));
            Assert.Empty(pig.GetPage(1).GetAnnotations());
        }
        Assert.True(res.ScriptsRemoved >= 3, $"scripts {res.ScriptsRemoved}");
        Assert.Equal(1, res.AttachmentsRemoved);
        Assert.Equal(1, res.CommentsRemoved);
        Assert.Equal(1, res.LinksRemoved);
        Assert.True(res.MetadataEntriesRemoved >= 3);
        Assert.Contains("Strona 1", Pdf.Text(res.Pdf, 0));
    }

    [Fact]
    public async Task Sanitize_removes_hidden_layer_content_and_clears_form_data()
    {
        var src = PdfSharpEdit.Modify(SamplePdfFactory.WithForm(), d =>
        {
            var page = d.Pages[0];
            using (var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
                g.DrawString("UKRYTA WARSTWA", new XFont("Arial", 14, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)), XBrushes.Black, 56, 300);
            var last = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)page.Contents.Elements[^1]).Value;
            var data = Encoding.Latin1.GetString(last.Stream.UnfilteredValue);
            last.Elements.Remove("/Filter");
            last.Stream.Value = Encoding.Latin1.GetBytes("/OC /oc1 BDC\n" + data + "\nEMC\n");
            var ocg = new PdfDictionary(d);
            ocg.Elements["/Type"] = new PdfName("/OCG");
            ocg.Elements["/Name"] = new PdfString("Ukryta");
            d.Internals.AddObject(ocg);
            var props = new PdfDictionary(d);
            props.Elements["/oc1"] = ocg.Reference!;
            page.Resources.Elements["/Properties"] = props;
            var ocp = new PdfDictionary(d);
            var arr = new PdfArray(d);
            arr.Elements.Add(ocg.Reference!);
            ocp.Elements["/OCGs"] = arr;
            var dd = new PdfDictionary(d);
            var off = new PdfArray(d);
            off.Elements.Add(ocg.Reference!);
            dd.Elements["/OFF"] = off;
            ocp.Elements["/D"] = dd;
            d.Internals.Catalog.Elements["/OCProperties"] = ocp;
        });
        var filled = PdfSharpEditHelpers.SetTextField(src, "imie", "Wartość Tajna");
        Assert.Contains("UKRYTA", Pdf.Text(filled, 0)); // PdfPig nie stosuje OCG – tekst jest w pliku

        var res = await new SanitizeService().SanitizeAsync(filled, new SanitizeOptions { FormData = true });
        Assert.DoesNotContain("UKRYTA", Pdf.Text(res.Pdf, 0));
        Assert.Contains("Strona 1", Pdf.Text(res.Pdf, 0));
        Assert.Equal(1, res.LayersRemoved);
        var deep = Pdf.Deep(res.Pdf);
        Assert.DoesNotContain("/OCProperties", deep);
        Assert.DoesNotContain("Tajna", deep);
    }
}

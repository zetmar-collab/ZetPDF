using DocumentFormat.OpenXml.Packaging;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class ConversionTests
{
    private readonly ConversionService _svc = new(TestEnv.Renderer);

    private static string Write(string dir, string name, byte[] data)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    [Fact]
    public async Task Images_to_pdf_supports_formats_multipage_tiff_and_page_sizes()
    {
        var dir = TestPaths.NewDirectory();
        var files = new[]
        {
            Write(dir, "obraz ą.png", TestImages.Png(200, 100, alpha: true)),
            Write(dir, "zdjęcie.jpg", TestImages.Jpeg(100, 200)),
            Write(dir, "b.bmp", TestImages.Bmp(50, 50)),
            Write(dir, "wiele.tif", TestImages.Tiff(3, 144, 72, dpi: 72)),
        };
        var pdf = await _svc.ImagesToPdfAsync(files, new ImageToPdfOptions());
        var sizes = PdfSharpEdit.Read(pdf, d => d.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => (p.Width.Point, p.Height.Point)).ToList());
        Assert.Equal(6, sizes.Count);
        Assert.Equal(150, sizes[0].Item1, 0);   // 200 px @96 DPI = 150 pt
        Assert.Equal(144, sizes[3].Item1, 0);   // TIFF 144 px @72 DPI = 144 pt
        var a4 = await _svc.ImagesToPdfAsync(files.Take(2).ToList(), new ImageToPdfOptions { PageSize = ImagePageSize.A4, Margin = 20 });
        var a4sizes = PdfSharpEdit.Read(a4, d => d.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => (p.Width.Point, p.Height.Point)).ToList());
        Assert.Equal(841.9, a4sizes[0].Item1, 0); // obraz poziomy → A4 poziomo
        Assert.Equal(595.3, a4sizes[1].Item1, 0);
        // Obraz zajmuje stronę (render ma kolor obrazu w środku).
        using var doc = await TestEnv.Renderer.OpenAsync(pdf);
        var bmp = await doc.RenderPageAsync(2, RenderRequest.ForDpi(72));
        int mid = bmp.Height / 2 * bmp.Stride + bmp.Width / 2 * 4;
        Assert.True(bmp.Bgra[mid + 2] > 150 && bmp.Bgra[mid] < 60);
    }

    [Fact]
    public async Task Pdf_to_images_in_all_formats()
    {
        var pdf = SamplePdfFactory.Text(3);
        var dir = TestPaths.NewDirectory();
        foreach (var f in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp, ImageFormat.Tiff })
        {
            var files = await _svc.PdfToImagesAsync(pdf, new ImageExportOptions { Format = f, Dpi = 50, PageRange = "1,3" }, dir, "zażółć");
            Assert.Equal(2, files.Count);
            Assert.All(files, p => Assert.True(new FileInfo(p).Length > 100));
            Assert.EndsWith("zażółć_3" + ImageCodecExt(f), files[1]);
        }
        var multi = await _svc.PdfToImagesAsync(pdf, new ImageExportOptions { Format = ImageFormat.Tiff, MultiPageTiff = true, Dpi = 40 }, dir, "wszystkie");
        var single = Assert.Single(multi);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(single), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.Equal(3, decoder.Frames.Count);
        Assert.InRange(decoder.Frames[0].PixelWidth, 329, 332);
    }

    private static string ImageCodecExt(ImageFormat f) => f switch { ImageFormat.Jpeg => ".jpg", ImageFormat.Tiff => ".tif", ImageFormat.Bmp => ".bmp", _ => ".png" };

    [Fact]
    public async Task Pdf_to_text_plain_and_layout()
    {
        var pdf = SamplePdfFactory.Text(2);
        var plain = await _svc.PdfToTextAsync(pdf, new TextExportOptions());
        Assert.Contains("Strona 1", plain);
        Assert.Contains(SamplePdfFactory.PolishPangram, plain);
        Assert.Contains('\f', plain);
        Assert.True(plain.IndexOf("Strona 1", StringComparison.Ordinal) < plain.IndexOf("quick", StringComparison.Ordinal));
        var layout = await _svc.PdfToTextAsync(pdf, new TextExportOptions { PreserveLayout = true, PageRange = "2", PageSeparator = false });
        Assert.Matches(@"Strona\s+2", layout);
        Assert.Contains("Zażółć", layout);
        Assert.DoesNotContain("Strona 1", layout);
    }

    [Fact]
    public async Task Pdf_to_docx_has_paragraphs_formatting_images_and_page_breaks()
    {
        var withImage = await new ObjectEditService().AddImageAsync(SamplePdfFactory.Text(2), 0, TestImages.Png(60, 30, false), new PdfRect(100, 300, 220, 360));
        var combined = PdfSharpEdit.Modify(withImage, d =>
        {
            using var scan = PdfSharpEdit.OpenForImport(SamplePdfFactory.Scanned());
            d.AddPage(scan.Pages[0]);
        });
        var path = Path.Combine(TestPaths.NewDirectory(), "wynik ąę.docx");
        await _svc.PdfToDocxAsync(combined, path, new DocxExportOptions());
        using var word = WordprocessingDocument.Open(path, false);
        var body = word.MainDocumentPart!.Document!.Body!;
        var text = body.InnerText;
        Assert.Contains("Strona 1", text);
        Assert.Contains(SamplePdfFactory.PolishPangram, text);
        var bold = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>().First(r => r.InnerText == "Strona 1");
        Assert.NotNull(bold.RunProperties?.Bold);
        Assert.Equal("48", bold.RunProperties!.FontSize!.Val!.Value);
        Assert.Equal(2, body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Break>().Count());
        Assert.Equal(2, word.MainDocumentPart.ImageParts.Count()); // obraz osadzony + strona-skan
    }

    [Fact]
    public async Task Combine_pdf_images_and_text()
    {
        var dir = TestPaths.NewDirectory();
        var files = new[]
        {
            Write(dir, "a.pdf", SamplePdfFactory.Text(2)),
            Write(dir, "b.png", TestImages.Png(100, 100, false)),
            Write(dir, "notatka ż.txt", System.Text.Encoding.UTF8.GetBytes("Notatka – zażółć gęślą jaźń\nDruga linia")),
        };
        var pdf = await _svc.CombineFilesAsync(files);
        using var doc = await TestEnv.Renderer.OpenAsync(pdf);
        Assert.Equal(4, doc.PageCount);
        Assert.Contains("Notatka – zażółć gęślą jaźń", (await doc.GetPageTextAsync(3)).Text);
    }

    [Fact]
    public async Task Text_to_pdf_wraps_and_paginates()
    {
        var text = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"Wiersz {i}: zażółć gęślą jaźń " + new string('x', i % 7 == 0 ? 200 : 10)));
        var pdf = await _svc.TextToPdfAsync(text);
        using var doc = await TestEnv.Renderer.OpenAsync(pdf);
        Assert.True(doc.PageCount >= 3);
        Assert.Contains("Wiersz 1: zażółć", (await doc.GetPageTextAsync(0)).Text);
        Assert.Contains("Wiersz 120", await TestEnv.PdfiumText(pdf));
    }

    [Fact]
    public async Task Image_data_to_pdf()
    {
        var pdf = await _svc.ImageDataToPdfAsync(TestImages.Png(96, 48, true), new ImageToPdfOptions());
        var size = PdfSharpEdit.Read(pdf, d => (d.Pages[0].Width.Point, d.Pages[0].Height.Point));
        Assert.Equal(72, size.Item1, 0);
        Assert.Equal(36, size.Item2, 0);
    }
}

using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Stamping;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Pages.Tests;

public class StampingTests
{
    private readonly StampingService _st = new();

    [Fact]
    public async Task Text_watermark_with_polish_characters_is_extractable_detectable_and_removable()
    {
        var src = SamplePdfFactory.Text(3);
        var opts = new WatermarkOptions { Text = "POUFNE – zażółć gęślą jaźń", PageRange = "1-2", Opacity = 0.4, RotationDegrees = 30 };
        var r = await _st.AddWatermarkAsync(src, opts);
        Assert.Contains("zażółć gęślą jaźń", Pdf.Text(r, 0));
        Assert.Contains("POUFNE", Pdf.Text(r, 1));
        Assert.DoesNotContain("POUFNE", Pdf.Text(r, 2));
        Assert.Contains("Strona 1", Pdf.Text(r, 0));
        Assert.Equal(PageMarkKind.Watermark, await _st.DetectMarksAsync(r));
        // czcionka osadzona (FontFile2 = TrueType)
        Assert.Contains("/FontFile2", Pdf.Deep(r));

        var stored = await _st.GetStoredOptionsAsync<WatermarkOptions>(r, PageMarkKind.Watermark);
        Assert.Equal(opts.Text, stored!.Text);
        Assert.Equal(0.4, stored.Opacity);

        var removed = await _st.RemoveWatermarksAsync(r);
        Assert.DoesNotContain("POUFNE", Pdf.AllText(removed));
        Assert.Contains("Strona 1", Pdf.Text(removed, 0));
        Assert.Equal(PageMarkKind.None, await _st.DetectMarksAsync(removed));
    }

    [Fact]
    public async Task Watermark_renders_with_pdfium_and_behind_content()
    {
        var r = await _st.AddWatermarkAsync(SamplePdfFactory.Text(1), new WatermarkOptions { Text = "TŁO", BehindContent = true, Style = new TextStyle { FontSize = 120, Color = PdfColor.Red }, Opacity = 1, RotationDegrees = 0 });
        using var doc = await new PdfiumRenderer().OpenAsync(r);
        var text = await doc.GetPageTextAsync(0);
        Assert.Contains("TŁO", text.Text);
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(0.5));
        // środek strony (tam jest znak) – czerwony piksel
        bool red = false;
        for (int y = bmp.Height / 2 - 30; y < bmp.Height / 2 + 30 && !red; y++)
            for (int x = 0; x < bmp.Width && !red; x++)
            {
                int o = y * bmp.Stride + x * 4;
                red = bmp.Bgra[o + 2] > 180 && bmp.Bgra[o + 1] < 80 && bmp.Bgra[o] < 80;
            }
        Assert.True(red);
    }

    [Fact]
    public async Task Image_watermark_and_background_color_image()
    {
        var png = Pdf.PngFile(200, 100);
        var r = await _st.AddWatermarkAsync(SamplePdfFactory.Text(2), new WatermarkOptions { ImagePath = png, ImageScale = 0.5, Opacity = 0.5 });
        Assert.Equal(PageMarkKind.Watermark, await _st.DetectMarksAsync(r));
        Assert.Contains("/ca 0.5", Pdf.Deep(r));

        var bg = await _st.AddBackgroundAsync(r, new BackgroundOptions { Color = PdfColor.Yellow, ImagePath = png, Opacity = 0.8 });
        Assert.Equal(PageMarkKind.Watermark | PageMarkKind.Background, await _st.DetectMarksAsync(bg));
        using (var doc = await new PdfiumRenderer().OpenAsync(bg))
        {
            var bmp = await doc.RenderPageAsync(0, new RenderRequest(0.3));
            int o = 5 * bmp.Stride + 5 * 4; // róg strony – żółte tło (z kryciem 0,8 na białym)
            Assert.True(bmp.Bgra[o] < 120 && bmp.Bgra[o + 2] > 200, $"BGRA {bmp.Bgra[o]},{bmp.Bgra[o + 1]},{bmp.Bgra[o + 2]}");
        }
        var nobg = await _st.RemoveBackgroundsAsync(bg);
        Assert.Equal(PageMarkKind.Watermark, await _st.DetectMarksAsync(nobg));
        Assert.Contains("Strona 2", Pdf.Text(nobg, 1));
    }

    [Fact]
    public async Task Header_footer_macros_and_page_numbers()
    {
        var hf = new HeaderFooterOptions { HeaderLeft = "Raport ąę", HeaderRight = "<<data>>", FooterCenter = "Strona <<strona>> z <<stron>>", DateFormat = "yyyy-MM-dd" };
        var r = await _st.AddHeaderFooterAsync(SamplePdfFactory.Text(3), hf);
        Assert.Contains("Raport ąę", Pdf.Text(r, 0));
        Assert.Contains("Strona 2 z 3", Pdf.Text(r, 1));
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), Pdf.Text(r, 2));

        var n = await _st.AddPageNumbersAsync(r, new PageNumberOptions { Format = "Str. <<page>>/<<pages>>", PageRange = "2-3", StartNumber = 1, Horizontal = HorizontalPosition.Right });
        Assert.Contains("Str. 1/2", Pdf.Text(n, 1));
        Assert.DoesNotContain("Str.", Pdf.Text(n, 0));
        Assert.Equal(PageMarkKind.HeaderFooter | PageMarkKind.PageNumbers, await _st.DetectMarksAsync(n));

        var noHf = await _st.RemoveHeaderFooterAsync(n);
        Assert.DoesNotContain("Raport", Pdf.AllText(noHf));
        Assert.Contains("Str. 2/2", Pdf.Text(noHf, 2));
        var none = await _st.RemovePageNumbersAsync(noHf);
        Assert.DoesNotContain("Str.", Pdf.AllText(none));
    }

    [Fact]
    public async Task Header_footer_position_on_rotated_page_is_upright()
    {
        var rotated = await new ZetPDF.Pages.Engine.PageOperations().RotateAsync(SamplePdfFactory.Text(1), new[] { 0 }, 90);
        var r = await _st.AddHeaderFooterAsync(rotated, new HeaderFooterOptions { HeaderCenter = "NAGŁÓWEK" });
        using var doc = await new PdfiumRenderer().OpenAsync(r);
        var t = await doc.GetPageTextAsync(0);
        int at = t.Text.IndexOf("NAGŁÓWEK", StringComparison.Ordinal);
        Assert.True(at >= 0, t.Text);
        var chars = t.Chars.Skip(at).Take(8).ToList();
        Assert.NotEmpty(chars);
        // obrót 90° w prawo: góra widoku = lewa krawędź strony w przestrzeni użytkownika (x małe), tekst biegnie w górę (y rośnie)
        Assert.True(chars.Average(c => c.Box.Left) < 100, $"x={chars.Average(c => c.Box.Left)}");
        Assert.True(chars[^1].Box.Bottom > chars[0].Box.Bottom);
    }

    [Fact]
    public async Task Bates_numbering_across_files()
    {
        var opts = new BatesOptions { Prefix = "ABC-", Suffix = "-PL", Digits = 5 };
        var first = await _st.AddBatesAsync(SamplePdfFactory.Text(2), opts, 7);
        Assert.Equal((7L, 8L), (first.FirstNumber, first.LastNumber));
        var second = await _st.AddBatesAsync(SamplePdfFactory.Text(3), opts, first.LastNumber + 1);
        Assert.Contains("ABC-00007-PL", Pdf.Text(first.Pdf, 0));
        Assert.Contains("ABC-00011-PL", Pdf.Text(second.Pdf, 2));
        Assert.Equal(PageMarkKind.Bates, await _st.DetectMarksAsync(second.Pdf));
        var removed = await _st.RemoveBatesAsync(second.Pdf);
        Assert.DoesNotContain("ABC-", Pdf.AllText(removed));
    }
}

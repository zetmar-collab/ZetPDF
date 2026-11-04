using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Conversion;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class HtmlExportTests
{
    private readonly ConversionService _svc = new(TestEnv.Renderer);

    private static XFont Font(double size, bool bold = false) =>
        new("Arial", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    private static byte[] Sample(params string[] lines)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using var g = XGraphics.FromPdfPage(page);
        double y = 80;
        foreach (var line in lines)
        {
            g.DrawString(line, Font(11), XBrushes.Black, 56, y);
            y += 40;
        }
        return PdfSharpEdit.Save(doc);
    }

    /// <summary>Tagi i atrybuty, które mogłyby uruchomić kod lub pobrać coś z zewnątrz.</summary>
    private static void AssertSafe(string html)
    {
        Assert.DoesNotMatch(new Regex("<script", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"<(iframe|object|embed|link|base|svg|form|a)\b", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"<[^>]*\son\w+\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"(src|href)\s*=\s*""(?!data:image/(png|jpeg);base64,)", RegexOptions.IgnoreCase), html);
    }

    [Fact]
    public async Task Script_in_pdf_text_is_escaped_and_stays_text()
    {
        var pdf = Sample("<script>alert('x')</script>", "<img src=x onerror=alert(1)>", "a & b \"c\"", "zażółć gęślą jaźń");
        var html = await _svc.PdfToHtmlAsync(pdf, new HtmlExportOptions(), "plik");
        AssertSafe(html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("a &amp; b &quot;c&quot;", html);
        Assert.Contains("zażółć gęślą jaźń", html);
    }

    [Fact]
    public async Task Title_from_metadata_and_file_name_are_escaped()
    {
        var pdf = PdfSharpEdit.Modify(Sample("tekst"), d => d.Info.Title = "</title><script>alert(1)</script>");
        var html = await _svc.PdfToHtmlAsync(pdf, new HtmlExportOptions(), "x");
        AssertSafe(html);
        Assert.Contains("<title>&lt;/title&gt;&lt;script&gt;", html);
        var noTitle = await _svc.PdfToHtmlAsync(Sample("tekst"), new HtmlExportOptions(), "<b>plik</b>");
        Assert.Contains("<title>&lt;b&gt;plik&lt;/b&gt;</title>", noTitle);
    }

    [Fact]
    public async Task Has_blocking_csp_no_external_resources_and_uses_sizes_and_bold()
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("Nagłówek", Font(20, bold: true), XBrushes.Black, 56, 80);
            g.DrawString("Zwykły akapit", Font(11), XBrushes.Black, 56, 140);
        }
        var html = await _svc.PdfToHtmlAsync(PdfSharpEdit.Save(doc), new HtmlExportOptions(), "x");
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("font-size:20pt", html);
        Assert.Contains("<strong>Nagłówek</strong>", html);
        Assert.Contains("Zwykły akapit</p>", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public async Task Images_are_embedded_as_data_uri_and_text_only_removes_them()
    {
        var conv = _svc;
        var pdf = await conv.ImageDataToPdfAsync(TestImages.Png(40, 30, alpha: false), new ImageToPdfOptions());
        var html = await conv.PdfToHtmlAsync(pdf, new HtmlExportOptions(), "x");
        AssertSafe(html);
        Assert.Matches("<img [^>]*src=\"data:image/(png|jpeg);base64,", html);

        var text = await conv.PdfToHtmlAsync(pdf, new HtmlExportOptions { TextOnly = true }, "x");
        Assert.DoesNotContain("<img", text);
        var plain = await conv.PdfToHtmlAsync(Sample("Akapit"), new HtmlExportOptions { TextOnly = true }, "x");
        Assert.Contains("<p>Akapit</p>", plain);
    }

    [Fact]
    public async Task Page_range_is_respected()
    {
        var pdf = SamplePdfFactory.Text(3);
        var html = await _svc.PdfToHtmlAsync(pdf, new HtmlExportOptions { PageRange = "2" }, "x");
        Assert.Equal(1, Regex.Count(html, "<section>"));
    }

    [Fact]
    public void Only_image_signatures_are_accepted_for_data_uri()
    {
        Assert.Equal("image/png", HtmlWriter.MimeOf(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0 }));
        Assert.Equal("image/jpeg", HtmlWriter.MimeOf(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Null(HtmlWriter.MimeOf(System.Text.Encoding.ASCII.GetBytes("<script>alert(1)</script>")));
    }
}

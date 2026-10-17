using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Karta Organizuj – kolejność stron sprawdzana tekstem „Strona N” (PdfPig), rozmiary ramek (PDFsharp).</summary>
public class OrganizePagesTests
{
    private static int[] Order(byte[] pdf)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return d.GetPages().Select(p =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(p.Text, @"Strona\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }).ToArray();
    }

    [Theory]
    [InlineData(new[] { 0 }, 3, new[] { 2, 3, 1 })]
    [InlineData(new[] { 2 }, 0, new[] { 3, 1, 2 })]
    [InlineData(new[] { 0 }, 2, new[] { 2, 1, 3 })]
    [InlineData(new[] { 1, 2 }, 0, new[] { 2, 3, 1 })]
    public async Task Move_pages_follows_drop_before_target_semantics(int[] pages, int target, int[] expected)
    {
        using var host = new TestHost();
        var res = await host.Get<IPageOperations>().MoveAsync(SamplePdfFactory.Text(3), pages, target);
        Assert.Equal(expected, Order(res));
    }

    [Fact]
    public async Task Swap_interleave_and_insert_subset_from_polish_path()
    {
        using var host = new TestHost();
        var ops = host.Get<IPageOperations>();
        Assert.Equal(new[] { 3, 2, 1 }, Order(await ops.SwapAsync(SamplePdfFactory.Text(3), 0, 2)));

        var second = TestPaths.WriteSample(SamplePdfFactory.Text(2, "Drugi plik"), "drugi plik źdźbło.pdf");
        var inter = await ops.InterleaveAsync(SamplePdfFactory.Text(2), second, null, reverseSecond: true);
        Assert.Equal(4, Inspect.PageCount(inter));
        Assert.Contains("Drugi plik", Inspect.PigPageText(inter, 1));
        Assert.Contains("Strona 2", Inspect.PigPageText(inter, 1)); // odwrócona kolejność drugiego pliku
        Assert.Contains("Strona 1", Inspect.PigPageText(inter, 3));

        var src = TestPaths.WriteSample(SamplePdfFactory.Text(5, "Z innego pliku"), "źródło ąę.pdf");
        var ins = await ops.InsertFromFileAsync(SamplePdfFactory.Text(2), src, null, new[] { 3, 4 }, 1);
        Assert.Equal(new[] { 1, 4, 5, 2 }, Order(ins));
        Assert.Contains("Z innego pliku", Inspect.PigPageText(ins, 1));

        var rep = await ops.ReplaceAsync(SamplePdfFactory.Text(3), new[] { 1 }, src, null, new[] { 4 });
        Assert.Equal(new[] { 1, 5, 3 }, Order(rep));
    }

    [Fact]
    public async Task Resize_rotate_and_crop_white_margins_change_page_boxes()
    {
        using var host = new TestHost();
        var ops = host.Get<IPageOperations>();
        var a5 = await ops.ResizeAsync(SamplePdfFactory.Text(2), new[] { 0, 1 }, new ResizeOptions { TargetSize = PdfSize.A5 });
        var sizes = Inspect.Read(a5, d => d.Pages.Cast<PdfPage>().Select(p => (p.MediaBox.Width, p.MediaBox.Height)).ToArray());
        Assert.All(sizes, s => { Assert.InRange(s.Width, PdfSize.A5.Width - 1, PdfSize.A5.Width + 1); Assert.InRange(s.Height, PdfSize.A5.Height - 1, PdfSize.A5.Height + 1); });
        Assert.Contains("gęślą", Inspect.PigText(a5)); // treść przeskalowana, nie utracona

        var rot = await ops.RotateAsync(await ops.RotateAsync(SamplePdfFactory.Text(1), new[] { 0 }, -90), new[] { 0 }, -90);
        Assert.Equal(180, Inspect.Read(rot, d => d.Pages[0].Rotate));

        var cropped = await ops.CropAsync(SamplePdfFactory.Text(1), new[] { 0 }, new CropOptions { RemoveWhiteMargins = true });
        var box = Inspect.Read(cropped, d => (d.Pages[0].CropBox.Width, d.Pages[0].CropBox.Height));
        Assert.True(box.Width < PdfSize.A4.Width - 100 && box.Height < PdfSize.A4.Height - 400, $"białe marginesy nie usunięte: {box}");
        Assert.Contains("gęślą", await Inspect.PdfiumText(cropped));
    }

    [Fact]
    public async Task Page_labels_are_written_and_read_back()
    {
        using var host = new TestHost();
        var ops = host.Get<IPageOperations>();
        var labels = new[] { new PageLabelRange(0, PageLabelStyle.LowerRoman), new PageLabelRange(2, PageLabelStyle.Decimal, "Rozdz-", 1) };
        var pdf = await ops.SetPageLabelsAsync(SamplePdfFactory.Text(4), labels);
        var back = await ops.GetPageLabelsAsync(pdf);
        Assert.Equal(labels, back);
        Assert.Contains("/PageLabels", Inspect.Deep(pdf));
        Assert.Empty(await ops.GetPageLabelsAsync(await ops.SetPageLabelsAsync(pdf, Array.Empty<PageLabelRange>())));
    }
}

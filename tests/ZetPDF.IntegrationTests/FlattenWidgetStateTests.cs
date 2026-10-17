using ZetPDF.Annotations;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Engine;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Spłaszczanie (pages-engine, IPageOperations.FlattenAsync) widżetów pól wyboru i opcji wg /AS:
/// formularz wypełniony przez annotations-forms (FormService) → spłaszczenie → render PDFium bez adnotacji i pól.
/// </summary>
public class FlattenWidgetStateTests
{
    // wnętrza widżetów z SamplePdfFactory.WithForm (bez ramki): pole wyboru "zgoda", opcje "plec" k i m
    private static readonly PdfRect Check = new(59, 563, 69, 573);
    private static readonly PdfRect RadioK = new(59, 523, 67, 531);
    private static readonly PdfRect RadioM = new(123, 523, 131, 531);

    private static async Task<int> Ink(byte[] pdf, PdfRect area)
    {
        using var doc = await new PdfiumRenderer().OpenAsync(pdf);
        var info = doc.Pages[0];
        double scale = 4;
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(scale, Flags: RenderFlags.None));
        int x0 = (int)((area.Left - info.CropOrigin.X) * scale), x1 = (int)((area.Right - info.CropOrigin.X) * scale);
        int y0 = (int)((info.Size.Height - (area.Top - info.CropOrigin.Y)) * scale), y1 = (int)((info.Size.Height - (area.Bottom - info.CropOrigin.Y)) * scale);
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = y * bmp.Stride + x * 4;
                if (bmp.Bgra[o] < 160 && bmp.Bgra[o + 1] < 160 && bmp.Bgra[o + 2] < 160) n++;
            }
        return n;
    }

    private static async Task<byte[]> FilledAndFlattened(string check, string radio)
    {
        var filled = await new FormService().SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["zgoda"] = check, ["plec"] = radio });
        var flat = await new PageOperations().FlattenAsync(filled, new FlattenOptions());
        Assert.Empty(await new FormService().GetFieldsAsync(flat));
        return flat;
    }

    [Fact]
    public async Task Checked_box_and_selected_radio_show_mark_after_flatten_unchecked_do_not()
    {
        var on = await FilledAndFlattened("Yes", "m");
        int checkOn = await Ink(on, Check), kOn = await Ink(on, RadioK), mOn = await Ink(on, RadioM);
        Assert.True(checkOn > 40, $"checked box mark: {checkOn}");
        Assert.True(mOn > 40, $"selected radio m: {mOn}");
        Assert.True(kOn < 5, $"unselected radio k: {kOn}");

        var off = await FilledAndFlattened("Off", "k");
        int checkOff = await Ink(off, Check), kOff = await Ink(off, RadioK), mOff = await Ink(off, RadioM);
        Assert.True(checkOff < 5, $"unchecked box: {checkOff}");
        Assert.True(kOff > 40, $"selected radio k: {kOff}");
        Assert.True(mOff < 5, $"unselected radio m: {mOff}");
    }

    [Fact]
    public async Task Widget_without_AS_uses_field_value()
    {
        var filled = await new FormService().SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["zgoda"] = "Yes" });
        var noAs = ZetPDF.Core.Documents.PdfSharpEdit.Modify(filled, d =>
        {
            foreach (var item in d.Pages[0].Annotations.Elements)
                if ((item as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfSharp.Pdf.PdfDictionary w && w.Elements.GetString("/T") == "zgoda")
                    w.Elements.Remove("/AS");
        });
        var flat = await new PageOperations().FlattenAsync(noAs, new FlattenOptions());
        Assert.True(await Ink(flat, Check) > 40, "checked box drawn from /V when /AS is missing");
    }
}

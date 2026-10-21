using System.Text.Json;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Forms;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.TestData;
using ZXing;
using ZXing.Common;

namespace ZetPDF.Annotations.Tests;

/// <summary>Pole kodu kreskowego: dane z pól formularza, wygląd z ZXing.Net, dekodowanie z renderu PDFium.</summary>
public class BarcodeFieldTests
{
    private static readonly PdfiumRenderer Renderer = new();
    private readonly FormService _forms = new();

    /// <summary>Render strony PDFium (z polami formularza) i dekodowanie kodu przez ZXing.</summary>
    private static async Task<string?> DecodeAsync(byte[] pdf, BarcodeFormat format, PdfRect? area = null, double scale = 3)
    {
        using var doc = await Renderer.OpenAsync(pdf);
        var info = doc.Pages[0];
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(scale, Flags: RenderFlags.Default));
        byte[] px = bmp.Bgra;
        int w = bmp.Width, h = bmp.Height, stride = bmp.Stride;
        if (area is { } a)
        {
            // wycinek renderu wokół widżetu (detektor DataMatrix nie radzi sobie z resztą strony)
            int x0 = Math.Max(0, (int)((a.Left - 10 - info.CropOrigin.X) * scale)), x1 = Math.Min(w, (int)((a.Right + 10 - info.CropOrigin.X) * scale));
            int y0 = Math.Max(0, (int)((info.Size.Height - (a.Top + 10 - info.CropOrigin.Y)) * scale)), y1 = Math.Min(h, (int)((info.Size.Height - (a.Bottom - 10 - info.CropOrigin.Y)) * scale));
            var crop = new byte[(x1 - x0) * (y1 - y0) * 4];
            for (int y = y0; y < y1; y++) Buffer.BlockCopy(px, y * stride + x0 * 4, crop, (y - y0) * (x1 - x0) * 4, (x1 - x0) * 4);
            px = crop; w = x1 - x0; h = y1 - y0;
        }
        else if (stride != w * 4)
        {
            var copy = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * stride, copy, y * w * 4, w * 4);
            px = copy;
        }
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = new[] { format }, TryHarder = true, CharacterSet = "UTF-8" },
        };
        return reader.Decode(px, w, h, RGBLuminanceSource.BitmapFormat.BGRA32)?.Text;
    }

    private static FormFieldSpec Spec(string name, PdfRect rect) => new() { Kind = FormFieldKind.Text, Name = name, PageIndex = 0, Rect = rect };

    [Fact]
    public async Task Qr_field_encodes_selected_fields_and_is_decodable_from_pdfium_render()
    {
        var pdf = await _forms.SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["imie"] = "Zażółć Gęślą", ["zgoda"] = "Yes" });
        pdf = await _forms.CreateBarcodeFieldAsync(pdf, Spec("Kod1", new PdfRect(350, 450, 500, 600)),
            new BarcodeOptions { Symbology = BarcodeSymbology.QrCode, Format = BarcodeDataFormat.TabDelimited, IncludeNames = true, Fields = new[] { "imie", "zgoda" } });
        var field = (await _forms.GetFieldsAsync(pdf)).Single(f => f.FullName == "Kod1");
        Assert.True(field.ReadOnly);
        var zgoda = (await _forms.GetFieldsAsync(pdf)).Single(f => f.FullName == "zgoda").Value;
        var expected = "imie\tzgoda\nZażółć Gęślą\t" + zgoda;
        Assert.Equal(expected, field.Value);
        Assert.Equal(expected, await DecodeAsync(pdf, BarcodeFormat.QR_CODE));
        Assert.NotNull(await _forms.GetBarcodeOptionsAsync(pdf, "Kod1"));
        Assert.Null(await _forms.GetBarcodeOptionsAsync(pdf, "imie"));
    }

    [Fact]
    public async Task Barcode_is_regenerated_after_field_change_reset_and_import()
    {
        var pdf = await _forms.CreateBarcodeFieldAsync(SamplePdfFactory.WithForm(), Spec("Kod", new PdfRect(350, 450, 500, 600)),
            new BarcodeOptions { Format = BarcodeDataFormat.Json, Fields = new[] { "imie" } });
        pdf = await _forms.SetValuesAsync(pdf, new Dictionary<string, string> { ["imie"] = "Jan \"Kowalski\"" });
        var json = await DecodeAsync(pdf, BarcodeFormat.QR_CODE);
        Assert.Equal("Jan \"Kowalski\"", JsonDocument.Parse(json!).RootElement.GetProperty("imie").GetString());
        pdf = await _forms.ResetAsync(pdf);
        Assert.Equal("{\"imie\":\"\"}", await DecodeAsync(pdf, BarcodeFormat.QR_CODE));
        using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("imie\r\nAnna\r\n"));
        pdf = await _forms.ImportDataAsync(pdf, data, FormDataFormat.Csv);
        Assert.Equal("{\"imie\":\"Anna\"}", await DecodeAsync(pdf, BarcodeFormat.QR_CODE));
        // zmiana ustawień: format tabulacyjny bez nazw
        pdf = await _forms.SetBarcodeOptionsAsync(pdf, "Kod", new BarcodeOptions { IncludeNames = false, Fields = new[] { "imie" } });
        Assert.Equal("Anna", await DecodeAsync(pdf, BarcodeFormat.QR_CODE));
    }

    [Theory]
    [InlineData(BarcodeSymbology.DataMatrix, BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeSymbology.Pdf417, BarcodeFormat.PDF_417)]
    public async Task Other_symbologies_are_decodable(BarcodeSymbology sym, BarcodeFormat format)
    {
        var pdf = await _forms.SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["imie"] = "Ala ma kota 123" });
        var rect = sym == BarcodeSymbology.Pdf417 ? new PdfRect(300, 420, 560, 520) : new PdfRect(350, 450, 500, 600);
        pdf = await _forms.CreateBarcodeFieldAsync(pdf, Spec("Kod", rect), new BarcodeOptions { Symbology = sym, IncludeNames = false, Fields = new[] { "imie" } });
        Assert.Equal("Ala ma kota 123", await DecodeAsync(pdf, format, rect));
    }

    [Fact]
    public void Barcode_appearance_is_vector_and_stored_in_widget()
    {
        var pdf = _forms.CreateBarcodeFieldAsync(SamplePdfFactory.WithForm(), Spec("Kod", new PdfRect(350, 450, 500, 600)), new BarcodeOptions()).GetAwaiter().GetResult();
        PdfSharpEdit.Read(pdf, doc =>
        {
            var node = FieldTree.Find(doc, "Kod")!;
            var ap = PdfLow.Dict(PdfLow.Dict(node.Widgets[0], "/AP")!, "/N")!;
            var content = System.Text.Encoding.ASCII.GetString(ap.Stream.UnfilteredValue);
            Assert.Contains(" re", content);
            Assert.Equal("/Tx", node.FieldType);
            Assert.Equal("/QR", PdfLow.NameOf(PdfLow.Dict(PdfLow.Dict(node.Dict, "/ZetPDF")!, "/Barcode")!, "/Sym"));
            return 0;
        });
    }
}

using System.Text;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;
using ZetPDF.Core.Contracts;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Annotations.Tests;

public class FormServiceTests
{
    private readonly FormService _svc = new();

    [Fact]
    public async Task Reads_sample_form()
    {
        var fields = await _svc.GetFieldsAsync(SamplePdfFactory.WithForm());
        Assert.Equal(FormFieldKind.Text, fields.Single(f => f.FullName == "imie").Kind);
        Assert.Equal(FormFieldKind.CheckBox, fields.Single(f => f.FullName == "zgoda").Kind);
        var plec = fields.Single(f => f.FullName == "plec");
        Assert.Equal(FormFieldKind.RadioButton, plec.Kind);
        Assert.Equal(new[] { "k", "m" }, plec.ExportValues);
        var miasto = fields.Single(f => f.FullName == "miasto");
        Assert.Equal(FormFieldKind.ComboBox, miasto.Kind);
        Assert.Contains("Kraków", miasto.Options);
    }

    [Fact]
    public async Task Filled_values_are_saved_with_appearance_and_visible_in_pdfium()
    {
        var pdf = SamplePdfFactory.WithForm();
        var before = await RenderCheck.NonWhitePixels(pdf, 0, new PdfRect(58, 602, 274, 620));
        var filled = await _svc.SetValuesAsync(pdf, new Dictionary<string, string>
        {
            ["imie"] = "Zażółć Gęślą",
            ["zgoda"] = "Yes",
            ["plec"] = "m",
            ["miasto"] = "Kraków",
        });
        var path = TestPaths.WriteSample(filled);
        var saved = await File.ReadAllBytesAsync(path);
        var fields = await _svc.GetFieldsAsync(saved);
        Assert.Equal("Zażółć Gęślą", fields.Single(f => f.FullName == "imie").Value);
        Assert.Equal("Yes", fields.Single(f => f.FullName == "zgoda").Value);
        Assert.Equal("m", fields.Single(f => f.FullName == "plec").Value);
        Assert.Equal("Kraków", fields.Single(f => f.FullName == "miasto").Value);
        Assert.Contains("/NeedAppearances false", Encoding.Latin1.GetString(saved));

        // render PDFium (FFLDraw jak w widoku) pokazuje wartości
        Assert.True(await RenderCheck.NonWhitePixels(saved, 0, new PdfRect(58, 602, 274, 620)) > before + 30, "text value not visible");
        Assert.True(await RenderCheck.NonWhitePixels(saved, 0, new PdfRect(58, 562, 70, 574)) > 5, "check not visible");
        Assert.True(await RenderCheck.NonWhitePixels(saved, 0, new PdfRect(122, 522, 132, 532)) > 5, "radio not visible");
        Assert.True(await RenderCheck.NonWhitePixels(saved, 0, new PdfRect(58, 472, 200, 490)) > 30, "combo not visible");
        // sam wygląd /AP (spłaszczony do treści strony) zawiera wartość – inne czytniki pokażą to samo
        var flat = await new AnnotationService().FlattenAsync(saved, includeFormFields: true);
        Assert.Empty(await _svc.GetFieldsAsync(flat));
        Assert.True(await RenderCheck.NonWhitePixels(flat, 0, new PdfRect(58, 602, 274, 620)) > before + 30);

        // PdfPig widzi wartości
        using var pig = PigDocument.Open(saved);
        Assert.True(pig.TryGetForm(out var form));
        var text = form.GetFields().OfType<AcroTextField>().Single(f => f.Information.PartialName == "imie");
        Assert.Equal("Zażółć Gęślą", text.Value);
    }

    [Fact]
    public async Task Reset_restores_defaults()
    {
        var filled = await _svc.SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["imie"] = "X", ["zgoda"] = "Yes" });
        var reset = await _svc.ResetAsync(filled);
        var fields = await _svc.GetFieldsAsync(reset);
        Assert.Equal("", fields.Single(f => f.FullName == "imie").Value);
        Assert.Equal("Off", fields.Single(f => f.FullName == "zgoda").Value);
    }

    [Fact]
    public async Task Created_fields_are_visible_in_pdfpig_and_render()
    {
        var pdf = SamplePdfFactory.Text(1);
        var specs = new[]
        {
            new FormFieldSpec { Kind = FormFieldKind.Text, Name = "nazwisko", PageIndex = 0, Rect = new(60, 500, 260, 522), DefaultValue = "Łódź", Tooltip = "Nazwisko", Required = true },
            new FormFieldSpec { Kind = FormFieldKind.Text, Name = "uwagi", PageIndex = 0, Rect = new(60, 420, 260, 490), Multiline = true },
            new FormFieldSpec { Kind = FormFieldKind.CheckBox, Name = "zgoda", PageIndex = 0, Rect = new(60, 390, 76, 406), DefaultValue = "Yes" },
            new FormFieldSpec { Kind = FormFieldKind.RadioButton, Name = "opcja", PageIndex = 0, Rect = new(60, 360, 74, 374), ExportValue = "a", DefaultValue = "a" },
            new FormFieldSpec { Kind = FormFieldKind.RadioButton, Name = "opcja", PageIndex = 0, Rect = new(90, 360, 104, 374), ExportValue = "b" },
            new FormFieldSpec { Kind = FormFieldKind.ComboBox, Name = "miasto", PageIndex = 0, Rect = new(60, 320, 200, 342), Options = new[] { "Gdańsk", "Poznań" }, DefaultValue = "Poznań" },
            new FormFieldSpec { Kind = FormFieldKind.ListBox, Name = "lista", PageIndex = 0, Rect = new(300, 300, 450, 360), Options = new[] { "Jeden", "Dwa", "Trzy" } },
            new FormFieldSpec { Kind = FormFieldKind.PushButton, Name = "reset", PageIndex = 0, Rect = new(300, 500, 380, 522), ButtonAction = FormButtonAction.ResetForm, ButtonCaption = "Wyczyść" },
            new FormFieldSpec { Kind = FormFieldKind.PushButton, Name = "www", PageIndex = 0, Rect = new(400, 500, 480, 522), ButtonAction = FormButtonAction.OpenUrl, ButtonUrl = "https://example.com", ButtonCaption = "WWW" },
            new FormFieldSpec { Kind = FormFieldKind.Signature, Name = "podpis", PageIndex = 0, Rect = new(300, 200, 500, 260) },
            new FormFieldSpec { Kind = FormFieldKind.Date, Name = "data", PageIndex = 0, Rect = new(60, 260, 200, 282), DateFormat = "dd.MM.yyyy" },
        };
        foreach (var s in specs) pdf = await _svc.CreateFieldAsync(pdf, s);

        var fields = await _svc.GetFieldsAsync(pdf);
        Assert.Equal(10, fields.Count);
        Assert.Equal(FormFieldKind.Date, fields.Single(f => f.FullName == "data").Kind);
        Assert.Equal(FormFieldKind.Signature, fields.Single(f => f.FullName == "podpis").Kind);
        Assert.False(fields.Single(f => f.FullName == "podpis").IsSigned);
        Assert.Equal(new[] { "a", "b" }, fields.Single(f => f.FullName == "opcja").ExportValues);
        Assert.Equal("a", fields.Single(f => f.FullName == "opcja").Value);
        Assert.True(fields.Single(f => f.FullName == "nazwisko").Required);
        Assert.Equal("Nazwisko", fields.Single(f => f.FullName == "nazwisko").Tooltip);
        Assert.True(fields.Single(f => f.FullName == "uwagi").Multiline);
        Assert.Contains("/ResetForm", Encoding.Latin1.GetString(pdf));
        Assert.DoesNotContain("/JavaScript", Encoding.Latin1.GetString(pdf));

        using (var pig = PigDocument.Open(pdf))
        {
            Assert.True(pig.TryGetForm(out var form));
            var names = form.GetFields().Select(f => f.Information.PartialName).ToList();
            foreach (var n in new[] { "nazwisko", "uwagi", "zgoda", "opcja", "miasto", "lista", "reset", "podpis", "data" }) Assert.Contains(n, names);
            Assert.Contains(form.GetFields(), f => f is AcroSignatureField);
        }

        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, new PdfRect(60, 500, 260, 522)) > 50, "text field not visible");
        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, new PdfRect(300, 500, 380, 522)) > 50, "button not visible");
        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, new PdfRect(60, 390, 76, 406)) > 10, "checkbox not visible");
    }

    [Fact]
    public async Task Update_delete_and_tab_order()
    {
        var pdf = SamplePdfFactory.Text(1);
        pdf = await _svc.CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "a", PageIndex = 0, Rect = new(60, 500, 160, 520) });
        pdf = await _svc.CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "b", PageIndex = 0, Rect = new(60, 460, 160, 480) });
        pdf = await _svc.CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.ComboBox, Name = "c", PageIndex = 0, Rect = new(60, 420, 160, 440), Options = new[] { "x" } });
        var ordered = await _svc.SetTabOrderAsync(pdf, 0, new[] { "c", "a", "b" });
        var fields = await _svc.GetFieldsAsync(ordered);
        Assert.Equal(new[] { "c", "a", "b" }, fields.OrderBy(f => f.TabOrder).Select(f => f.FullName));
        Assert.Matches(@"/Tabs\s*/W", Encoding.Latin1.GetString(ordered));

        var updated = await _svc.UpdateFieldAsync(ordered, "c", new FormFieldSpec { Kind = FormFieldKind.ComboBox, Name = "miasto", PageIndex = 0, Rect = new(200, 420, 300, 440), Options = new[] { "x", "y" }, ReadOnly = true });
        var u = (await _svc.GetFieldsAsync(updated)).Single(f => f.FullName == "miasto");
        Assert.Equal(new[] { "x", "y" }, u.Options);
        Assert.True(u.ReadOnly);
        Assert.Equal(new PdfRect(200, 420, 300, 440), u.Rect);

        var deleted = await _svc.DeleteFieldsAsync(updated, new[] { "a" });
        Assert.DoesNotContain(await _svc.GetFieldsAsync(deleted), f => f.FullName == "a");
    }

    [Theory]
    [InlineData(FormDataFormat.Fdf)]
    [InlineData(FormDataFormat.Xfdf)]
    [InlineData(FormDataFormat.Csv)]
    public async Task Data_export_import_roundtrip(FormDataFormat format)
    {
        var filled = await _svc.SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string>
        {
            ["imie"] = "Anna, \"Ż\"", ["zgoda"] = "Yes", ["plec"] = "k", ["miasto"] = "Gdańsk",
        });
        using var ms = new MemoryStream();
        await _svc.ExportDataAsync(filled, ms, format);
        var bytes = ms.ToArray();
        if (format == FormDataFormat.Csv) Assert.StartsWith("imie,", Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        var imported = await _svc.ImportDataAsync(SamplePdfFactory.WithForm(), new MemoryStream(bytes), format);
        var fields = await _svc.GetFieldsAsync(imported);
        Assert.Equal("Anna, \"Ż\"", fields.Single(f => f.FullName == "imie").Value);
        Assert.Equal("Yes", fields.Single(f => f.FullName == "zgoda").Value);
        Assert.Equal("k", fields.Single(f => f.FullName == "plec").Value);
        Assert.Equal("Gdańsk", fields.Single(f => f.FullName == "miasto").Value);
    }

    [Fact]
    public void Csv_parser_handles_quotes_and_semicolons()
    {
        var rows = Forms.FormDataIO.ParseCsv("a;b\r\n\"x;1\";\"y \"\"z\"\"\"\r\n");
        Assert.Equal(new[] { "a", "b" }, rows[0]);
        Assert.Equal(new[] { "x;1", "y \"z\"" }, rows[1]);
    }
}

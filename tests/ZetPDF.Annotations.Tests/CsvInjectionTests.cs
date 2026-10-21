using System.Globalization;
using System.Text;
using ZetPDF.Annotations.Measure;
using ZetPDF.Core.Contracts;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>Neutralizacja wstrzykiwania formuł we wszystkich eksportach CSV modułu (pomiary, dane formularza).</summary>
public class CsvInjectionTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+48 123", "'+48 123")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("Jan", "Jan")]
    [InlineData("", "")]
    public void Neutralize_prefixes_formula_starters(string input, string expected)
    {
        Assert.Equal(expected, CsvText.Neutralize(input));
        Assert.Equal(input, CsvText.Restore(CsvText.Neutralize(input)));
    }

    [Fact]
    public void Program_numbers_are_not_neutralized_and_quoting_still_applies()
    {
        Assert.Equal("-12", CsvText.Cell("-12", ';', neutralize: false));
        Assert.Equal("\"-12,5\"", CsvText.Cell("-12,5", ';', neutralize: false));
        Assert.Equal("\"'=A;B\"", CsvText.Cell("=A;B", ';'));
    }

    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    public void Values_with_any_separator_are_always_quoted(char sep)
    {
        Assert.Equal("\"a,=1+1\"", CsvText.Cell("a,=1+1", sep));
        Assert.Equal("\"a;=1+1\"", CsvText.Cell("a;=1+1", sep));
        Assert.Equal("\"a\t@x\"", CsvText.Cell("a\t@x", sep));
        Assert.Equal("\"'=a,b\"", CsvText.Cell("=a,b", sep));
    }

    [Fact]
    public async Task Csv_import_skips_sep_line()
    {
        var parsed = Forms.FormDataIO.ParseCsv("sep=;\r\nimie;zgoda\r\n\"Kowalski, Jan\";Yes\r\n");
        Assert.Equal(new[] { "imie", "zgoda" }, parsed[0]);
        Assert.Equal("Kowalski, Jan", parsed[1][0]);
        var forms = new FormService();
        var data = Encoding.UTF8.GetBytes("sep=;\r\nimie\r\n\"Zażółć, gęślą\"\r\n");
        var pdf = await forms.ImportDataAsync(SamplePdfFactory.WithForm(), new MemoryStream(data), FormDataFormat.Csv);
        Assert.Equal("Zażółć, gęślą", (await forms.GetFieldsAsync(pdf)).Single(f => f.FullName == "imie").Value);
    }

    [Fact]
    public async Task Measurement_csv_neutralizes_author_formula()
    {
        var svc = new MeasureService(new AnnotationService());
        var (pdf, _) = await svc.AddAsync(SamplePdfFactory.Text(1), new MeasureSpec
        {
            Type = MeasureType.Distance, PageIndex = 0, Author = "=HYPERLINK(\"http://x\",\"y\")",
            Points = new[] { new PdfPoint(10, 10), new PdfPoint(100, 10) },
        });
        var csv = Encoding.UTF8.GetString(svc.ExportCsv(await svc.GetMeasurementsAsync(pdf))).TrimStart('﻿');
        var cells = Forms.FormDataIO.ParseCsv(csv)[1];
        Assert.StartsWith("'=HYPERLINK(", cells[4]);
        Assert.DoesNotContain(cells, c => c.StartsWith('='));
    }

    [Fact]
    public async Task Form_data_csv_neutralizes_values_and_round_trips()
    {
        var forms = new FormService();
        var pdf = await forms.SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["imie"] = "=HYPERLINK(\"http://x\")" });
        using var ms = new MemoryStream();
        await forms.ExportDataAsync(pdf, ms, FormDataFormat.Csv);
        var text = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
        var rows = Forms.FormDataIO.ParseCsv(text);
        int i = rows[0].IndexOf("imie");
        Assert.StartsWith("'=HYPERLINK(", rows[1][i]);
        var back = await forms.ImportDataAsync(SamplePdfFactory.WithForm(), new MemoryStream(ms.ToArray()), FormDataFormat.Csv);
        Assert.Equal("=HYPERLINK(\"http://x\")", (await forms.GetFieldsAsync(back)).Single(f => f.FullName == "imie").Value);
        _ = CultureInfo.InvariantCulture;
    }
}

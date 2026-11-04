using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Conversion;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class XlsxExportTests
{
    private readonly ConversionService _svc = new(TestEnv.Renderer);

    private static XFont Font(double size = 10) => new("Arial", size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    /// <summary>Strona z napisami w podanych miejscach (x, y od góry, tekst).</summary>
    internal static byte[] Page(params (double X, double Y, string Text)[] items) => Pages(items);

    private static byte[] Pages(params (double X, double Y, string Text)[][] pages)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        foreach (var items in pages)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            foreach (var (x, y, text) in items) g.DrawString(text, Font(), XBrushes.Black, x, y);
        }
        return PdfSharpEdit.Save(doc);
    }

    private static (double, double, string)[] Invoice() => new (double, double, string)[]
    {
        (56, 60, "Faktura zażółć gęślą jaźń numer siedem"),
        (56, 100, "Towar"), (220, 100, "Ilość"), (320, 100, "Cena"),
        (56, 116, "Śruba"), (220, 116, "12"), (320, 116, "1 234,50"),
        (56, 132, "Nakrętka"), (220, 132, "3"), (320, 132, "45,20"),
        (56, 148, "Podkładka"), (220, 148, "7"), (320, 148, "0,99"),
        (56, 220, "Uwagi na końcu dokumentu"),
    };

    private async Task<SheetContent[]> Export(byte[] pdf, XlsxExportOptions? options = null)
    {
        var path = Path.Combine(TestPaths.NewDirectory(), "wynik ą.xlsx");
        await _svc.PdfToXlsxAsync(pdf, path, options ?? new XlsxExportOptions());
        return Read(path);
    }

    private sealed record SheetContent(string Name, List<Cell> Cells)
    {
        public Cell? Find(string text) => Cells.FirstOrDefault(c => Value(c) == text);
        public IEnumerable<string> Values => Cells.Select(Value);
    }

    private static string Value(Cell c) => c.DataType?.Value == CellValues.InlineString ? c.InlineString!.Text!.Text : c.CellValue!.Text;

    private static SheetContent[] Read(string path)
    {
        using var xl = SpreadsheetDocument.Open(path, false);
        var wb = xl.WorkbookPart!;
        return wb.Workbook!.Sheets!.Cast<Sheet>().Select(s =>
        {
            var part = (WorksheetPart)wb.GetPartById(s.Id!);
            return new SheetContent(s.Name!, part.Worksheet!.Descendants<Cell>().ToList());
        }).ToArray();
    }

    [Fact]
    public async Task Table_goes_to_columns_numbers_are_numbers_and_text_goes_to_rows()
    {
        var sheets = await Export(Page(Invoice()));
        var sheet = Assert.Single(sheets);
        // kolumny: Towar w A, Ilość w B, Cena w C, wiersze kolejne
        var header = sheet.Find("Towar")!;
        int row = int.Parse(header.CellReference!.Value![1..], CultureInfo.InvariantCulture);
        Assert.StartsWith("A", header.CellReference!.Value);
        Assert.Equal("B" + row, sheet.Find("Ilość")!.CellReference!.Value);
        Assert.Equal("C" + row, sheet.Find("Cena")!.CellReference!.Value);
        var price = sheet.Cells.Single(c => c.CellReference!.Value == "C" + (row + 1));
        Assert.Equal(CellValues.Number, price.DataType!.Value);
        Assert.Equal(1234.5, double.Parse(price.CellValue!.Text, CultureInfo.InvariantCulture));
        Assert.Equal(CellValues.Number, sheet.Cells.Single(c => c.CellReference!.Value == "B" + (row + 1)).DataType!.Value);
        // tekst spoza tabeli w osobnych wierszach w kolumnie A
        Assert.StartsWith("A", sheet.Find("Faktura zażółć gęślą jaźń numer siedem")!.CellReference!.Value);
        Assert.StartsWith("A", sheet.Find("Uwagi na końcu dokumentu")!.CellReference!.Value);
    }

    [Fact]
    public async Task Sheet_modes_page_and_table()
    {
        var pdf = Pages(Invoice(), new (double, double, string)[] { (56, 100, "Tylko akapit na drugiej stronie") });
        var perPage = await Export(pdf);
        Assert.Equal(new[] { "Page 1", "Page 2" }, perPage.Select(s => s.Name));
        var perTable = await Export(pdf, new XlsxExportOptions { SheetMode = XlsxSheetMode.PerTable });
        Assert.Equal(2, perTable.Length);
        Assert.Equal("Table 1 (p. 1)", perTable[0].Name);
        Assert.NotNull(perTable[0].Find("Towar"));
        Assert.Null(perTable[0].Find("Uwagi na końcu dokumentu"));
        Assert.Equal("Text", perTable[1].Name);
        Assert.NotNull(perTable[1].Find("Tylko akapit na drugiej stronie"));
    }

    [Fact]
    public async Task Page_range_limits_export_and_page_without_text_gets_a_note()
    {
        var pdf = Pages(Invoice(), Array.Empty<(double, double, string)>());
        var path = Path.Combine(TestPaths.NewDirectory(), "a.xlsx");
        var result = await _svc.PdfToXlsxAsync(pdf, path, new XlsxExportOptions { PageRange = "2" });
        Assert.Equal(new XlsxExportResult(1, 0, 1), result);
        var sheet = Assert.Single(Read(path));
        Assert.Equal("Page 2", sheet.Name);
        Assert.Contains("no text", Assert.Single(sheet.Values));
    }

    [Fact]
    public async Task Document_content_is_never_a_formula()
    {
        var evil = new[] { "=HYPERLINK(\"http://x\",\"a\")", "=1+1", "+SUM(A1:A2)", "@SUM(A1)", "=cmd|'/c calc'!A1", "-2+3" };
        var items = evil.Select((e, i) => (56.0, 100.0 + i * 16, e))
            .Concat(evil.Select((e, i) => (300.0, 100.0 + i * 16, "wartość" + i))).ToArray();
        var path = Path.Combine(TestPaths.NewDirectory(), "f.xlsx");
        await _svc.PdfToXlsxAsync(Page(items), path, new XlsxExportOptions());

        using var xl = SpreadsheetDocument.Open(path, false);
        var part = xl.WorkbookPart!.WorksheetParts.Single();
        Assert.Empty(part.Worksheet!.Descendants<CellFormula>());
        Assert.DoesNotContain("<f>", part.Worksheet!.OuterXml);
        var stylesheet = xl.WorkbookPart.WorkbookStylesPart!.Stylesheet!;
        var formats = stylesheet.CellFormats!.Cast<CellFormat>().ToList();
        foreach (var e in evil)
        {
            var cell = part.Worksheet!.Descendants<Cell>().Single(c => c.InlineString?.Text?.Text == e);
            Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
            Assert.True(formats[(int)cell.StyleIndex!.Value].QuotePrefix!.Value, e);
        }
    }

    [Fact]
    public void Illegal_characters_in_text_are_removed()
    {
        Assert.Equal("ab", XlsxWriter.CleanText("a" + (char)1 + (char)0xFFFF + (char)0xD800 + "b"));
        Assert.Equal("a" + char.ConvertFromUtf32(0x1F600), XlsxWriter.CleanText("a" + char.ConvertFromUtf32(0x1F600)));
    }

    [Theory]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1 234 567", 1234567)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("12,5", 12.5)]
    [InlineData("12.5", 12.5)]
    [InlineData("0,123", 0.123)]
    [InlineData("0", 0)]
    [InlineData("-7", -7)]
    [InlineData("+7,50", 7.5)]
    public void Numbers_in_polish_and_english_format(string text, double expected)
    {
        Assert.True(CellNumberParser.TryParse(text, DecimalSeparatorHint.Unknown, out var v), text);
        Assert.Equal(expected, v, 6);
    }

    [Fact]
    public void Non_breaking_group_separator_and_true_minus_are_understood()
    {
        Assert.True(CellNumberParser.TryParse("1" + (char)0x00A0 + "234,5", DecimalSeparatorHint.Unknown, out var a));
        Assert.Equal(1234.5, a);
        Assert.True(CellNumberParser.TryParse((char)0x2212 + "3,5", DecimalSeparatorHint.Unknown, out var b));
        Assert.Equal(-3.5, b);
    }

    [Theory]
    [InlineData("00123")]            // kod z zerami
    [InlineData("00-950")]           // kod pocztowy
    [InlineData("44051401358")]      // PESEL
    [InlineData("12.05.2024")]       // data
    [InlineData("2024-05-12")]       // data
    [InlineData("+48 123 456 789")]  // telefon
    [InlineData("12,50 zł")]         // jednostka
    [InlineData("12%")]
    [InlineData("1e5")]
    [InlineData("1,234")]            // niejednoznaczne bez wskazówki
    [InlineData("1.234")]
    [InlineData("1234567890123456,5")] // ponad 15 cyfr znaczących
    [InlineData("abc")]
    public void Not_numbers_stay_text(string text) => Assert.False(CellNumberParser.TryParse(text, DecimalSeparatorHint.Unknown, out _), text);

    [Fact]
    public void Ambiguous_numbers_follow_the_rest_of_the_document()
    {
        Assert.True(CellNumberParser.TryParse("1,234", DecimalSeparatorHint.Comma, out var a));
        Assert.Equal(1.234, a, 6);
        Assert.True(CellNumberParser.TryParse("1,234", DecimalSeparatorHint.Dot, out var b));
        Assert.Equal(1234, b);
        Assert.True(CellNumberParser.TryParse("1.234", DecimalSeparatorHint.Comma, out var c));
        Assert.Equal(1234, c);
        Assert.True(CellNumberParser.TryParse("1.234", DecimalSeparatorHint.Dot, out var d));
        Assert.Equal(1.234, d, 6);
        // w dokumencie z przecinkiem „12.05” to nie liczba (data), a w dokumencie z kropką – „12,05”
        Assert.False(CellNumberParser.TryParse("12.05", DecimalSeparatorHint.Comma, out _));
        Assert.False(CellNumberParser.TryParse("12,05", DecimalSeparatorHint.Dot, out _));

        Assert.Equal(DecimalSeparatorHint.Comma, CellNumberParser.DetectHint(new[] { "12,50", "3,99", "1 234,50", "1,234" }));
        Assert.Equal(DecimalSeparatorHint.Dot, CellNumberParser.DetectHint(new[] { "12.50", "1,234.99", "3.5" }));
        Assert.Equal(DecimalSeparatorHint.Unknown, CellNumberParser.DetectHint(new[] { "1,234", "tekst", "12" }));
        Assert.Equal(DecimalSeparatorHint.Unknown, CellNumberParser.DetectHint(new[] { "12,50", "12.50" }));
    }

    [Fact]
    public void Prose_in_two_columns_and_single_line_gaps_are_not_tables()
    {
        var words = new List<LayoutWord>();
        void Line(double x, double y, string text)
        {
            double at = x;
            foreach (var w in text.Split(' '))
            {
                words.Add(new LayoutWord(w, at, at + w.Length * 5, y, 10));
                at += w.Length * 5 + 2.5;
            }
        }
        for (int i = 0; i < 6; i++)
        {
            Line(50, 700 - i * 12, "lewa szpalta ma tu sporo zwykłych słów");
            Line(320, 700 - i * 12, "prawa szpalta też ma sporo zwykłych słów");
        }
        Assert.All(TableDetector.Analyze(words), b => Assert.False(b.IsTable));

        words.Clear();
        Line(50, 700, "Nagłówek");
        Line(400, 700, "Data");
        Line(50, 600, "Akapit bez kolumn");
        Assert.All(TableDetector.Analyze(words), b => Assert.False(b.IsTable));
    }

    [Fact]
    public void Sheet_names_are_cleaned_and_unique()
    {
        var used = new HashSet<string>();
        Assert.Equal("a_b_c", XlsxWriter.UniqueName("a:b/c", used));
        Assert.Equal("a_b_c 2", XlsxWriter.UniqueName("a:b/c", used));
        Assert.Equal(31, XlsxWriter.UniqueName(new string('x', 50), used).Length);
    }

    [Fact]
    public async Task Cancelling_leaves_no_files()
    {
        var dir = TestPaths.NewDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _svc.PdfToXlsxAsync(SamplePdfFactory.Text(2), Path.Combine(dir, "x.xlsx"), new XlsxExportOptions(), null, cts.Token));
        Assert.Empty(Directory.GetFiles(dir));
    }
}

using System.Globalization;
using System.Runtime.ExceptionServices;
using ZetPDF.Annotations.Measure;
using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;
using Microsoft.Extensions.DependencyInjection;

namespace ZetPDF.Annotations.Tests;

/// <summary>Kalkulator księgowy: rozpoznawanie liczb (format polski i angielski, nawiasy ujemne), taśma, okno.</summary>
public class CalculatorTests
{
    private static readonly CultureInfo Pl = new("pl-PL");
    private static readonly CultureInfo En = new("en-US");

    [Theory]
    [InlineData("Kwota: 1 234,56 zł", "1234.56")]
    [InlineData("Saldo -12,5", "-12.5")]
    [InlineData("Strata (1 234,00) PLN", "-1234.00")]
    [InlineData("1 000 000,01", "1000000.01")]
    [InlineData("12.345.678,9", "12345678.9")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("0,500", "0.500")]
    [InlineData("1.234", "1234")]
    [InlineData("1,234", "1.234")]
    [InlineData("€12,30", "12.30")]
    public void Parses_polish_number_formats(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Assert.Single(NumberParser.Parse(text, Pl)));

    [Fact]
    public void Ambiguous_separators_follow_culture_and_many_numbers_are_found()
    {
        Assert.Equal(1.234m, Assert.Single(NumberParser.Parse("1.234", En)));
        Assert.Equal(1234m, Assert.Single(NumberParser.Parse("1,234", En)));
        var many = NumberParser.Parse("Netto 1 000,00 zł\nVAT 230,00 zł\nRabat (50,00)\nRazem 1 180,00 zł", Pl);
        Assert.Equal(new[] { 1000m, 230m, -50m, 1180m }, many);
        Assert.Empty(NumberParser.Parse("brak liczb", Pl));
        Assert.Equal(-7.5m, NumberParser.ParseSingle("-7,5", Pl));
        Assert.Null(NumberParser.ParseSingle("7 kotów", Pl));
    }

    [Fact]
    public void Tape_sums_operates_and_keeps_history()
    {
        var t = new CalculatorTape();
        t.Add(TapeOp.Add, 100);
        t.Add(TapeOp.Add, 50.5m);
        t.Add(TapeOp.Subtract, 0.5m);
        t.Add(TapeOp.Multiply, 2);
        Assert.False(t.Add(TapeOp.Divide, 0));
        t.Add(TapeOp.Divide, 4);
        Assert.Equal(75m, t.Total);
        t.RemoveLast();
        Assert.Equal(300m, t.Total);
        t.Clear();
        Assert.Equal(0m, t.Total);
        Assert.Equal(300m, Assert.Single(t.History));
        Assert.Equal("1 234,50", CalculatorTape.Format(1234.5m, Pl));
        Assert.Equal("0,125", CalculatorTape.Format(0.125m, Pl));
    }

    [Fact]
    public void Window_adds_numbers_from_document_selection()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var s = new ServiceCollection();
                s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
                using var sp = s.BuildServiceProvider();
                var loc = sp.GetRequiredService<ILocalizationService>();
                loc.SetCulture(Pl);
                string? inserted = null;
                var w = new CalculatorWindow(loc, () => "Netto: 1 234,56 zł\nRabat: (34,56) zł", text => inserted = text);
                Assert.Equal(2, w.AddSelection());
                Assert.Equal(1200m, w.Tape.Total);
                w.Close();
                Assert.Null(inserted);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public async Task Inserted_result_is_a_typewriter_annotation_visible_in_pdfium()
    {
        var svc = new AnnotationService();
        var text = CalculatorTape.Format(1200m, Pl);
        var (pdf, _) = await svc.AddAsync(SamplePdfFactory.Text(1), new AnnotationSpec
        {
            Kind = AnnotationKind.Typewriter, PageIndex = 0, Rect = new PdfRect(100, 200, 100, 200), Contents = text, Color = PdfColor.Black,
            TextStyle = new TextStyle { FontFamily = "Arial", FontSize = 12, Color = PdfColor.Black },
        });
        var a = Assert.Single(await svc.GetAnnotationsAsync(pdf));
        Assert.Equal(AnnotationKind.Typewriter, a.Kind);
        Assert.Equal(text, a.Contents);
        Assert.True(await RenderCheck.NonWhitePixels(pdf, 0, a.Rect) > 20);
    }
}

using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Measure;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>Pomiary: adnotacje /LineDimension, /PolyLineDimension, /PolygonDimension z /Measure, skala /VP, eksport CSV, render PDFium.</summary>
public class MeasureTests
{
    private static readonly double Cm = 72 / 2.54;

    private static MeasureService Service() => new(new AnnotationService());

    private static T WithCulture<T>(string name, Func<T> body)
    {
        var old = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(name);
        try { return body(); }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private static PdfDictionary Single(PdfDocument doc, int page = 0) =>
        AnnotationStore.All(doc, page).Select(a => a.Dict).Single(d => PdfLow.NameOf(d, "/Subtype") != "/Popup");

    [Fact]
    public async Task Distance_is_a_line_dimension_with_measure_dictionary_and_label()
    {
        var svc = Service();
        var pdf = SamplePdfFactory.Text(1);
        var scale = new MeasureScale(1, "cm", 2, "m");
        var (result, id) = await WithCulture("pl-PL", () => svc.AddAsync(pdf, new MeasureSpec
        {
            Type = MeasureType.Distance, PageIndex = 0, Scale = scale, Author = "Tester",
            Points = new[] { new PdfPoint(100, 300), new PdfPoint(100 + 6.25 * Cm, 300) },
        }));
        Assert.StartsWith("zet-", id.Name);
        PdfSharpEdit.Read(result, doc =>
        {
            var d = Single(doc);
            Assert.Equal("/Line", PdfLow.NameOf(d, "/Subtype"));
            Assert.Equal("/LineDimension", PdfLow.NameOf(d, "/IT"));
            Assert.Equal("12,5 m", PdfLow.Text(d, "/Contents"));
            Assert.Equal("Tester", PdfLow.Text(d, "/T"));
            var m = PdfLow.Dict(d, "/Measure")!;
            Assert.Equal("/Measure", PdfLow.NameOf(m, "/Type"));
            Assert.Equal("/RL", PdfLow.NameOf(m, "/Subtype"));
            Assert.Equal("1 cm = 2 m", PdfLow.Text(m, "/R"));
            var x = (PdfDictionary)PdfLow.Deref(PdfLow.ArrayOf(m, "/X")!.Elements[0])!;
            Assert.Equal("m", PdfLow.Text(x, "/U"));
            Assert.Equal(2 / Cm, PdfLow.Number(x, "/C"), 6);
            Assert.NotNull(PdfLow.ArrayOf(m, "/D"));
            Assert.NotNull(PdfLow.ArrayOf(m, "/A"));
            Assert.NotNull(PdfLow.Dict(PdfLow.Dict(d, "/AP")!, "/N"));
            Assert.Equal(4, PdfLow.Numbers(PdfLow.ArrayOf(d, "/L")).Length);
            return 0;
        });
        // wygląd (linia + etykieta) widoczny w renderze PDFium
        Assert.True(await RenderCheck.NonWhitePixels(result, 0, new PdfRect(95, 290, 100 + 6.25 * Cm + 5, 320)) > 80);
        var list = await new AnnotationService().GetAnnotationsAsync(result);
        Assert.Equal(AnnotationKind.Line, Assert.Single(list).Kind);
    }

    [Fact]
    public async Task Perimeter_and_area_values_follow_the_scale()
    {
        var svc = Service();
        var pdf = SamplePdfFactory.Text(1);
        var sq = new[] { new PdfPoint(100, 100), new PdfPoint(100 + 2 * Cm, 100), new PdfPoint(100 + 2 * Cm, 100 + 3 * Cm), new PdfPoint(100, 100 + 3 * Cm) };
        var (p1, _) = await WithCulture("pl-PL", () => svc.AddAsync(pdf, new MeasureSpec { Type = MeasureType.Area, PageIndex = 0, Points = sq, Scale = MeasureScale.Default }));
        var (p2, _) = await WithCulture("pl-PL", () => svc.AddAsync(p1, new MeasureSpec { Type = MeasureType.Perimeter, PageIndex = 0, Points = sq, Scale = new MeasureScale(1, "cm", 10, "mm") }));
        var items = await WithCulture("pl-PL", () => svc.GetMeasurementsAsync(p2));
        Assert.Equal(2, items.Count);
        var area = items.Single(i => i.Type == MeasureType.Area);
        Assert.Equal(6, area.Value, 4);
        Assert.Equal("cm²", area.Unit);
        Assert.Equal("6 cm²", area.Label);
        var per = items.Single(i => i.Type == MeasureType.Perimeter);
        Assert.Equal(70, per.Value, 3); // 2 + 3 + 2 cm = 70 mm (linia łamana otwarta)
        PdfSharpEdit.Read(p2, doc =>
        {
            var dicts = AnnotationStore.All(doc, 0).Select(a => a.Dict).ToList();
            Assert.Contains(dicts, d => PdfLow.NameOf(d, "/IT") == "/PolygonDimension" && PdfLow.NameOf(d, "/Subtype") == "/Polygon");
            Assert.Contains(dicts, d => PdfLow.NameOf(d, "/IT") == "/PolyLineDimension" && PdfLow.NameOf(d, "/Subtype") == "/PolyLine");
            return 0;
        });
        Assert.True(await RenderCheck.NonWhitePixels(p2, 0, new PdfRect(100, 100, 100 + 2 * Cm, 100 + 3 * Cm)) > 200);
    }

    [Fact]
    public async Task Page_scale_is_stored_in_viewport_and_existing_measurements_are_recalculated()
    {
        var svc = Service();
        var pdf = SamplePdfFactory.Text(2);
        var pts = new[] { new PdfPoint(100, 300), new PdfPoint(100 + 4 * Cm, 300) };
        var (p1, id) = await WithCulture("en-US", () => svc.AddAsync(pdf, new MeasureSpec { Type = MeasureType.Distance, PageIndex = 1, Points = pts }));
        Assert.Equal("4 cm", (await svc.GetMeasurementsAsync(p1)).Single().Label);
        var scale = new MeasureScale(1, "cm", 2.5, "m", 1);
        var p2 = await WithCulture("en-US", () => svc.SetScaleAsync(p1, new[] { 1 }, scale, updateExisting: true));
        Assert.Null(await svc.GetScaleAsync(p2, 0));
        Assert.Equal(scale, await svc.GetScaleAsync(p2, 1));
        PdfSharpEdit.Read(p2, doc =>
        {
            var vp = (PdfArray)PdfLow.Deref(doc.Pages[1].Elements["/VP"])!;
            var view = (PdfDictionary)PdfLow.Deref(vp.Elements[0])!;
            Assert.Equal("/Viewport", PdfLow.NameOf(view, "/Type"));
            Assert.Equal("1 cm = 2.5 m", PdfLow.Text(PdfLow.Dict(view, "/Measure")!, "/R"));
            Assert.Equal("10 m", PdfLow.Text(Single(doc, 1), "/Contents"));
            return 0;
        });
        // nowy pomiar bez jawnej skali bierze skalę strony
        var (p3, _) = await WithCulture("en-US", () => svc.AddAsync(p2, new MeasureSpec { Type = MeasureType.Distance, PageIndex = 1, Points = new[] { new PdfPoint(50, 50), new PdfPoint(50, 50 + 2 * Cm) } }));
        Assert.Contains(await svc.GetMeasurementsAsync(p3), m => m.Label == "5 m");
        // przesunięcie adnotacji nie zmienia wartości
        var annots = new AnnotationService();
        var info = (await annots.GetAnnotationsAsync(p3)).First(a => a.Id == id);
        var moved = await WithCulture("en-US", () => annots.UpdateAsync(p3, id, new AnnotationUpdate { Rect = new PdfRect(info.Rect.Left + 30, info.Rect.Bottom - 40, info.Rect.Right + 30, info.Rect.Top - 40) }));
        Assert.Contains(await svc.GetMeasurementsAsync(moved), m => m.Id == id && m.Label == "10 m");
    }

    [Fact]
    public async Task Csv_export_has_localized_header_and_rows()
    {
        var svc = Service();
        var (pdf, _) = await WithCulture("pl-PL", () => svc.AddAsync(SamplePdfFactory.Text(1), new MeasureSpec
        {
            Type = MeasureType.Distance, PageIndex = 0, Author = "Jan Kowalski", Scale = new MeasureScale(1, "cm", 1.5, "m"),
            Points = new[] { new PdfPoint(10, 10), new PdfPoint(10 + Cm, 10) },
        }));
        var csv = WithCulture("pl-PL", () =>
        {
            var items = svc.GetMeasurementsAsync(pdf).GetAwaiter().GetResult();
            return Encoding.UTF8.GetString(svc.ExportCsv(items)).TrimStart('﻿');
        });
        var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("sep=;", lines[0]);
        Assert.Equal("Strona;Typ;Wartość;Jednostka;Autor;Data;Skala", lines[1]);
        Assert.StartsWith("1;Odległość;\"1,5\";m;Jan Kowalski;", lines[2]);
        Assert.EndsWith(";\"1 cm = 1,5 m\"", lines[2]);
        var rows = Forms.FormDataIO.ParseCsv(csv);
        Assert.Equal("Wartość", rows[0][2]);
        Assert.Equal("1,5", rows[1][2]);
    }

    [Fact]
    public void Scale_math_calibration_and_number_format()
    {
        var s = MeasureScale.Calibrate(5 * Cm, 10, "m");
        Assert.Equal(new MeasureScale(1, "cm", 2, "m"), s);
        Assert.Equal(2 / Cm, s.RealPerPoint, 9);
        var f = MeasureFormat.From(new MeasureScale(1, "cm", 1, "m", 2), new CultureInfo("pl-PL"));
        Assert.Equal("1 234,57", f.FormatNumber(1234.567));
        Assert.Equal("12,5", f.FormatNumber(12.5));
        Assert.Equal("0", f.FormatNumber(-0.001));
        Assert.Equal(12, MeasureGeometry.PolygonArea(new[] { new PdfPoint(0, 0), new PdfPoint(4, 0), new PdfPoint(4, 3), new PdfPoint(0, 3) }));
        Assert.Equal(new PdfPoint(2, 1.5), MeasureGeometry.Centroid(new[] { new PdfPoint(0, 0), new PdfPoint(4, 0), new PdfPoint(4, 3), new PdfPoint(0, 3) }));
    }

    [Fact]
    public void Measure_dictionary_from_another_program_is_understood()
    {
        // słownik jak z Acrobata: /X z przelicznikiem do jednostki, /D i /A z /C 1, bez prywatnego /ZetPDF
        PdfSharpEdit.Read(SamplePdfFactory.Text(1), doc =>
        {
            var m = new PdfDictionary(doc);
            m.Elements["/Type"] = new PdfName("/Measure");
            m.Elements["/Subtype"] = new PdfName("/RL");
            m.Elements["/R"] = new PdfString("1 in = 1 ft");
            PdfArray Fmt(string u, double c, int d)
            {
                var nf = new PdfDictionary(doc);
                nf.Elements["/U"] = new PdfString(u);
                nf.Elements["/C"] = new PdfReal(c);
                nf.Elements["/D"] = new PdfInteger(d);
                var a = new PdfArray(doc);
                a.Elements.Add(nf);
                return a;
            }
            m.Elements["/X"] = Fmt("ft", 1.0 / 72, 100);
            m.Elements["/D"] = Fmt("ft", 1, 100);
            m.Elements["/A"] = Fmt("sq ft", 1, 10);
            var f = MeasureDict.ReadFormat(m, CultureInfo.InvariantCulture)!;
            Assert.Equal(2, f.Value(MeasureType.Distance, new[] { new PdfPoint(0, 0), new PdfPoint(144, 0) }), 9);
            Assert.Equal("sq ft", f.UnitFor(MeasureType.Area));
            Assert.Equal("2 ft", f.Label(MeasureType.Distance, 2));
            var scale = MeasureDict.ReadScale(m)!;
            Assert.Equal("ft", scale.RealUnit);
            Assert.Equal(1.0 / 72, scale.RealPerPoint, 9);
            return 0;
        });
    }
}

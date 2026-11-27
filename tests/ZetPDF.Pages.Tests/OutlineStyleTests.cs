using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Pages.Engine;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Pages.Tests;

/// <summary>Kolor (/C) i styl (/F) zakładek przechodzą przy scalaniu i dzieleniu.</summary>
public class OutlineStyleTests
{
    private readonly PageOperations _ops = new(new PdfiumRenderer());

    private static byte[] StyledBookmarks() => PdfSharpEdit.Modify(SamplePdfFactory.Text(2), doc =>
    {
        var o = doc.Outlines.Add("Czerwona", doc.Pages[0], false, PdfOutlineStyle.BoldItalic);
        var c = new PdfArray(doc);
        foreach (var v in new[] { 0.8, 0.1, 0.1 }) c.Elements.Add(new PdfReal(v));
        o.Elements["/C"] = c;
    });

    private static (double[] Color, int Flags) FirstOutline(byte[] pdf) => PdfSharpEdit.Read(pdf, doc =>
    {
        var root = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Internals.Catalog.Elements["/Outlines"]!).Value;
        var walk = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)root.Elements["/First"]!).Value;
        // przy scalaniu pierwsza jest zakładka pliku – szukamy w dzieciach
        while (walk.Elements.GetString("/Title") != "Czerwona" && walk.Elements["/First"] is PdfSharp.Pdf.Advanced.PdfReference first)
            walk = (PdfDictionary)first.Value;
        var arr = walk.Elements.GetArray("/C")!;
        return (arr.Elements.Select(e => e is PdfReal r ? r.Value : ((PdfInteger)e).Value).ToArray(), walk.Elements.GetInteger("/F"));
    });

    [Fact]
    public async Task Merge_keeps_bookmark_color_and_style()
    {
        var merged = await _ops.MergeAsync(new[] { new MergeSource("plik.pdf", Data: StyledBookmarks()) }, new MergeOptions());
        var (color, flags) = FirstOutline(merged);
        Assert.Equal(new[] { 0.8, 0.1, 0.1 }, color);
        Assert.Equal(3, flags);
    }

    [Fact]
    public async Task Split_keeps_bookmark_color_and_style()
    {
        var parts = await _ops.SplitAsync(StyledBookmarks(), new SplitOptions { Mode = SplitMode.TopLevelBookmarks }, TestPaths.NewDirectory(), "część");
        var (color, flags) = FirstOutline(File.ReadAllBytes(parts[0]));
        Assert.Equal(new[] { 0.8, 0.1, 0.1 }, color);
        Assert.Equal(3, flags);
    }
}

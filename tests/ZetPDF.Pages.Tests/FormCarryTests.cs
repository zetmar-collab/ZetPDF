using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Pages.Engine;
using ZetPDF.Rendering;
using ZetPDF.TestData;
using UglyToad.PdfPig.AcroForms;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Pages.Tests;

/// <summary>Formularz (/AcroForm) zostaje przy wyodrębnianiu, dzieleniu, scalaniu, wstawianiu, zastępowaniu i duplikowaniu stron.</summary>
public class FormCarryTests
{
    private readonly PageOperations _ops = new(new PdfiumRenderer());
    private static readonly string[] FormNames = { "imie", "zgoda", "plec", "miasto" };

    /// <summary>Nazwy pól najwyższego poziomu według PdfPig (niezależny czytnik).</summary>
    private static string[] FieldNames(byte[] pdf)
    {
        using var d = Pig.Open(pdf);
        if (!d.TryGetForm(out AcroForm? form)) return Array.Empty<string>();
        return form.Fields.Select(f => f.Information.PartialName ?? "").OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static int WidgetsOnPage(byte[] pdf, int page) => PdfSharpEdit.Read(pdf, doc =>
        doc.Pages[page].Elements.GetArray("/Annots")?.Elements
            .Count(e => Deref(e) is PdfDictionary d && d.Elements.GetName("/Subtype") == "/Widget") ?? 0);

    private static PdfItem? Deref(PdfItem? i) => i is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : i;

    private static bool NoCarryKeyLeft(byte[] pdf) => !System.Text.Encoding.Latin1.GetString(pdf).Contains("ZetPDFCarriedAcroForm");

    [Fact]
    public async Task Extracted_page_keeps_fillable_form_with_resources()
    {
        var r = await _ops.ExtractAsync(SamplePdfFactory.WithForm(), new[] { 0 });
        Assert.Equal(FormNames.OrderBy(n => n, StringComparer.Ordinal), FieldNames(r));
        PdfSharpEdit.Read(r, doc =>
        {
            var acro = (PdfDictionary)Deref(doc.Internals.Catalog.Elements["/AcroForm"])!;
            Assert.True(acro.Elements.ContainsKey("/DR"));
            Assert.Equal("/Helv 0 Tf 0 g", acro.Elements.GetString("/DA"));
            return 0;
        });
        Assert.True(NoCarryKeyLeft(r));
    }

    [Fact]
    public async Task Page_without_widgets_extracted_from_form_document_gets_no_form()
    {
        var withText = await _ops.InsertFromBytesAsync(SamplePdfFactory.WithForm(), SamplePdfFactory.Text(1), null, 1);
        var r = await _ops.ExtractAsync(withText, new[] { 1 });
        Assert.Empty(FieldNames(r));
        Assert.True(NoCarryKeyLeft(r));
    }

    [Fact]
    public async Task Merging_two_forms_renames_colliding_fields()
    {
        var r = await _ops.MergeAsync(new[] { new MergeSource("", Data: SamplePdfFactory.WithForm()), new MergeSource("", Data: SamplePdfFactory.WithForm()) }, new MergeOptions());
        var names = FieldNames(r);
        Assert.Equal(8, names.Length);
        Assert.Contains("imie", names);
        Assert.Contains("imie_2", names);
        Assert.Contains("plec_2", names);
        Assert.Equal(5, WidgetsOnPage(r, 1)); // 4 pola, radio ma dwa widżety
        Assert.True(NoCarryKeyLeft(r));
    }

    [Fact]
    public async Task Inserting_form_pages_into_form_document_adds_fields()
    {
        var r = await _ops.InsertFromBytesAsync(SamplePdfFactory.WithForm(), SamplePdfFactory.WithForm(), null, 1);
        Assert.Equal(8, FieldNames(r).Length);
    }

    [Fact]
    public async Task Duplicated_form_page_gets_separate_fields()
    {
        var r = await _ops.DuplicateAsync(SamplePdfFactory.WithForm(), new[] { 0 });
        Assert.Equal(2, Pdf.PageCount(r));
        Assert.Equal(8, FieldNames(r).Length);
    }

    [Fact]
    public async Task Replacing_form_page_with_form_page_keeps_one_set_of_fields()
    {
        var path = Pdf.File(SamplePdfFactory.WithForm());
        var r = await _ops.ReplaceAsync(SamplePdfFactory.WithForm(), new[] { 0 }, path, null, new[] { 0 });
        Assert.Equal(FormNames.OrderBy(n => n, StringComparer.Ordinal), FieldNames(r));
    }

    [Fact]
    public async Task Split_parts_keep_their_fields()
    {
        var two = await _ops.InsertFromBytesAsync(SamplePdfFactory.WithForm(), SamplePdfFactory.Text(1), null, 1);
        var dir = ZetPDF.TestData.TestPaths.NewDirectory();
        var parts = await _ops.SplitAsync(two, new SplitOptions { Mode = SplitMode.EveryNPages, PagesPerFile = 1 }, dir, "część");
        Assert.Equal(2, parts.Count);
        Assert.Equal(4, FieldNames(File.ReadAllBytes(parts[0])).Length);
        Assert.Empty(FieldNames(File.ReadAllBytes(parts[1])));
    }

    [Fact]
    public async Task Radio_group_split_across_pages_keeps_only_copied_widgets()
    {
        // Grupa opcji z widżetami na dwóch stronach: druga strona to kopia pierwszej, widżet „m” przeniesiony na nią.
        var pdf = PdfSharpEdit.Modify(SamplePdfFactory.WithForm(), doc =>
        {
            var second = doc.AddPage();
            var annots0 = doc.Pages[0].Elements.GetArray("/Annots")!;
            var mRef = annots0.Elements.OfType<PdfSharp.Pdf.Advanced.PdfReference>()
                .First(r => r.Value is PdfDictionary d && d.Elements["/Parent"] is not null && ((PdfDictionary)d.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!).Elements.ContainsKey("/m"));
            annots0.Elements.Remove(mRef);
            var annots1 = new PdfArray(doc);
            annots1.Elements.Add(mRef);
            second.Elements["/Annots"] = annots1;
            ((PdfDictionary)mRef.Value).Elements["/P"] = second.Reference!;
        });
        var r = await _ops.ExtractAsync(pdf, new[] { 1 });
        Assert.Equal(new[] { "plec" }, FieldNames(r));
        PdfSharpEdit.Read(r, doc =>
        {
            var acro = (PdfDictionary)Deref(doc.Internals.Catalog.Elements["/AcroForm"])!;
            var plec = (PdfDictionary)Deref(acro.Elements.GetArray("/Fields")!.Elements[0])!;
            Assert.Single(plec.Elements.GetArray("/Kids")!.Elements);
            return 0;
        });
    }
}

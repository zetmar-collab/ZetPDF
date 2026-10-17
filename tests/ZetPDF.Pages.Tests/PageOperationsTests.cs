using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Engine;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Pages.Tests;

public class PageOperationsTests
{
    private readonly PageOperations _ops = new(new PdfiumRenderer());

    [Fact]
    public async Task Insert_blank_and_create_blank_document()
    {
        var r = await _ops.InsertBlankAsync(SamplePdfFactory.Text(3), 1, 2, PdfSize.A5);
        Assert.Equal(5, Pdf.PageCount(r));
        Assert.Equal(new[] { 1, -1, -1, 2, 3 }, Pdf.PageNumbers(r));
        var size = Pdf.Read(r, d => (d.Pages[1].Width.Point, d.Pages[1].Height.Point));
        Assert.Equal(PdfSize.A5.Width, size.Item1, 1);
        Assert.Equal(PdfSize.A5.Height, size.Item2, 1);

        var blank = await _ops.CreateBlankDocumentAsync(new PdfSize(842, 595), 3);
        Assert.Equal(3, Pdf.PageCount(blank));
    }

    [Fact]
    public async Task Insert_from_file_with_polish_path_and_page_subset()
    {
        var path = Pdf.File(SamplePdfFactory.Text(4));
        var r = await _ops.InsertFromFileAsync(SamplePdfFactory.Text(2), path, null, new[] { 3, 0 }, 2);
        Assert.Equal(new[] { 1, 2, 4, 1 }, Pdf.PageNumbers(r));
    }

    [Fact]
    public async Task Insert_from_encrypted_file_with_user_password()
    {
        var path = Pdf.File(SamplePdfFactory.EncryptedAes256(pages: 2));
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _ops.InsertFromFileAsync(SamplePdfFactory.Text(1), path, null, null, 1));
        await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => _ops.InsertFromFileAsync(SamplePdfFactory.Text(1), path, "złe", null, 1));
        var r = await _ops.InsertFromFileAsync(SamplePdfFactory.Text(1), path, SamplePdfFactory.UserPassword, null, 1);
        Assert.Equal(new[] { 1, 1, 2 }, Pdf.PageNumbers(r));
        Assert.Contains("gęślą", Pdf.Text(r, 2));
    }

    [Fact]
    public async Task Delete_pages_and_fix_bookmarks()
    {
        var src = SamplePdfFactory.WithBookmarks(3); // 6 stron, rozdziały na 1,3,5
        var r = await _ops.DeleteAsync(src, new[] { 2, 3 });
        Assert.Equal(new[] { 1, 2, 5, 6 }, Pdf.PageNumbers(r));
        // zakładka „Rozdział 2” wskazywała usuniętą stronę 3 → teraz stronę 5 (indeks 2)
        var outlines = Pdf.Read(r, d => Outlines.Read(d));
        Assert.Equal(2, outlines.Single(o => o.Title == "Rozdział 2").PageIndex);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _ops.DeleteAsync(SamplePdfFactory.Text(2), new[] { 0, 1 }));
    }

    [Fact]
    public async Task Delete_page_with_form_fields_removes_fields()
    {
        var form = await _ops.InsertBlankAsync(SamplePdfFactory.WithForm(), 0, 1, PdfSize.A4);
        var r = await _ops.DeleteAsync(form, new[] { 1 });
        Assert.Equal(1, Pdf.PageCount(r));
        int fields = Pdf.Read(r, d => PdfIo.GetArray(PdfIo.GetDict(d.Internals.Catalog, "/AcroForm")!, "/Fields")?.Elements.Count ?? 0);
        Assert.Equal(0, fields);
    }

    [Fact]
    public async Task Extract_to_document_and_to_separate_files()
    {
        var src = SamplePdfFactory.Text(5);
        var one = await _ops.ExtractAsync(src, new[] { 1, 3 });
        Assert.Equal(new[] { 2, 4 }, Pdf.PageNumbers(one));
        var dir = TestPaths.NewDirectory();
        var files = await _ops.ExtractToFilesAsync(src, new[] { 0, 4 }, dir, "{name}_strona_{page}", "raport ż");
        Assert.Equal(2, files.Count);
        Assert.Equal(Path.Combine(dir, "raport ż_strona_1.pdf"), files[0]);
        Assert.Equal(new[] { 5 }, Pdf.PageNumbers(File.ReadAllBytes(files[1])));
    }

    [Fact]
    public async Task Replace_pages_from_file()
    {
        var path = Pdf.File(SamplePdfFactory.Text(3, extraLine: "ZAMIENNIK"));
        var r = await _ops.ReplaceAsync(SamplePdfFactory.Text(4), new[] { 1, 2 }, path, null, new[] { 2, 0 });
        Assert.Equal(new[] { 1, 3, 1, 4 }, Pdf.PageNumbers(r));
        Assert.Contains("ZAMIENNIK", Pdf.Text(r, 1));
        Assert.DoesNotContain("ZAMIENNIK", Pdf.Text(r, 3));
        await Assert.ThrowsAsync<ArgumentException>(() => _ops.ReplaceAsync(SamplePdfFactory.Text(4), new[] { 1 }, path, null, new[] { 0, 1 }));
    }

    [Fact]
    public async Task Duplicate_move_swap_reverse()
    {
        var src = SamplePdfFactory.Text(4);
        Assert.Equal(new[] { 1, 2, 2, 3, 4, 4 }, Pdf.PageNumbers(await _ops.DuplicateAsync(src, new[] { 1, 3 })));
        // przeciąganie miniatur: strony 1 i 2 (0-based 0,1) przed stroną o indeksie 4 (na koniec)
        Assert.Equal(new[] { 3, 4, 1, 2 }, Pdf.PageNumbers(await _ops.MoveAsync(src, new[] { 0, 1 }, 4)));
        Assert.Equal(new[] { 4, 1, 2, 3 }, Pdf.PageNumbers(await _ops.MoveAsync(src, new[] { 3 }, 0)));
        Assert.Equal(new[] { 1, 3, 2, 4 }, Pdf.PageNumbers(await _ops.MoveAsync(src, new[] { 1 }, 3)));
        Assert.Equal(new[] { 4, 2, 3, 1 }, Pdf.PageNumbers(await _ops.SwapAsync(src, 0, 3)));
        Assert.Equal(new[] { 4, 3, 2, 1 }, Pdf.PageNumbers(await _ops.ReverseAsync(src, null)));
        Assert.Equal(new[] { 1, 4, 3, 2 }, Pdf.PageNumbers(await _ops.ReverseAsync(src, new[] { 1, 2, 3 })));
    }

    [Fact]
    public async Task Interleave_two_files_with_reversed_second()
    {
        var second = Pdf.File(SamplePdfFactory.Text(3, extraLine: "DRUGI"));
        var r = await _ops.InterleaveAsync(SamplePdfFactory.Text(2), second, null, reverseSecond: true);
        Assert.Equal(new[] { 1, 3, 2, 2, 1 }, Pdf.PageNumbers(r));
        Assert.Contains("DRUGI", Pdf.Text(r, 1));
        Assert.DoesNotContain("DRUGI", Pdf.Text(r, 0));
    }

    [Fact]
    public async Task Rotate_accumulates_and_normalizes()
    {
        var r = await _ops.RotateAsync(SamplePdfFactory.Text(2), new[] { 0 }, 90);
        r = await _ops.RotateAsync(r, new[] { 0, 1 }, -90);
        var rot = Pdf.Read(r, d => (PdfIo.GetRotation(d.Pages[0]), PdfIo.GetRotation(d.Pages[1])));
        Assert.Equal((0, 270), rot);
        using var doc = await new PdfiumRenderer().OpenAsync(r);
        Assert.Equal(270, doc.Pages[1].Rotation);
    }

    [Fact]
    public async Task Crop_margins_and_white_margins()
    {
        var r = await _ops.CropAsync(SamplePdfFactory.Text(1), new[] { 0 }, new CropOptions { Left = 10, Right = 20, Top = 30, Bottom = 40 });
        var box = Pdf.Read(r, d => PdfIo.VisibleBox(d.Pages[0]));
        Assert.Equal(10, box.Left, 1);
        Assert.Equal(40, box.Bottom, 1);
        Assert.Equal(PdfSize.A4.Width - 20, box.Right, 0);

        var w = await _ops.CropAsync(SamplePdfFactory.Text(1), new[] { 0 }, new CropOptions { RemoveWhiteMargins = true });
        var wb = Pdf.Read(w, d => PdfIo.VisibleBox(d.Pages[0]));
        // tekst zaczyna się w x=56 od lewej, nagłówek ~80 pt od góry
        Assert.InRange(wb.Left, 50, 60);
        Assert.True(wb.Top < PdfSize.A4.Height - 50, $"Top {wb.Top}");
        Assert.True(wb.Width < 500);
        Assert.Contains("Strona 1", Pdf.Text(w, 0));
    }

    [Fact]
    public async Task Crop_on_rotated_page_uses_display_margins()
    {
        var rotated = await _ops.RotateAsync(SamplePdfFactory.Text(1), new[] { 0 }, 90);
        var r = await _ops.CropAsync(rotated, new[] { 0 }, new CropOptions { Left = 100 });
        var box = Pdf.Read(r, d => PdfIo.VisibleBox(d.Pages[0]));
        // po obrocie o 90° lewa krawędź widoku = dolna krawędź strony
        Assert.Equal(100, box.Bottom, 1);
        Assert.Equal(0, box.Left, 1);
    }

    [Fact]
    public async Task Resize_scales_content_into_target_size()
    {
        var r = await _ops.ResizeAsync(SamplePdfFactory.WithAnnotations(), new[] { 0, 1 }, new ResizeOptions { TargetSize = PdfSize.A5 });
        var size = Pdf.Read(r, d => PdfIo.MediaBox(d.Pages[0]));
        Assert.Equal(PdfSize.A5.Width, size.Width, 1);
        Assert.Equal(PdfSize.A5.Height, size.Height, 1);
        Assert.Contains("Strona 1", Pdf.Text(r, 0));
        // adnotacja przeskalowana (A4→A5 ≈ 0,707)
        var rect = Pdf.Read(r, d => PdfIo.GetArray((PdfDictionary)PdfIo.Resolve(PdfIo.GetArray(d.Pages[0], "/Annots")!.Elements[0])!, "/Rect")!.Elements.Select(e => PdfIo.GetNumber(e)!.Value).ToArray());
        Assert.InRange(rect[0], 400 * 0.70, 400 * 0.72);
    }

    [Fact]
    public async Task Flatten_form_fields_and_annotations()
    {
        // wartość pola bez wyglądu → wygląd generowany i wtapiany w treść
        var filled = PdfSharpEditHelpers.SetTextField(SamplePdfFactory.WithForm(), "imie", "Józef Żółć");
        var r = await _ops.FlattenAsync(filled, new FlattenOptions());
        Assert.True(Pdf.Text(r, 0).Contains("Józef Żółć"), Pdf.Text(r, 0) + " | " + Pdf.Deep(r));
        bool hasAcro = Pdf.Read(r, d => d.Internals.Catalog.Elements.ContainsKey("/AcroForm"));
        Assert.False(hasAcro);
        int widgets = Pdf.Read(r, d => PdfIo.GetArray(d.Pages[0], "/Annots")?.Elements.Count ?? 0);
        Assert.Equal(0, widgets);
    }

    [Fact]
    public async Task Page_labels_roundtrip()
    {
        var ranges = new[] { new PageLabelRange(0, PageLabelStyle.LowerRoman), new PageLabelRange(2, PageLabelStyle.Decimal, "A-", 5) };
        var r = await _ops.SetPageLabelsAsync(SamplePdfFactory.Text(4), ranges);
        var back = await _ops.GetPageLabelsAsync(r);
        Assert.Equal(ranges, back);
        Assert.Equal("ii", PageLabels.Label(back, 1));
        Assert.Equal("A-6", PageLabels.Label(back, 3));
        var cleared = await _ops.SetPageLabelsAsync(r, Array.Empty<PageLabelRange>());
        Assert.Empty(await _ops.GetPageLabelsAsync(cleared));
    }

    [Fact]
    public async Task Split_every_n_pages_by_bookmarks_and_by_size()
    {
        var dir = TestPaths.NewDirectory();
        var files = await _ops.SplitAsync(SamplePdfFactory.Text(5), new SplitOptions { PagesPerFile = 2 }, dir, "dok");
        Assert.Equal(3, files.Count);
        Assert.Equal(new[] { 5 }, Pdf.PageNumbers(File.ReadAllBytes(files[2])));
        Assert.EndsWith("dok_3.pdf", files[2]);

        var byBm = await _ops.SplitAsync(SamplePdfFactory.WithBookmarks(3), new SplitOptions { Mode = SplitMode.TopLevelBookmarks, FileNamePattern = "{n} {bookmark}" }, TestPaths.NewDirectory(), "x");
        Assert.Equal(3, byBm.Count);
        Assert.EndsWith("2 Rozdział 2.pdf", byBm[1]);
        Assert.Equal(new[] { 3, 4 }, Pdf.PageNumbers(File.ReadAllBytes(byBm[1])));

        var big = SamplePdfFactory.Text(12);
        var bySize = await _ops.SplitAsync(big, new SplitOptions { Mode = SplitMode.MaxFileSize, MaxFileSizeBytes = big.Length / 3 }, TestPaths.NewDirectory(), "r");
        Assert.True(bySize.Count >= 2);
        Assert.All(bySize, f => Assert.True(new FileInfo(f).Length <= big.Length / 3 || Pdf.PageCount(File.ReadAllBytes(f)) == 1));
        Assert.Equal(12, bySize.Sum(f => Pdf.PageCount(File.ReadAllBytes(f))));
    }

    [Fact]
    public async Task Merge_with_ranges_and_bookmarks_from_file_names()
    {
        var a = Pdf.File(SamplePdfFactory.WithBookmarks(2), "pierwszy ą.pdf");
        var b = Pdf.File(SamplePdfFactory.Text(3), "drugi.pdf");
        var r = await _ops.MergeAsync(new[] { new MergeSource(a), new MergeSource(b, Pages: new[] { 2 }), new MergeSource("", Data: SamplePdfFactory.Text(1)) }, new MergeOptions());
        Assert.Equal(new[] { 1, 2, 3, 4, 3, 1 }, Pdf.PageNumbers(r));
        var outlines = Pdf.Read(r, d => Outlines.Read(d));
        Assert.Equal("pierwszy ą", outlines[0].Title);
        Assert.Equal("Rozdział 1", outlines[0].Children[0].Title);
        Assert.Equal(4, outlines[1].PageIndex);
    }
}

internal static class PdfSharpEditHelpers
{
    public static byte[] SetTextField(byte[] pdf, string name, string value) => ZetPDF.Core.Documents.PdfSharpEdit.Modify(pdf, d =>
    {
        foreach (var f in PdfIo.GetArray(PdfIo.GetDict(d.Internals.Catalog, "/AcroForm")!, "/Fields")!.Elements)
            if (PdfIo.Resolve(f) is PdfDictionary fd && PdfIo.GetText(fd.Elements["/T"]) == name)
                fd.Elements["/V"] = new PdfString(value, PdfStringEncoding.Unicode);
    });
}

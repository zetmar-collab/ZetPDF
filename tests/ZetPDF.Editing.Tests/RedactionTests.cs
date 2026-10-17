using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class RedactionTests
{
    private static RedactionService Service() => new(TestEnv.Renderer);

    [Theory]
    [InlineData(SensitiveDataPattern.Pesel, "44051401359", true)]
    [InlineData(SensitiveDataPattern.Pesel, "44051401358", false)]
    [InlineData(SensitiveDataPattern.Nip, "526-025-09-95", true)]
    [InlineData(SensitiveDataPattern.Nip, "5260250996", false)]
    [InlineData(SensitiveDataPattern.CardNumber, "4111 1111 1111 1111", true)]
    [InlineData(SensitiveDataPattern.CardNumber, "4111 1111 1111 1112", false)]
    [InlineData(SensitiveDataPattern.IbanPl, "PL61 1090 1014 0000 0712 1981 2874", true)]
    [InlineData(SensitiveDataPattern.IbanPl, "PL62 1090 1014 0000 0712 1981 2874", false)]
    [InlineData(SensitiveDataPattern.Email, "jan.kowalski@example.com", true)]
    [InlineData(SensitiveDataPattern.Email, "jan.kowalski@", false)]
    [InlineData(SensitiveDataPattern.PhonePl, "+48 600 700 800", true)]
    [InlineData(SensitiveDataPattern.PhonePl, "12 34", false)]
    public void Validates_patterns_with_checksums(SensitiveDataPattern p, string value, bool expected) =>
        Assert.Equal(expected, Service().Validate(p, value));

    [Fact]
    public void Sample_values_are_valid()
    {
        var s = SamplePdfFactory.WithSensitiveData();
        var svc = Service();
        Assert.True(svc.Validate(SensitiveDataPattern.Pesel, s.Pesel));
        Assert.True(svc.Validate(SensitiveDataPattern.Nip, s.Nip));
        Assert.True(svc.Validate(SensitiveDataPattern.CardNumber, s.Card));
        Assert.True(svc.Validate(SensitiveDataPattern.IbanPl, s.Iban));
        Assert.True(svc.Validate(SensitiveDataPattern.Email, s.Email));
        Assert.True(svc.Validate(SensitiveDataPattern.PhonePl, s.Phone));
    }

    [Fact]
    public async Task Finds_all_patterns_in_sample()
    {
        var s = SamplePdfFactory.WithSensitiveData();
        var found = await Service().FindAsync(s.Pdf!, new RedactionSearch { Patterns = SensitiveDataPattern.All });
        string Of(SensitiveDataPattern p) => string.Join("|", found.Where(c => c.Pattern == p).Select(c => c.Text));
        Assert.Equal(s.Pesel, Of(SensitiveDataPattern.Pesel));
        Assert.Equal(s.Nip, Of(SensitiveDataPattern.Nip));
        Assert.Equal(s.Email, Of(SensitiveDataPattern.Email));
        Assert.Equal(s.Phone, Of(SensitiveDataPattern.PhonePl));
        Assert.Equal(s.Card, Of(SensitiveDataPattern.CardNumber));
        Assert.Equal(s.Iban, Of(SensitiveDataPattern.IbanPl));
        Assert.All(found, c => Assert.NotEmpty(c.Quads));
    }

    [Fact]
    public async Task Apply_removes_pesel_and_email_from_pdfpig_and_pdfium_text()
    {
        var s = SamplePdfFactory.WithSensitiveData();
        var svc = Service();
        var found = await svc.FindAsync(s.Pdf!, new RedactionSearch { Patterns = SensitiveDataPattern.Pesel | SensitiveDataPattern.Email });
        Assert.Equal(2, found.Count);
        var marked = await svc.MarkAsync(s.Pdf!, found.Select(c => new RedactionMark(c.PageIndex, c.Quads)).ToList());
        // Oznaczenie nie usuwa treści (podgląd).
        Assert.Contains(s.Pesel, await TestEnv.PdfiumText(marked));
        Assert.Equal(2, RedactionService.ReadMarks(marked)[0].Count);

        var applied = await svc.ApplyAsync(marked, new RedactionApplyOptions());
        await AssertGone(applied, s.Pesel);
        await AssertGone(applied, s.Email);
        Assert.Empty(RedactionService.ReadMarks(applied));
        // Reszta tekstu zostaje wyszukiwalna.
        Assert.True(await TestEnv.PdfiumSearchCount(applied, s.Nip) == 1);
        Assert.Contains("gęślą", await TestEnv.PdfiumText(applied));
        Assert.Contains("Telefon", TestEnv.PdfPigText(applied));
    }

    [Fact]
    public async Task Apply_removes_phrase_in_text_and_metadata()
    {
        var pdf = PdfSharpEdit.Modify(SamplePdfFactory.Text(2, "Tajny projekt Bursztyn zostaje zamknięty."), d =>
        {
            d.Info.Title = "Raport: projekt Bursztyn";
            d.Info.Subject = "Ogólny";
        });
        var svc = Service();
        var found = await svc.FindAsync(pdf, new RedactionSearch { Phrases = new[] { "Bursztyn" } });
        Assert.Equal(2, found.Count);
        var marked = await svc.MarkAsync(pdf, found.Select(c => new RedactionMark(c.PageIndex, c.Quads)).ToList());
        var applied = await svc.ApplyAsync(marked, new RedactionApplyOptions());
        await AssertGone(applied, "Bursztyn");
        Assert.DoesNotContain("Bursztyn", TestEnv.RawStreams(applied));
        var meta = PdfSharpEdit.Read(applied, d => (d.Info.Title, d.Info.Subject));
        Assert.Equal(string.Empty, meta.Title);
        Assert.Equal("Ogólny", meta.Subject);
        Assert.Contains("projekt", await TestEnv.PdfiumText(applied));
        // Obszar jest czarny w renderze.
        using var doc = await TestEnv.Renderer.OpenAsync(applied);
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public async Task Area_redaction_covers_pixels_black_and_removes_annotations_inside()
    {
        var pdf = SamplePdfFactory.WithAnnotations();
        var svc = Service();
        var text = await TestEnv.PdfiumText(pdf);
        var area = new PdfRect(0, 0, 595, 842);
        var marked = await svc.MarkAsync(pdf, new[] { new RedactionMark(0, new[] { PdfQuad.FromRect(area) }) });
        var applied = await svc.ApplyAsync(marked, new RedactionApplyOptions());
        Assert.Equal(string.Empty, TestEnv.Squash(await TestEnv.PdfiumText(applied).ContinueWith(t => t.Result.Split('\n')[0])));
        var annots = PdfSharpEdit.Read(applied, d => d.Pages[0].Elements["/Annots"] is PdfSharp.Pdf.PdfArray a ? a.Elements.Count : 0);
        Assert.Equal(0, annots);
        using var r = await TestEnv.Renderer.OpenAsync(applied);
        var bmp = await r.RenderPageAsync(0, RenderRequest.ForDpi(20));
        int mid = (bmp.Height / 2) * bmp.Stride + bmp.Width / 2 * 4;
        Assert.True(bmp.Bgra[mid] < 10 && bmp.Bgra[mid + 1] < 10 && bmp.Bgra[mid + 2] < 10);
        Assert.False(string.IsNullOrEmpty(text));
    }

    [Fact]
    public async Task Apply_on_rotated_page_keeps_orientation_and_removes_text()
    {
        var pdf = Samples.RotatedUpright(SamplePdfFactory.Text(1, "Poufne słowo Kryptonim."));
        var svc = Service();
        var found = await svc.FindAsync(pdf, new RedactionSearch { Phrases = new[] { "Kryptonim" } });
        Assert.Single(found);
        var applied = await svc.ApplyAsync(await svc.MarkAsync(pdf, new[] { new RedactionMark(0, found[0].Quads) }), new RedactionApplyOptions());
        await AssertGone(applied, "Kryptonim");
        using var r = await TestEnv.Renderer.OpenAsync(applied);
        Assert.Equal(90, r.Pages[0].Rotation);
        Assert.Equal(1, await TestEnv.PdfiumSearchCount(applied, "Poufne"));
    }

    private static async Task AssertGone(byte[] pdf, string phrase)
    {
        var squashed = TestEnv.Squash(phrase);
        Assert.DoesNotContain(squashed, TestEnv.Squash(TestEnv.PdfPigText(pdf)));
        Assert.DoesNotContain(squashed, TestEnv.Squash(await TestEnv.PdfiumText(pdf)));
        Assert.Equal(0, await TestEnv.PdfiumSearchCount(pdf, phrase));
    }
}

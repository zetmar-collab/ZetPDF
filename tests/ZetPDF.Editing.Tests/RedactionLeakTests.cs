using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Editing.Tests;

/// <summary>Redakcja: frazy złamane między wierszami i kopie tekstu poza treścią strony.</summary>
public class RedactionLeakTests
{
    private static RedactionService Service() => new(TestEnv.Renderer);

    private static byte[] TextLines(params string[] lines)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 14, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            double y = 100;
            foreach (var line in lines)
            {
                g.DrawString(line, font, XBrushes.Black, 72, y);
                y += 22;
            }
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("Jan Kowalski", "Podpisał Jan", "Kowalski z Gdańska")]
    [InlineData("Kowalski", "Podpisał Kowal-", "ski z Gdańska")]
    public async Task Phrase_broken_across_lines_is_found_and_removed(string phrase, string line1, string line2)
    {
        var svc = Service();
        var pdf = TextLines(line1, line2, "Reszta dokumentu");

        var found = await svc.FindAsync(pdf, new RedactionSearch { Phrases = new[] { phrase } });

        var hit = Assert.Single(found);
        Assert.Equal(2, hit.Quads.Count); // po jednym obszarze w każdym wierszu
        var applied = await svc.ApplyAsync(await svc.MarkAsync(pdf, found.Select(c => new RedactionMark(c.PageIndex, c.Quads)).ToList()), new RedactionApplyOptions());
        var text = await TestEnv.PdfiumText(applied);
        Assert.DoesNotContain(line2.Split(' ')[0], text);
        Assert.Contains("Reszta", text);
    }

    [Fact]
    public async Task Whole_word_option_still_rejects_part_of_a_longer_word()
    {
        var pdf = TextLines("Kowalskiego nie ma");
        var found = await Service().FindAsync(pdf, new RedactionSearch { Phrases = new[] { "Kowalski" }, Options = TextSearchOptions.WholeWord });
        Assert.Empty(found);
    }

    [Fact]
    public void Phrase_typed_in_decomposed_form_is_still_found()
    {
        var hits = RedactionService.FindPhrase("Zażółć gęślą jaźń", "ge\u0328s\u0301la\u0328", default).ToList();
        Assert.Equal((7, 5), Assert.Single(hits));
    }

    [Fact]
    public void Invisible_characters_inside_a_word_do_not_hide_it()
    {
        Assert.Single(RedactionService.FindPhrase("Pan Kowal\u00ADski i \u200BPani", "Kowalski", default));
    }

    [Fact]
    public void Crafted_text_does_not_slow_the_search_down()
    {
        // Długie ciągi odstępów i końców wierszy po prawie pasującym początku.
        var text = string.Concat(Enumerable.Repeat("Kowal-" + new string(' ', 2000) + "\r\n" + new string(' ', 2000) + "X", 200));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(RedactionService.FindPhrase(text, "Kowalski", default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Theory]
    [InlineData("Kowal-\r\n\r\nski")]
    [InlineData("Kowal-\u00A0\r\nski")]
    [InlineData("Kowal\r\n\fski")]
    public void Word_broken_by_several_breaks_is_found(string text)
    {
        Assert.Single(RedactionService.FindPhrase(text, "Kowalski", default));
    }

    /// <summary>Plik ze znacznikami, XFA i komentarzem z okienkiem i odpowiedzią – każdy nosi inną frazę.</summary>
    private static byte[] WithHiddenCopies() => PdfSharpEdit.Modify(TextLines("Tajna treść strony"), doc =>
    {
        var page = doc.Pages[0];
        var height = page.Height.Point;
        PdfDictionary Annot(string subtype, double l, double b, double r, double t, string? contents)
        {
            var a = new PdfDictionary(doc);
            a.Elements["/Type"] = new PdfName("/Annot");
            a.Elements["/Subtype"] = new PdfName(subtype);
            a.Elements["/Rect"] = new PdfArray(doc, new PdfReal(l), new PdfReal(b), new PdfReal(r), new PdfReal(t));
            if (contents is not null) a.Elements["/Contents"] = new PdfString(contents);
            doc.Internals.AddObject(a);
            return a;
        }

        // Komentarz na treści; okienko i odpowiedź leżą daleko poza obszarem redakcji.
        var comment = Annot("/Text", 72, height - 110, 200, height - 90, "SEKRET-KOMENTARZ");
        var popup = Annot("/Popup", 400, 100, 500, 200, null);
        popup.Elements["/Parent"] = comment.Reference!;
        comment.Elements["/Popup"] = popup.Reference!;
        var reply = Annot("/Text", 400, 300, 420, 320, "SEKRET-ODPOWIEDZ");
        reply.Elements["/IRT"] = comment.Reference!;
        var annots = new PdfArray(doc);
        foreach (var a in new[] { comment, popup, reply }) annots.Elements.Add(a.Reference!);
        page.Elements["/Annots"] = annots;

        var element = new PdfDictionary(doc);
        element.Elements["/S"] = new PdfName("/P");
        element.Elements["/ActualText"] = new PdfString("SEKRET-TAG");
        doc.Internals.AddObject(element);
        var root = new PdfDictionary(doc);
        root.Elements["/Type"] = new PdfName("/StructTreeRoot");
        root.Elements["/K"] = element.Reference!;
        doc.Internals.AddObject(root);
        doc.Internals.Catalog.Elements["/StructTreeRoot"] = root.Reference!;

        var xfa = new PdfDictionary(doc);
        xfa.CreateStream(Encoding.ASCII.GetBytes("<xfa>SEKRET-XFA</xfa>"));
        doc.Internals.AddObject(xfa);
        var acro = new PdfDictionary(doc);
        acro.Elements["/Fields"] = new PdfArray(doc);
        acro.Elements["/XFA"] = xfa.Reference!;
        doc.Internals.AddObject(acro);
        doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference!;
    });

    [Fact]
    public async Task Apply_leaves_no_copy_of_content_in_tags_xfa_popups_or_replies()
    {
        var svc = Service();
        var pdf = WithHiddenCopies();
        foreach (var marker in new[] { "SEKRET-KOMENTARZ", "SEKRET-ODPOWIEDZ", "SEKRET-TAG", "SEKRET-XFA" })
            Assert.Contains(marker, Encoding.Latin1.GetString(pdf));

        var found = await svc.FindAsync(pdf, new RedactionSearch { Phrases = new[] { "Tajna" } });
        var applied = await svc.ApplyAsync(await svc.MarkAsync(pdf, found.Select(c => new RedactionMark(c.PageIndex, c.Quads)).ToList()), new RedactionApplyOptions());

        var raw = Encoding.Latin1.GetString(applied);
        foreach (var marker in new[] { "SEKRET-KOMENTARZ", "SEKRET-ODPOWIEDZ", "SEKRET-TAG", "SEKRET-XFA" })
            Assert.DoesNotContain(marker, raw);
        Assert.DoesNotContain("/StructTreeRoot", raw);
        Assert.DoesNotContain("/XFA", raw);
        Assert.DoesNotContain("/Popup", raw);
    }
}

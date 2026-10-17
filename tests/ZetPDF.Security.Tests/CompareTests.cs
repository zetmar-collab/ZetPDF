using System.Globalization;
using PdfSharp.Drawing;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.Security.Compare;
using CompareOptions = ZetPDF.Core.Contracts.CompareOptions;
using ZetPDF.TestData;

namespace ZetPDF.Security.Tests;

public class CompareTests
{
    internal static byte[] Pdf(params string[][] pages) => Pdf(null, pages);

    internal static byte[] Pdf(Action<XGraphics, int>? extra, params string[][] pages)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var font = new XFont("Arial", 12);
        for (int p = 0; p < pages.Length; p++)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            double y = 60;
            foreach (var line in pages[p]) { g.DrawString(line, font, XBrushes.Black, 50, y); y += 20; }
            extra?.Invoke(g, p);
        }
        var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public async Task Detects_changed_word_inserted_and_deleted_text()
    {
        var a = Pdf(new[] { "Ala ma kota i psa.", "Zażółć gęślą jaźń." }, new[] { "Druga strona bez zmian.", "To zdanie zostanie usunięte." });
        var b = Pdf(new[] { "Ala ma żółwia i psa.", "Zażółć gęślą jaźń." }, new[] { "Druga strona bez zmian.", "Nowe słowa dodane tutaj." , "Koniec." });
        var r = await new CompareService().CompareAsync(a, "a.pdf", b, "b.pdf", new CompareOptions());

        Assert.Equal(2, r.PageCountA);
        var changed = Assert.Single(r.Changes, c => c.TextA == "kota");
        Assert.Equal(CompareChangeKind.Changed, changed.Kind);
        Assert.Equal("żółwia", changed.TextB);
        Assert.Equal(0, changed.PageA);
        Assert.Equal(0, changed.PageB);
        Assert.NotEmpty(changed.RectsA);
        Assert.NotEmpty(changed.RectsB);
        Assert.Contains(r.Changes, c => c.PageA == 1 && c.TextA.Contains("usunięte", StringComparison.Ordinal));
        Assert.Contains(r.Changes, c => c.TextB.Contains("Koniec.", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(0, r.Changes.Count), r.Changes.Select(c => c.Index));
        Assert.Contains((0, 0), r.PageMap);
        Assert.Contains((1, 1), r.PageMap);

        // raporty TXT i HTML w folderze z polskimi znakami
        var dir = TestPaths.NewDirectory();
        var svc = new CompareService();
        await svc.ExportReportAsync(r, Path.Combine(dir, "raport porównania.txt"), CompareReportFormat.Text);
        await svc.ExportReportAsync(r, Path.Combine(dir, "raport porównania.html"), CompareReportFormat.Html);
        var txt = await File.ReadAllTextAsync(Path.Combine(dir, "raport porównania.txt"));
        Assert.Contains("kota", txt);
        Assert.Contains("żółwia", txt);
        var html = await File.ReadAllTextAsync(Path.Combine(dir, "raport porównania.html"));
        Assert.Contains("<del>kota</del>", html);
        Assert.Contains("<ins>żółwia</ins>", html);
    }

    [Fact]
    public async Task Identical_documents_have_no_changes_and_ignore_case_works()
    {
        var a = Pdf(new[] { "Ten sam Tekst." });
        Assert.Empty((await new CompareService().CompareAsync(a, "a", a, "b", new CompareOptions())).Changes);
        var b = Pdf(new[] { "ten sam tekst." });
        Assert.NotEmpty((await new CompareService().CompareAsync(a, "a", b, "b", new CompareOptions())).Changes);
        Assert.Empty((await new CompareService().CompareAsync(a, "a", b, "b", new CompareOptions { IgnoreCase = true })).Changes);
    }

    [Fact]
    public async Task Graphic_difference_is_found_by_render_comparison()
    {
        var a = Pdf(new[] { "Tekst." });
        var b = Pdf((g, p) => g.DrawRectangle(XBrushes.Black, 300, 400, 120, 80), new[] { "Tekst." });
        var svc = new CompareService { Renderer = new PdfiumRenderer() };
        var r = await svc.CompareAsync(a, "a", b, "b", new CompareOptions { CompareGraphics = true });
        var g = Assert.Single(r.Changes);
        Assert.Equal(string.Empty, g.TextA);
        var rect = g.RectsB[0];
        // prostokąt XGraphics (300,400)-(420,480) od góry strony A4 → w PDF Y od dołu
        Assert.InRange(rect.Left, 280, 310);
        Assert.InRange(rect.Right, 410, 440);
        Assert.InRange(rect.Top, 841.89 - 410, 841.89 - 380);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Myers_edit_script_is_minimal_and_reproduces_target(int seed)
    {
        var rnd = new Random(seed);
        for (int iter = 0; iter < 200; iter++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => rnd.Next(0, 4)).ToArray();
            var b = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => rnd.Next(0, 4)).ToArray();
            var edits = MyersDiff.Diff(a, b);
            // odtworzenie A i B
            Assert.Equal(a, edits.Where(e => e.Op != DiffOp.Insert).Select(e => a[e.IndexA]).ToArray());
            Assert.Equal(b, edits.Where(e => e.Op != DiffOp.Delete).Select(e => b[e.IndexB]).ToArray());
            Assert.All(edits.Where(e => e.Op == DiffOp.Equal), e => Assert.Equal(a[e.IndexA], b[e.IndexB]));
            Assert.Equal(Lcs(a, b), edits.Count(e => e.Op == DiffOp.Equal));
        }
    }

    private static int Lcs(int[] a, int[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Length, b.Length];
    }

    [Fact]
    public void Report_text_contains_kinds_in_polish()
    {
        var r = new CompareResult("a", "b", 1, 1, new[] { (0, 0) }, new[]
        {
            new CompareChange(0, CompareChangeKind.Inserted, 0, 0, "", "nowe", Array.Empty<PdfRect>(), Array.Empty<PdfRect>()),
        });
        var txt = CompareService.BuildText(r, new CultureInfo("pl"));
        Assert.Contains(SecurityModule.Strings.GetString("Security.Compare.Kind.Inserted", new CultureInfo("pl"))!, txt);
    }
}

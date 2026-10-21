using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Indexing;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class FullTextIndexTests
{
    private static byte[] Pdf(params string[] pageTexts)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var font = new XFont("Arial", 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        foreach (var t in pageTexts)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString(t, font, XBrushes.Black, new XPoint(56, 100));
        }
        return PdfSharpEdit.Save(doc);
    }

    private static (string Folder, FullTextIndex Index) Setup()
    {
        var root = TestPaths.NewDirectory();
        var folder = Path.Combine(root, "Próbny folder – zażółć");
        Directory.CreateDirectory(Path.Combine(folder, "podfolder"));
        File.WriteAllBytes(Path.Combine(folder, "raport roczny.pdf"), Pdf("Raport roczny", "Budżet na rok"));
        File.WriteAllBytes(Path.Combine(folder, "pangram ćma.pdf"), Pdf("Pierwsza strona", "Zażółć gęślą jaźń – drugi wiersz", "Koniec dokumentu"));
        File.WriteAllBytes(Path.Combine(folder, "umowa.pdf"), Pdf("Umowa najmu lokalu", "Strona druga umowy"));
        File.WriteAllBytes(Path.Combine(folder, "tajne.pdf"), SamplePdfFactory.EncryptedAes256());
        File.WriteAllBytes(Path.Combine(folder, "podfolder", "głęboko.pdf"), Pdf("Gęślą w podfolderze"));
        File.WriteAllBytes(Path.Combine(folder, "nie pdf.pdf"), SamplePdfFactory.NotAPdf());
        return (folder, new FullTextIndex(Path.Combine(root, "index")));
    }

    [Fact]
    public async Task Index_of_polish_folder_finds_word_with_diacritics_in_right_file_and_page()
    {
        var (folder, index) = Setup();
        var progress = new List<double?>();
        var result = await index.BuildAsync(folder, recursive: false, new SyncProgress(p => progress.Add(p.Fraction)));
        Assert.Equal(5, result.Index.Files.Count);
        Assert.Equal(new[] { "tajne.pdf" }, result.Encrypted);
        Assert.Equal(new[] { "nie pdf.pdf" }, result.Failed);
        Assert.Equal(1.0, progress[^1]);
        Assert.StartsWith(Path.Combine(Path.GetDirectoryName(index.IndexDirectory)!, "index"), index.IndexFileFor(folder));
        Assert.True(File.Exists(index.IndexFileFor(folder)));

        var loaded = index.Load(folder)!;
        var hit = Assert.Single(FullTextIndex.Search(loaded, "gęślą", new TextSearchOptionsEx()));
        Assert.Equal("pangram ćma.pdf", hit.DisplayName);
        Assert.Equal(Path.Combine(folder, "pangram ćma.pdf"), hit.Source);
        Assert.Equal(1, hit.PageIndex);
        Assert.Equal("gęślą", hit.Match);
        Assert.Contains("Zażółć gęślą jaźń", hit.Context);

        // bez rozróżniania wielkości liter; bez diakrytyków tylko na żądanie
        Assert.Single(FullTextIndex.Search(loaded, "GĘŚLĄ JAŹŃ", new TextSearchOptionsEx()));
        Assert.Empty(FullTextIndex.Search(loaded, "gesla", new TextSearchOptionsEx()));
        Assert.Single(FullTextIndex.Search(loaded, "gesla jazn", new TextSearchOptionsEx { IgnoreDiacritics = true }));
        Assert.Single(FullTextIndex.Search(loaded, "zazolc", new TextSearchOptionsEx { IgnoreDiacritics = true }));
        Assert.Empty(FullTextIndex.Search(loaded, "GĘŚLĄ", new TextSearchOptionsEx { MatchCase = true }));
        Assert.Empty(FullTextIndex.Search(loaded, "gęśl", new TextSearchOptionsEx { WholeWords = true }));
        Assert.Single(FullTextIndex.Search(loaded, "gęśl", new TextSearchOptionsEx()));
        Assert.Contains(index.ListIndexes(), i => i.Root == FullTextIndex.NormalizeFolder(folder) && !i.Recursive);

        // rekursywnie: także podfolder
        var rec = await index.BuildAsync(folder, recursive: true);
        Assert.Equal(6, rec.Index.Files.Count);
        Assert.Equal(2, FullTextIndex.Search(rec.Index, "gęślą", new TextSearchOptionsEx()).Count);
    }

    [Fact]
    public async Task Incremental_update_reuses_unchanged_files_and_reindexes_changed_and_removed()
    {
        var (folder, index) = Setup();
        await index.BuildAsync(folder, false);
        var again = await index.BuildAsync(folder, false);
        Assert.Equal(0, again.Indexed);
        Assert.Equal(5, again.Reused);

        var umowa = Path.Combine(folder, "umowa.pdf");
        File.WriteAllBytes(umowa, Pdf("Aneks do umowy – nowa treść"));
        File.SetLastWriteTimeUtc(umowa, DateTime.UtcNow.AddMinutes(5));
        File.Delete(Path.Combine(folder, "raport roczny.pdf"));
        var third = await index.BuildAsync(folder, false);
        Assert.Equal(1, third.Indexed);
        Assert.Equal(3, third.Reused);
        Assert.Equal(1, third.Removed);
        Assert.Single(FullTextIndex.Search(third.Index, "aneks", new TextSearchOptionsEx()));
        Assert.Empty(FullTextIndex.Search(third.Index, "najmu", new TextSearchOptionsEx()));
    }

    [Fact]
    public async Task Cancelled_build_keeps_partial_index_and_corrupt_index_file_is_ignored()
    {
        var (folder, index) = Setup();
        using var cts = new CancellationTokenSource();
        int n = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.BuildAsync(folder, false, new SyncProgress(_ => { if (++n == 3) cts.Cancel(); }), cts.Token));
        var partial = index.Load(folder);
        Assert.NotNull(partial);
        Assert.InRange(partial!.Files.Count, 1, 4);
        File.WriteAllBytes(index.IndexFileFor(folder), new byte[] { 1, 2, 3 });
        Assert.Null(index.Load(folder));
        var rebuilt = await index.BuildAsync(folder, false);
        Assert.Equal(5, rebuilt.Indexed);
    }

    [Fact]
    public void Csv_export_quotes_fields_and_neutralizes_formulas()
    {
        Assert.Equal("\"'=SUM(A1:A2)\"", FullTextIndex.CsvField("=SUM(A1:A2)"));
        Assert.Equal("\"'+48 123\"", FullTextIndex.CsvField("+48 123"));
        Assert.Equal("\"'-1\"", FullTextIndex.CsvField("-1"));
        Assert.Equal("\"'@cmd\"", FullTextIndex.CsvField("@cmd"));
        Assert.Equal("\"'\tx\"", FullTextIndex.CsvField("\tx"));
        Assert.Equal("\"'\rx\"", FullTextIndex.CsvField("\rx"));
        Assert.Equal("\"a \"\"b\"\" c\"", FullTextIndex.CsvField("a \"b\" c"));
        Assert.Equal("\"zażółć\"", FullTextIndex.CsvField("zażółć"));
        var hits = new[] { new FullTextHit(@"C:\x\=evil.pdf", "=evil.pdf", 0, 0, 3, 0, "", "=1+1", " tekst") };
        var csv = FullTextIndex.ToCsv(hits, new[] { "Plik", "Ścieżka", "Strona", "Kontekst" }, ";");
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("\"Plik\";\"Ścieżka\";\"Strona\";\"Kontekst\"", lines[0]);
        Assert.Equal("\"'=evil.pdf\";\"C:\\x\\=evil.pdf\";\"1\";\"'=1+1 tekst\"", lines[1]);
    }

    [Fact]
    public void Text_folding_keeps_length_and_maps_polish_letters()
    {
        Assert.Equal("zazolc gesla jazn", TextFolding.Fold("Zażółć gęślą jaźń", true, true));
        Assert.Equal("zażółć", TextFolding.Fold("ZAŻÓŁĆ", true, false));
        Assert.Equal("Lodz", TextFolding.Fold("Łódź", false, true));
        Assert.Equal(new[] { 4, 20 }, FullTextIndex.FindAll("Ala gęślą ma kota.\r\nGĘŚLĄ", "gęślą", new TextSearchOptionsEx()));
    }

    [Fact]
    public async Task Search_in_open_documents_and_hit_rectangles_from_pdfium_text()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Core.Services.CoreServiceCollectionExtensions.AddZetPdfCore(services, new Core.Services.AppPaths(TestPaths.NewDirectory()));
        new Rendering.RenderingModule().ConfigureServices(services);
        new EditingModule().ConfigureServices(services);
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var docs = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Core.Contracts.IDocumentManager>(sp);
        var a = await docs.CreateNewAsync(Pdf("Pierwszy gęślą", "Drugi GĘŚLĄ i gęślą"), "a.pdf");
        var b = await docs.CreateNewAsync(Pdf("Nic tu nie ma", "Ostatnia gęślą"), "b.pdf");
        docs.ActiveDocument = a;
        var index = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<FullTextIndex>(sp);
        Assert.EndsWith("index", index.IndexDirectory);
        var controller = new Ui.Indexing.IndexController(new Ui.EditingContext(sp), index);
        var (all, skipped) = await controller.SearchAsync(Ui.Indexing.SearchScope.OpenDocuments, null, "gęślą", new TextSearchOptionsEx(), CancellationToken.None);
        Assert.Equal(0, skipped);
        Assert.Equal(4, all.Count);
        Assert.Equal(new[] { "a.pdf", "a.pdf", "a.pdf", "b.pdf" }, all.Select(h => h.DisplayName));
        var second = all[2];
        Assert.Equal(1, second.PageIndex);
        Assert.Equal(1, second.OccurrenceOnPage);
        var (current, _) = await controller.SearchAsync(Ui.Indexing.SearchScope.CurrentDocument, null, "gęślą", new TextSearchOptionsEx(), CancellationToken.None);
        Assert.Equal(3, current.Count);

        using var doc = await TestEnv.Renderer.OpenAsync(a.Current.GetBytes());
        var text = await doc.GetPageTextAsync(1);
        var first = Ui.Indexing.IndexController.HitRects(text, "gęślą", new TextSearchOptionsEx(), 0);
        var other = Ui.Indexing.IndexController.HitRects(text, "gęślą", new TextSearchOptionsEx(), 1);
        var r1 = Assert.Single(first);
        var r2 = Assert.Single(other);
        Assert.True(r2.Left > r1.Right, $"{r1} {r2}");
        Assert.InRange(r1.Width, 15, 60);
    }

    private sealed class SyncProgress : IProgress<Core.Contracts.OperationProgress>
    {
        private readonly Action<Core.Contracts.OperationProgress> _a;
        public SyncProgress(Action<Core.Contracts.OperationProgress> a) => _a = a;
        public void Report(Core.Contracts.OperationProgress value) => _a(value);
    }
}

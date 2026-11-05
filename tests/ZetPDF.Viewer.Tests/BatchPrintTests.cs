using System.IO;
using System.Windows;
using System.Windows.Xps.Packaging;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.TestData;
using ZetPDF.Viewer.Printing;

namespace ZetPDF.Viewer.Tests;

/// <summary>Kolejność broszury, układ arkusza, druk do XPS, druk wsadowy. Fizycznej drukarki w testach nie ma.</summary>
[Collection(PdfiumCollection.Name)]
public class BatchPrintTests
{
    private const int B = BookletImposition.Blank;

    public static IEnumerable<object[]> BookletCases() => new[]
    {
        new object[] { 1, new (int, int)[] { (B, 1), (B, B) } },
        new object[] { 2, new (int, int)[] { (B, 1), (2, B) } },
        new object[] { 3, new (int, int)[] { (B, 1), (2, 3) } },
        new object[] { 4, new (int, int)[] { (4, 1), (2, 3) } },
        new object[] { 5, new (int, int)[] { (B, 1), (2, B), (B, 3), (4, 5) } },
        new object[] { 6, new (int, int)[] { (B, 1), (2, B), (6, 3), (4, 5) } },
        new object[] { 7, new (int, int)[] { (B, 1), (2, 7), (6, 3), (4, 5) } },
        new object[] { 8, new (int, int)[] { (8, 1), (2, 7), (6, 3), (4, 5) } },
        new object[] { 9, new (int, int)[] { (B, 1), (2, B), (B, 3), (4, 9), (8, 5), (6, 7) } },
    };

    [Theory]
    [MemberData(nameof(BookletCases))]
    public void Booklet_order_for_1_to_9_pages(int pages, (int, int)[] expected) =>
        Assert.Equal(expected, BookletImposition.Sides(pages));

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 12)]
    [InlineData(0, 0)]
    public void Booklet_pads_to_multiple_of_four(int pages, int padded) => Assert.Equal(padded, BookletImposition.PaddedCount(pages));

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(101)]
    public void Every_page_appears_exactly_once(int pages)
    {
        var all = BookletImposition.Sides(pages).SelectMany(s => new[] { s.Left, s.Right }).Where(p => p != B).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, pages), all);
    }

    [Fact]
    public void Booklet_maps_selected_pages_and_lays_out_left_and_right()
    {
        var map = BookletImposition.Map(new[] { 10, 20, 30, 40 });
        Assert.Equal(new[] { 40, 10 }, map[0]);
        Assert.Equal(new[] { 20, 30 }, map[1]);

        var options = new PrintOptions(null, "", Booklet: true);
        var (paper, printable) = PrintLayout.ForBooklet(new Size(800, 1100), new Rect(20, 30, 760, 1040));
        Assert.Equal(new Size(1100, 800), paper);
        var places = PrintLayout.LayoutSheet(new[] { B, 7 }, _ => new Size(595, 842), printable, options);
        var only = Assert.Single(places);
        Assert.Equal(7, only.PageIndex);
        Assert.True(only.Destination.Left >= printable.Left + printable.Width / 2 - 1, "strona z prawej");
        Assert.False(only.Rotate90);
        var both = PrintLayout.LayoutSheet(new[] { 8, 1 }, _ => new Size(595, 842), printable, options);
        Assert.True(both[0].Destination.Right <= both[1].Destination.Left);
    }

    [Fact]
    public void Booklet_prints_to_xps_on_landscape_sheets() => Sta.Run(async () =>
    {
        using var doc = await new PdfiumRenderer().OpenAsync(SamplePdfFactory.Text(6));
        var dir = TestPaths.NewDirectory();
        var path = Path.Combine(dir, "broszura zażółć.xps");
        await PrintService.PrintToXpsFileAsync(doc, new PrintOptions(null, "", Booklet: true), path);
        using var xps = new XpsDocument(path, FileAccess.Read);
        var paginator = xps.GetFixedDocumentSequence().DocumentPaginator;
        Assert.Equal(4, paginator.PageCount); // 6 stron dopełnione do 8, czyli 2 arkusze i 4 strony arkusza
        var size = paginator.GetPage(0).Size;
        Assert.True(size.Width > size.Height, size.ToString());
        Assert.True(paginator.GetPage(3).Size.Width > 0);
    });

    private sealed class FakeTarget
    {
        public List<string> Printed { get; } = new();
        public List<int> PageCounts { get; } = new();
        public Exception? ThrowFor { get; init; }
        public string? ThrowOn { get; init; }
        public Action? BeforeEach { get; init; }

        public Task Print(IRenderDocument doc, PrintOptions options, string path, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BeforeEach?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (ThrowOn is not null && path.EndsWith(ThrowOn, StringComparison.Ordinal)) throw ThrowFor!;
            Printed.Add(Path.GetFileName(path));
            PageCounts.Add(doc.PageCount);
            progress?.Report(OperationProgress.Of(1, 1));
            return Task.CompletedTask;
        }
    }

    private static byte[] NoPrint() => PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
    {
        doc.SecurityHandler.SetEncryptionToV5();
        doc.SecuritySettings.UserPassword = "";
        doc.SecuritySettings.OwnerPassword = "wlasciciel";
        doc.SecuritySettings.PermitPrint = false;
    });

    private static (string Dir, List<string> Files) MakeFiles()
    {
        var dir = Path.Combine(TestPaths.NewDirectory(), "Próbny folder");
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        void Write(string name, byte[] data)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            files.Add(path);
        }
        Write("zwykły plik.pdf", SamplePdfFactory.Text(2));
        Write("zaszyfrowany.pdf", SamplePdfFactory.EncryptedAes256(pages: 3));
        Write("uszkodzony.pdf", SamplePdfFactory.Corrupted());
        Write("bez druku.pdf", NoPrint());
        Write("drugi.pdf", SamplePdfFactory.Text(1));
        return (dir, files);
    }

    [Fact]
    public async Task Batch_prints_in_order_and_reports_per_file_with_correct_password()
    {
        var (_, files) = MakeFiles();
        var service = new BatchPrintService(new PdfiumRenderer());
        var entries = await service.ProbeAsync(files);
        var asked = new List<(string Name, bool Retry)>();
        await service.ResolvePasswordsAsync(entries, (name, retry) =>
        {
            asked.Add((name, retry));
            return retry ? SamplePdfFactory.UserPassword : "złe hasło";
        });
        Assert.Equal(new[] { ("zaszyfrowany.pdf", false), ("zaszyfrowany.pdf", true) }, asked);

        var target = new FakeTarget();
        var progress = new List<OperationProgress>();
        await service.PrintAsync(entries, new PrintOptions(null, ""), target.Print, new Sync(progress.Add));
        Assert.Equal(new[] { "zwykły plik.pdf", "zaszyfrowany.pdf", "drugi.pdf" }, target.Printed);
        Assert.Equal(new[] { 2, 3, 1 }, target.PageCounts);
        Assert.Equal(new[] { BatchPrintStatus.Printed, BatchPrintStatus.Printed, BatchPrintStatus.SkippedCorrupt, BatchPrintStatus.SkippedNoPrintPermission, BatchPrintStatus.Printed }, entries.Select(e => e.Status));
        Assert.NotEmpty(progress);
        Assert.All(entries, e => Assert.Null(e.Password));
    }

    [Fact]
    public async Task Skipped_password_and_failed_file_do_not_stop_the_rest()
    {
        var (_, files) = MakeFiles();
        var service = new BatchPrintService(new PdfiumRenderer());
        var entries = await service.ProbeAsync(files.Append(Path.Combine(Path.GetDirectoryName(files[0])!, "nie ma.pdf")).ToList());
        await service.ResolvePasswordsAsync(entries, (_, _) => null);
        var target = new FakeTarget { ThrowOn = "zwykły plik.pdf", ThrowFor = new InvalidOperationException("drukarka niedostępna") };
        await service.PrintAsync(entries, new PrintOptions(null, ""), target.Print);
        Assert.Equal(new[] { "drugi.pdf" }, target.Printed);
        Assert.Equal(BatchPrintStatus.Failed, entries[0].Status);
        Assert.Contains("drukarka niedostępna", entries[0].Detail);
        Assert.Equal(BatchPrintStatus.SkippedEncrypted, entries[1].Status);
        Assert.Equal(BatchPrintStatus.SkippedUnreadable, entries[5].Status);
    }

    [Fact]
    public async Task Cancel_between_files_marks_the_rest_cancelled()
    {
        var dir = TestPaths.NewDirectory();
        var files = Enumerable.Range(0, 4).Select(i =>
        {
            var p = Path.Combine(dir, $"p{i}.pdf");
            File.WriteAllBytes(p, SamplePdfFactory.Text(1));
            return p;
        }).ToList();
        var service = new BatchPrintService(new PdfiumRenderer());
        var entries = await service.ProbeAsync(files);
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var target = new FakeTarget { BeforeEach = () => { if (++calls == 2) cts.Cancel(); } };
        await service.PrintAsync(entries, new PrintOptions(null, ""), target.Print, null, cts.Token);
        Assert.Equal(new[] { BatchPrintStatus.Printed, BatchPrintStatus.Cancelled, BatchPrintStatus.Cancelled, BatchPrintStatus.Cancelled }, entries.Select(e => e.Status));
    }

    [Fact]
    public void Batch_prints_really_to_xps_files() => Sta.Run(async () =>
    {
        var (dir, files) = MakeFiles();
        var service = new BatchPrintService(new PdfiumRenderer());
        var entries = await service.ProbeAsync(files);
        await service.ResolvePasswordsAsync(entries, (_, _) => SamplePdfFactory.UserPassword);
        var output = new List<string>();
        await service.PrintAsync(entries, new PrintOptions(null, "", PagesPerSheet: 2), (doc, options, path, progress, ct) =>
        {
            var target = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".xps");
            output.Add(target);
            return PrintService.PrintToXpsFileAsync(doc, options, target, null, progress, ct);
        });
        Assert.Equal(3, output.Count);
        foreach (var (file, sheets) in output.Zip(new[] { 1, 2, 1 }))
        {
            using var xps = new XpsDocument(file, FileAccess.Read);
            Assert.Equal(sheets, xps.GetFixedDocumentSequence().DocumentPaginator.PageCount);
        }
    });

    [Fact]
    public void Windows_can_be_created_and_list_is_ordered() => Sta.Run(() =>
    {
        var loc = new ZetPDF.Core.Localization.LocalizationService(initial: new System.Globalization.CultureInfo("pl"));
        loc.RegisterResources(ViewerModule.Strings);
        var window = new BatchPrintWindow(loc, null);
        window.AddPaths(new[] { @"C:\a.pdf", @"C:\b.pdf", @"C:\A.PDF" });
        Assert.Equal(2, window.Files.Count);
        window.Close();
        var entry = new BatchPrintEntry(@"C:\ścieżka\x.pdf");
        Assert.Contains("x.pdf", BatchPrintReportWindow.Describe(loc, entry));
        new BatchPrintReportWindow(loc, new[] { entry }).Close();
        return Task.CompletedTask;
    });

    private sealed class Sync(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}

using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Indexing;
using ZetPDF.Editing.Ui;
using ZetPDF.Editing.Ui.Indexing;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

/// <summary>Ochrona DPAPI plików indeksu, stary format i okno zarządzania indeksami.</summary>
public class IndexManagementTests
{
    private const string Secret = "Tajemnicaksiegowa";

    private static byte[] Pdf(string text)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        var font = new XFont("Arial", 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.WinAnsi));
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page)) g.DrawString(text, font, XBrushes.Black, new XPoint(56, 100));
        return PdfSharpEdit.Save(doc);
    }

    private static (string Folder, FullTextIndex Index) Setup(string name = "Próbny folder – zażółć")
    {
        var root = TestPaths.NewDirectory();
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "umowa.pdf"), Pdf(Secret));
        return (folder, new FullTextIndex(Path.Combine(root, "index")));
    }

    /// <summary>Plik starego formatu (sam GZip) z jednym plikiem i podanym tekstem strony.</summary>
    private static void WriteLegacy(string path, string root, string pageText)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var w = new BinaryWriter(gz, Encoding.UTF8);
        w.Write("ZIDX"); w.Write(1); w.Write(root); w.Write(false); w.Write(DateTime.UtcNow.Ticks); w.Write(1);
        w.Write("stary.pdf"); w.Write(10L); w.Write(20L); w.Write((byte)IndexedFileStatus.Ok); w.Write(string.Empty); w.Write(1); w.Write(pageText);
    }

    private static string Decompressed(string path)
    {
        var raw = File.ReadAllBytes(path);
        Assert.False(raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B, "plik jest jawnym GZip");
        return Encoding.Latin1.GetString(raw);
    }

    [Fact]
    public async Task Index_file_is_protected_and_text_is_not_readable_on_disk()
    {
        var (folder, index) = Setup();
        await index.BuildAsync(folder, recursive: false);
        var file = index.IndexFileFor(folder);
        var content = Decompressed(file);
        Assert.StartsWith("ZIDXDP2", content, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, content);
        Assert.DoesNotContain("umowa", content);
        Assert.DoesNotContain("Próbny", content);
        Assert.Empty(Directory.GetFiles(index.IndexDirectory, "*.tmp"));

        var loaded = index.Load(folder)!;
        Assert.Single(FullTextIndex.Search(loaded, Secret, new TextSearchOptionsEx()));
        Assert.Equal(folder, loaded.Root);
    }

    [Fact]
    public async Task Update_is_incremental_on_protected_index()
    {
        var (folder, index) = Setup();
        await index.BuildAsync(folder, recursive: false);
        File.WriteAllBytes(Path.Combine(folder, "nowy.pdf"), Pdf("Drugi"));
        var result = await index.BuildAsync(folder, recursive: false);
        Assert.Equal(1, result.Reused);
        Assert.Equal(1, result.Indexed);
    }

    [Fact]
    public async Task Legacy_index_is_read_and_rewritten_protected()
    {
        var (folder, index) = Setup();
        var file = index.IndexFileFor(folder);
        WriteLegacy(file, folder, "Tekst starego indeksu");
        Assert.Equal(0x1F, File.ReadAllBytes(file)[0]);

        var loaded = index.Load(folder)!;
        Assert.Single(FullTextIndex.Search(loaded, "starego", new TextSearchOptionsEx()));
        Assert.DoesNotContain("starego", Decompressed(file));
        // po przepisaniu nadal czytelny
        Assert.Single(FullTextIndex.Search(index.Load(folder)!, "starego", new TextSearchOptionsEx()));
        await Task.CompletedTask;
    }

    [Fact]
    public void Startup_pass_protects_legacy_files_and_removes_unreadable_ones()
    {
        var (folder, index) = Setup();
        var good = index.IndexFileFor(folder);
        WriteLegacy(good, folder, "Do przepisania");
        var bad = Path.Combine(index.IndexDirectory, "aaaaaaaaaaaaaaaa.zidx");
        File.WriteAllBytes(bad, new byte[] { 0x1F, 0x8B, 1, 2, 3 });
        var oldTemp = Path.Combine(index.IndexDirectory, "x.zidx.abcd.tmp");
        File.WriteAllText(oldTemp, "jawny tekst");
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-3));
        var freshTemp = Path.Combine(index.IndexDirectory, "y.zidx.abcd.tmp");
        File.WriteAllText(freshTemp, "trwa zapis");

        index.ProtectLegacyFiles();

        Assert.DoesNotContain("przepisania", Decompressed(good));
        Assert.False(File.Exists(bad));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(freshTemp));
        Assert.Equal(folder, index.Load(folder)!.Root);
    }

    [Fact]
    public async Task Damaged_protected_file_counts_as_stale_and_is_rebuilt()
    {
        var (folder, index) = Setup();
        await index.BuildAsync(folder, recursive: false);
        var file = index.IndexFileFor(folder);
        var raw = File.ReadAllBytes(file);
        raw[^10] ^= 0xFF;
        File.WriteAllBytes(file, raw);
        Assert.Null(index.Load(folder));
        var result = await index.BuildAsync(folder, recursive: false);
        Assert.Equal(1, result.Indexed);
        Assert.Equal(0, result.Reused);
        Assert.NotNull(index.Load(folder));
    }

    [Fact]
    public async Task Entries_have_size_date_and_count_and_unreadable_file_is_listed()
    {
        var (folder, index) = Setup();
        await index.BuildAsync(folder, recursive: false);
        var unreadable = Path.Combine(index.IndexDirectory, "bbbbbbbbbbbbbbbb.zidx");
        File.WriteAllBytes(unreadable, Encoding.ASCII.GetBytes("ZIDXDP2\n").Concat(new byte[] { 4, 0, 0, 0, 1, 2, 3, 4 }).ToArray());

        var entries = index.ListEntries();

        Assert.Equal(2, entries.Count);
        Assert.Equal(folder, entries[0].Root);
        Assert.Equal(1, entries[0].FileCount);
        Assert.True(entries[0].SizeBytes > 0);
        Assert.True(DateTime.UtcNow - entries[0].BuiltUtc < TimeSpan.FromMinutes(5));
        Assert.Null(entries[1].Root);
        Assert.Equal(unreadable, entries[1].FilePath);
    }

    [Fact]
    public async Task Delete_entry_and_delete_all()
    {
        var (folder, index) = Setup();
        var (other, _) = Setup("Drugi");
        await index.BuildAsync(folder, recursive: false);
        await index.BuildAsync(other, recursive: false);

        var first = index.ListEntries().First(e => e.Root == folder);
        index.DeleteEntry(first);
        Assert.Null(index.Load(folder));
        Assert.NotNull(index.Load(other));

        Assert.Throws<ArgumentException>(() => index.DeleteEntry(first with { FilePath = Path.Combine(other, "umowa.pdf") }));
        Assert.True(File.Exists(Path.Combine(other, "umowa.pdf")));

        index.DeleteAll();
        Assert.Empty(index.ListEntries());
        Assert.True(File.Exists(Path.Combine(other, "umowa.pdf")));
    }

    private static void Sta(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var frame = new System.Windows.Threading.DispatcherFrame();
            body().ContinueWith(r =>
            {
                error = r.Exception?.InnerException;
                frame.Continue = false;
            }, TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void Manage_window_lists_indexes_with_size_and_deletes_them()
    {
        var (folder, _) = Setup();
        var root = TestPaths.NewDirectory();
        var services = new ServiceCollection();
        Core.Services.CoreServiceCollectionExtensions.AddZetPdfCore(services, new Core.Services.AppPaths(root));
        new Rendering.RenderingModule().ConfigureServices(services);
        new EditingModule().ConfigureServices(services);
        using var sp = services.BuildServiceProvider();
        var loc = sp.GetRequiredService<ILocalizationService>();
        loc.RegisterResources(EditingModule.Strings);
        var index = sp.GetRequiredService<FullTextIndex>();
        Sta(async () =>
        {
            await index.BuildAsync(folder, recursive: true);
            var ctx = new EditingContext(sp);
            var controller = new IndexController(ctx, index);
            var window = new IndexManageWindow(loc, controller);
            await window.RefreshAsync();

            var row = Assert.Single(window.Rows);
            Assert.Equal(loc.Format("Edit.Search.IndexRecursive", folder), row.Folder);
            Assert.Equal("1", row.Documents);
            Assert.NotEmpty(row.Size);
            Assert.NotEmpty(row.Built);

            Assert.True(await controller.DeleteAsync(row.Entry));
            await window.RefreshAsync();
            Assert.Empty(window.Rows);
            window.Close();
        });
        Assert.NotEqual("[Edit.Index.Privacy]", loc["Edit.Index.Privacy"]);
    }

    [Theory]
    [InlineData(1, "1 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void Size_is_formatted(long bytes, string expected)
    {
        var services = new ServiceCollection();
        Core.Services.CoreServiceCollectionExtensions.AddZetPdfCore(services, new Core.Services.AppPaths(TestPaths.NewDirectory()));
        using var sp = services.BuildServiceProvider();
        var loc = sp.GetRequiredService<ILocalizationService>();
        loc.RegisterResources(EditingModule.Strings);
        Assert.Equal(expected, IndexManageWindow.FormatSize(bytes, loc));
    }
}

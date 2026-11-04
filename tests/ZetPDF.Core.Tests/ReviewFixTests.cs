using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

/// <summary>Poprawki z przeglądu kodu: zapis i historia zmian.</summary>
public class ReviewFixTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private static IDocumentCommand AddPage() => new DelegateDocumentCommand("Test.AddPage",
        (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);

    [Fact]
    public async Task Safe_write_does_not_touch_existing_bak_file_of_the_user()
    {
        var dir = TestPaths.NewDirectory();
        var path = Path.Combine(dir, "umowa.pdf");
        await File.WriteAllTextAsync(path, "stary");
        await File.WriteAllTextAsync(path + ".bak", "kopia użytkownika");

        await new FileService(_home.Paths).WriteAllBytesSafeAsync(path, new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Equal("kopia użytkownika", await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task Safe_write_with_backup_keeps_previous_version()
    {
        var path = Path.Combine(TestPaths.NewDirectory(), "a.pdf");
        await File.WriteAllTextAsync(path, "stary");
        await new FileService(_home.Paths).WriteAllBytesSafeAsync(path, new byte[] { 9 }, keepBackup: true);
        Assert.Equal("stary", await File.ReadAllTextAsync(path + ".bak"));
    }

    /// <summary>Atrapa zapisu: wykonuje akcję w trakcie zapisu (jak użytkownik edytujący w tym czasie).</summary>
    private sealed class HookedFiles(IFileService inner, Func<Task> during) : IFileService
    {
        public async Task WriteAllBytesSafeAsync(string path, byte[] data, bool keepBackup = false, CancellationToken ct = default)
        {
            await during();
            await inner.WriteAllBytesSafeAsync(path, data, keepBackup, ct);
        }
        public string GetTempFilePath(string extension = ".tmp") => inner.GetTempFilePath(extension);
        public string SanitizeFileName(string name) => inner.SanitizeFileName(name);
    }

    [Fact]
    public async Task Edit_made_during_save_stays_marked_as_unsaved()
    {
        IPdfDocumentSession? session = null;
        bool edited = false;
        Func<Task> during = async () =>
        {
            if (edited) return;
            edited = true;
            await session!.ExecuteAsync(AddPage());
        };
        using var sp = _home.BuildServices(s => s.AddSingleton<IFileService>(new HookedFiles(new FileService(_home.Paths), () => during())));
        var manager = sp.GetRequiredService<IDocumentManager>();
        var path = Path.Combine(TestPaths.NewDirectory(), "a.pdf");
        await File.WriteAllBytesAsync(path, SamplePdfFactory.Text(2));
        session = await manager.OpenAsync(path);
        await session!.ExecuteAsync(AddPage());

        await manager.SaveAsync(session);

        Assert.True(session.IsModified); // czwarta strona powstała w trakcie zapisu – nie ma jej w pliku
        Assert.Equal(3, PdfSharpEdit.Read(await File.ReadAllBytesAsync(path), d => d.PageCount));
        Assert.Equal(4, PdfSharpEdit.Read(session.Current.GetBytes(), d => d.PageCount));

        await session.History.UndoAsync();
        Assert.False(session.IsModified); // Cofnij wraca do wersji zapisanej
    }

    [Fact]
    public async Task Failed_spill_to_disk_does_not_break_editing_or_history()
    {
        var blocker = Path.Combine(TestPaths.NewDirectory(), "to-jest-plik");
        await File.WriteAllTextAsync(blocker, "x");
        var original = SamplePdfFactory.Text(2);
        using var s = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(original), PdfSecurityInfo.None, DocumentPermissions.All,
            new SnapshotStorageOptions(Path.Combine(blocker, "snapshots"), MemoryBudgetBytes: 1));

        Assert.True(await s.ExecuteAsync(AddPage()));
        Assert.True(await s.ExecuteAsync(AddPage()));
        Assert.Equal(4, PdfSharpEdit.Read(s.Current.GetBytes(), d => d.PageCount));

        await s.History.UndoAsync();
        await s.History.UndoAsync();
        Assert.Equal(original, s.Current.GetBytes());
        await s.History.RedoAsync();
        Assert.Equal(3, PdfSharpEdit.Read(s.Current.GetBytes(), d => d.PageCount));
    }

    [Fact]
    public async Task Snapshots_on_disk_are_protected_also_for_unencrypted_documents()
    {
        var dir = Path.Combine(TestPaths.NewDirectory(), "snapshots");
        var original = SamplePdfFactory.Text(2);
        using var s = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(original), PdfSecurityInfo.None, DocumentPermissions.All,
            new SnapshotStorageOptions(dir, MemoryBudgetBytes: 1));
        await s.ExecuteAsync(AddPage());
        await s.ExecuteAsync(AddPage());

        var files = Directory.GetFiles(dir);
        Assert.NotEmpty(files);
        foreach (var f in files)
            Assert.DoesNotContain("%PDF", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(f)));

        await s.History.UndoAsync();
        await s.History.UndoAsync();
        Assert.Equal(original, s.Current.GetBytes());
    }

    [Fact]
    public void Orphaned_temp_folders_are_removed_but_own_and_foreign_ones_stay()
    {
        var root = TestPaths.NewDirectory();
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.TempDirectory);
        var tempRoot = Path.GetDirectoryName(paths.TempDirectory)!;
        var orphan = Path.Combine(tempRoot, "2147483000");
        var other = Path.Combine(tempRoot, "nie-proces");
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(orphan, "snap.bin"), "x");

        Assert.Equal(1, paths.CleanupOrphanedTemp());

        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(paths.TempDirectory));
    }
}

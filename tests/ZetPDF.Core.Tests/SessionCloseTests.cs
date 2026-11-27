using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

/// <summary>Zamknięcie dokumentu w trakcie polecenia (karta zamknięta, zanim operacja się skończyła).</summary>
public class SessionCloseTests
{
    private static IDocumentCommand AddPage() => new DelegateDocumentCommand("Test.AddPage",
        (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);

    private static IDocumentCommand Blocking(Task gate) => new DelegateDocumentCommand("Test.Blocking",
        async (pdf, _, _) => { await gate; return PdfSharpEdit.Modify(pdf, d => d.AddPage()); }, DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);

    [Fact]
    public async Task Closing_during_command_discards_result_without_exception_and_releases_snapshots()
    {
        var dir = TestPaths.NewDirectory();
        var session = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(SamplePdfFactory.Text(1)), PdfSecurityInfo.None, DocumentPermissions.All,
            new SnapshotStorageOptions(dir, MemoryBudgetBytes: 1));
        for (int i = 0; i < 3; i++) await session.ExecuteAsync(AddPage());
        Assert.NotEmpty(Directory.GetFiles(dir)); // starsze zrzuty są na dysku

        var gate = new TaskCompletionSource();
        int changed = 0;
        session.Changed += (_, _) => changed++;
        var running = session.ExecuteAsync(Blocking(gate.Task));
        await Task.Delay(50);
        Assert.True(session.IsBusy);

        session.Dispose();
        Assert.NotEmpty(Directory.GetFiles(dir)); // polecenie trwa – zrzuty jeszcze nie zwolnione
        gate.SetResult();

        Assert.False(await running);
        Assert.Equal(0, changed);
        Assert.Empty(Directory.GetFiles(dir));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync(AddPage()));
    }

    [Fact]
    public async Task Closing_idle_session_releases_snapshots_at_once()
    {
        var dir = TestPaths.NewDirectory();
        var session = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(SamplePdfFactory.Text(1)), PdfSecurityInfo.None, DocumentPermissions.All,
            new SnapshotStorageOptions(dir, MemoryBudgetBytes: 1));
        for (int i = 0; i < 3; i++) await session.ExecuteAsync(AddPage());
        session.Dispose();
        Assert.Empty(Directory.GetFiles(dir));
        session.Dispose(); // drugi raz bez błędu
    }

    [Fact]
    public async Task Undo_after_close_is_ignored()
    {
        var session = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(SamplePdfFactory.Text(1)), PdfSecurityInfo.None, DocumentPermissions.All,
            new SnapshotStorageOptions(TestPaths.NewDirectory()));
        await session.ExecuteAsync(AddPage());
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.History.UndoAsync());
    }
}

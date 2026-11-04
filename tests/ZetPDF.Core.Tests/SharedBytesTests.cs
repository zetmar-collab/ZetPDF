using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

public class SharedBytesTests
{
    [Fact]
    public void Shared_bytes_do_not_copy_but_GetBytes_still_does()
    {
        var pdf = SamplePdfFactory.Text(1);
        var snapshot = DocumentSnapshot.FromBytes(pdf);

        Assert.Same(pdf, snapshot.GetSharedBytes());
        Assert.NotSame(pdf, snapshot.GetBytes());
    }

    [Fact]
    public async Task Shared_bytes_work_for_snapshot_moved_to_disk()
    {
        var original = SamplePdfFactory.Text(1);
        using var s = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(original), PdfSecurityInfo.None, DocumentPermissions.All,
            new SnapshotStorageOptions(Path.Combine(TestPaths.NewDirectory(), "snapshots"), MemoryBudgetBytes: 1));
        var addPage = new DelegateDocumentCommand("Test.AddPage",
            (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);
        var first = s.Current;

        await s.ExecuteAsync(addPage);
        await s.ExecuteAsync(addPage);

        Assert.Equal(original, first.GetSharedBytes());
        Assert.Equal(original, first.GetBytes());
    }

    [Fact]
    public async Task Command_does_not_change_the_snapshot_it_read()
    {
        var original = SamplePdfFactory.Text(1);
        var copy = (byte[])original.Clone();
        using var s = new PdfDocumentSession(null, "t.pdf", DocumentSnapshot.FromBytes(original), PdfSecurityInfo.None, DocumentPermissions.All,
            new SnapshotStorageOptions(Path.Combine(TestPaths.NewDirectory(), "snapshots")));

        await s.ExecuteAsync(new DelegateDocumentCommand("Test.AddPage",
            (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument));

        Assert.Equal(copy, original);
    }
}

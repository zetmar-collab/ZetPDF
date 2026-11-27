using Microsoft.Extensions.Logging.Abstractions;
using ZetPDF.App.Shell;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Operacje powłoki na dokumentach (DocumentActions) z atrapą okien i prawdziwym DocumentManager.</summary>
public class DocumentActionsTests
{
    private sealed class NoPrompts : IOwnerProtectionPrompts
    {
        public OwnerProtectionChoice AskOwnerProtectionChoice(string documentName, bool canEnterOwnerPassword) => OwnerProtectionChoice.Cancel;
        public string? AskOwnerPassword(string documentName, bool isRetry) => null;
        public string? AskNewPermissionsPassword(string documentName) => null;
    }

    private static DocumentActions Actions(TestHost host) => new(host.Documents, host.Dialogs, host.Get<ILocalizationService>(),
        host.Get<IRecentFilesService>(), host.Shell, NullLogger<DocumentActions>.Instance, new OwnerProtectionGuard(host.Documents, new NoPrompts()));

    private static IDocumentCommand AddPage() => new DelegateDocumentCommand("Test.AddPage",
        (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);

    private static async Task<(IPdfDocumentSession Session, string Path)> OpenSignedAsync(TestHost host)
    {
        var path = Path.Combine(TestPaths.NewDirectory(), "podpisany ą.pdf");
        await File.WriteAllBytesAsync(path, SamplePdfFactory.Text(1).Concat("\n%/ByteRange\n"u8.ToArray()).ToArray());
        var session = (await host.Documents.OpenAsync(path))!;
        await session.ExecuteAsync(AddPage());
        return (session, path);
    }

    [Fact]
    public async Task Save_as_onto_own_signed_file_asks_like_save_and_cancel_keeps_file()
    {
        using var host = new TestHost(withShellFakes: true);
        var (session, path) = await OpenSignedAsync(host);
        var before = await File.ReadAllBytesAsync(path);
        host.Dialogs.FileToSave = path;
        host.Dialogs.YesNoCancelResult = YesNoCancel.Cancel;

        Assert.False(await Actions(host).SaveAsAsync(session));

        Assert.Single(host.Dialogs.Questions);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.True(session.IsModified);
    }

    [Fact]
    public async Task Save_as_onto_own_file_changed_on_disk_asks_before_overwriting()
    {
        using var host = new TestHost(withShellFakes: true);
        var path = Path.Combine(TestPaths.NewDirectory(), "a.pdf");
        await File.WriteAllBytesAsync(path, SamplePdfFactory.Text(1));
        var session = (await host.Documents.OpenAsync(path))!;
        var foreign = SamplePdfFactory.Text(3);
        await File.WriteAllBytesAsync(path, foreign);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        host.Dialogs.FileToSave = path;
        host.Dialogs.ConfirmResult = false;

        Assert.False(await Actions(host).SaveAsAsync(session));

        Assert.Single(host.Dialogs.Confirmations);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Save_as_onto_own_signed_file_overwrite_answer_saves()
    {
        using var host = new TestHost(withShellFakes: true);
        var (session, path) = await OpenSignedAsync(host);
        host.Dialogs.FileToSave = path;
        host.Dialogs.YesNoCancelResult = YesNoCancel.No; // Nadpisz

        Assert.True(await Actions(host).SaveAsAsync(session));

        Assert.False(session.IsModified);
        Assert.Equal(2, PdfSharpEdit.Read(await File.ReadAllBytesAsync(path), d => d.PageCount));
    }

    [Fact]
    public async Task Save_as_to_another_file_does_not_ask_about_signatures()
    {
        using var host = new TestHost(withShellFakes: true);
        var (session, _) = await OpenSignedAsync(host);
        host.Dialogs.FileToSave = Path.Combine(TestPaths.NewDirectory(), "kopia.pdf");

        Assert.True(await Actions(host).SaveAsAsync(session));
        Assert.Empty(host.Dialogs.Questions);
    }
}

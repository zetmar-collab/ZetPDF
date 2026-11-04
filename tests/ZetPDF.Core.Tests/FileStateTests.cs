using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

/// <summary>Stan pliku na dysku: zmiana przez inny program i podpisy, które zapis by unieważnił.</summary>
public class FileStateTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private static IDocumentCommand AddPage() => new DelegateDocumentCommand("Test.AddPage",
        (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);

    private async Task<(ServiceProvider Sp, DocumentManager Manager, IPdfDocumentSession Session, string Path)> OpenAsync(byte[] pdf)
    {
        var sp = _home.BuildServices();
        var manager = (DocumentManager)sp.GetRequiredService<IDocumentManager>();
        var path = Path.Combine(TestPaths.NewDirectory(), "a.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        return (sp, manager, (await manager.OpenAsync(path))!, path);
    }

    [Fact]
    public async Task Change_by_another_program_is_detected_until_next_save()
    {
        var (sp, manager, session, path) = await OpenAsync(SamplePdfFactory.Text(2));
        using (sp)
        {
            Assert.False(manager.HasChangedOnDisk(session));

            await File.WriteAllBytesAsync(path, SamplePdfFactory.Text(3));
            Assert.True(manager.HasChangedOnDisk(session));

            await manager.SaveAsync(session);
            Assert.False(manager.HasChangedOnDisk(session));
        }
    }

    [Fact]
    public async Task Missing_file_and_new_document_do_not_count_as_changed()
    {
        var (sp, manager, session, path) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp)
        {
            File.Delete(path);
            Assert.False(manager.HasChangedOnDisk(session));
            var created = await manager.CreateNewAsync(SamplePdfFactory.Text(1), "nowy.pdf");
            Assert.False(manager.HasChangedOnDisk(created));
        }
    }

    [Fact]
    public async Task Plain_file_has_no_signatures()
    {
        var (sp, manager, session, _) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp) Assert.False(manager.HasSignatures(session));
    }

    [Fact]
    public async Task Signature_is_tracked_until_changes_are_saved_over_the_file()
    {
        // Znacznik podpisu w komentarzu pliku: rdzeń szuka tylko /ByteRange i niczego tu nie weryfikuje.
        var signed = SamplePdfFactory.Text(1).Concat("\n%/ByteRange\n"u8.ToArray()).ToArray();
        var (sp, manager, session, _) = await OpenAsync(signed);
        using (sp)
        {
            Assert.True(manager.HasSignatures(session));

            await manager.SaveAsync(session); // zapis bez zmian zachowuje bajty, więc i podpis
            Assert.True(manager.HasSignatures(session));

            await session.ExecuteAsync(AddPage());
            await manager.SaveAsync(session);
            Assert.False(manager.HasSignatures(session));
        }
    }
}

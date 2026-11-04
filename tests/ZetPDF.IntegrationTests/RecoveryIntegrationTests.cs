using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Services;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Odzyskiwanie po awarii na prawdziwych usługach (PDFium): dokument z hasłem użytkownika nie traci ochrony ani ograniczeń.</summary>
public class RecoveryIntegrationTests
{
    [Fact]
    public void User_password_document_keeps_protection_and_permissions_after_recovery() => Sta.Run(async () =>
    {
        using var host = new TestHost(withShellFakes: true);
        var manager = (DocumentManager)host.Documents;
        var auto = host.Get<AutoSaveService>();
        var path = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256());
        var session = (await manager.OpenAsync(path, SamplePdfFactory.UserPassword))!;
        var permissions = session.Permissions;
        Assert.NotEqual(DocumentPermissions.All, permissions);

        auto.Start();
        await session.ExecuteAsync(new DelegateDocumentCommand("Test.Edit", (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.Info.Keywords = "po awarii")), DocumentChangeKind.Metadata, DocumentPermissions.None));
        await auto.SaveNowAsync();
        auto.Stop(deleteFiles: false);

        var dir = host.Get<IAppPaths>().RecoveryDirectory;
        var id = Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(dir, "*.info")));
        foreach (var file in Directory.GetFiles(dir))
        {
            var raw = await File.ReadAllBytesAsync(file);
            Assert.True(raw.AsSpan().IndexOf("%PDF"u8) < 0);
            Assert.DoesNotContain(SamplePdfFactory.UserPassword, System.Text.Encoding.Latin1.GetString(raw));
        }
        var bytes = auto.Store.ReadPdf(id);
        manager.Close(session); // bez Stop: autozapis odłączony, kopia została jak po awarii
        Assert.True(File.Exists(Path.Combine(dir, id + ".info")));

        host.Dialogs.Passwords.Enqueue("złe hasło");
        host.Dialogs.Passwords.Enqueue(SamplePdfFactory.UserPassword);
        var info = new RecoveryInfo
        {
            Id = id,
            OriginalPath = path,
            DisplayName = Path.GetFileName(path),
            IsProtected = true,
            Permissions = permissions,
            SourceLength = new FileInfo(path).Length,
            SourceModifiedUtcTicks = new FileInfo(path).LastWriteTimeUtc.Ticks,
        };
        var recovered = (await manager.OpenRecoveredAsync(info, bytes, _ => throw new InvalidOperationException("Nie powinno pytać o nowe hasło.")))!;

        Assert.True(recovered.IsModified);
        Assert.Equal(path, recovered.FilePath);
        Assert.Equal(permissions, recovered.Permissions);
        Assert.Equal(bytes, recovered.Current.GetBytes());
        Assert.False(manager.HasChangedOnDisk(recovered));

        await manager.SaveAsync(recovered);
        var renderer = host.Get<IPdfRenderer>();
        var saved = await File.ReadAllBytesAsync(path);
        Assert.Equal(PdfProbeStatus.PasswordRequired, (await renderer.ProbeAsync(saved, null)).Status);
        Assert.Equal(PdfProbeStatus.Ok, (await renderer.ProbeAsync(saved, SamplePdfFactory.UserPassword)).Status);
    });
}

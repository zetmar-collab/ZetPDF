using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Security.Tests;

public class EncryptionTests
{
    private const string User = "otwórz-Zażółć";
    private const string Owner = "właściciel-123";

    [Theory]
    [InlineData(PdfEncryptionMethod.Aes256)]
    [InlineData(PdfEncryptionMethod.Aes128)]
    public async Task Encrypted_file_opens_in_pdfium_and_pdfpig_only_with_password(PdfEncryptionMethod method)
    {
        // AES-128 (R4): hasła w PDFDocEncoding – znaki spoza Latin-1 nie są przenośne między czytnikami (patrz ograniczenia)
        string user = method == PdfEncryptionMethod.Aes256 ? User : "open-123";
        string owner = method == PdfEncryptionMethod.Aes256 ? Owner : "owner-456";
        var svc = new SecurityService();
        var enc = await svc.EncryptAsync(SamplePdfFactory.Text(2), new PdfEncryptionSettings(user, owner, method, DocumentPermissions.Print));

        var pdfium = new PdfiumRenderer();
        await Assert.ThrowsAnyAsync<PdfPasswordRequiredException>(() => pdfium.OpenAsync(enc));
        await Assert.ThrowsAnyAsync<PdfPasswordRequiredException>(() => pdfium.OpenAsync(enc, "złe"));
        using (var d = await pdfium.OpenAsync(enc, user)) Assert.Equal(2, d.PageCount);
        using (var d = await pdfium.OpenAsync(enc, owner)) Assert.Equal(2, d.PageCount);

        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(enc));
        using (var pig = PdfDocument.Open(enc, new ParsingOptions { Password = user }))
            Assert.Equal(2, pig.NumberOfPages);

        var info = await svc.AnalyzeAsync(enc, user);
        Assert.True(info.IsEncrypted);
        Assert.Equal(method, info.Method);
        Assert.False(info.OpenedWithOwnerPassword);
        Assert.True(info.FilePermissions.HasFlag(DocumentPermissions.Print));
        Assert.False(info.FilePermissions.HasFlag(DocumentPermissions.ModifyContent));
        Assert.False(info.FilePermissions.HasFlag(DocumentPermissions.CopyContent));

        var asOwner = await svc.AnalyzeAsync(enc, owner);
        Assert.True(asOwner.OpenedWithOwnerPassword);
        await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => svc.AnalyzeAsync(enc, "złe"));
    }

    [Fact]
    public async Task Resave_settings_follow_decision_D08()
    {
        var svc = new SecurityService();
        var enc = await svc.EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings(User, Owner, PdfEncryptionMethod.Aes256, DocumentPermissions.Print));

        var user = svc.CreateResaveSettings(await svc.AnalyzeAsync(enc, User))!;
        Assert.Equal(User, user.UserPassword);
        Assert.NotEqual(Owner, user.OwnerPassword);
        Assert.Equal(DocumentPermissions.Print, user.Permissions & DocumentPermissions.Print);

        var ownerInfo = await svc.AnalyzeAsync(enc, Owner);
        var owner = svc.CreateResaveSettings(ownerInfo)!;
        Assert.Equal(Owner, owner.OwnerPassword);
        Assert.True(svc.IsResaveOwnerOnly(ownerInfo)); // AES-256: hasła użytkownika nie da się odzyskać

        // RC4/AES-128 (R4): hasło użytkownika odzyskiwane z hasła właściciela
        var enc128 = await svc.EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings("abc", Owner, PdfEncryptionMethod.Aes128));
        var info128 = await svc.AnalyzeAsync(enc128, Owner);
        Assert.Equal("abc", svc.CreateResaveSettings(info128)!.UserPassword);
        Assert.False(svc.IsResaveOwnerOnly(info128));

        // puste hasło użytkownika
        var noUser = await svc.EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings(null, Owner, PdfEncryptionMethod.Aes256, DocumentPermissions.Print));
        var infoNoUser = await svc.AnalyzeAsync(noUser, null);
        Assert.True(infoNoUser.IsEncrypted);
        Assert.False(infoNoUser.OpenedWithOwnerPassword);
        Assert.Null(svc.CreateResaveSettings(infoNoUser)!.UserPassword);
        Assert.True(svc.IsUserPasswordEmpty(infoNoUser));
    }

    [Fact]
    public async Task Analyze_unencrypted_returns_none()
    {
        var info = await new SecurityService().AnalyzeAsync(SamplePdfFactory.Text(1), null);
        Assert.False(info.IsEncrypted);
    }

    [Fact]
    public async Task Password_protect_and_remove_security_through_session_and_save()
    {
        var home = Path.Combine(Path.GetTempPath(), "ZetPDF-tests", "home-" + Guid.NewGuid().ToString("N")[..8]);
        var services = new ServiceCollection();
        services.AddZetPdfCore(new AppPaths(home));
        new SecurityModule().ConfigureServices(services);
        services.AddSingleton<IPdfRenderer, PdfiumRenderer>();
        using var sp = services.BuildServiceProvider();
        var docs = sp.GetRequiredService<IDocumentManager>();
        var pdfium = sp.GetRequiredService<IPdfRenderer>();

        // 1) Ochrona hasłem: plik bez zabezpieczeń → AES-256
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(2));
        var session = (await docs.OpenAsync(path))!;
        await session.ExecuteAsync(new SetEncryptionCommand("Core.Undo.SetEncryption", new PdfEncryptionSettings(User, Owner)));
        var protectedPath = Path.Combine(Path.GetDirectoryName(path)!, "chroniony ąę.pdf");
        await docs.SaveAsync(session, protectedPath);
        var saved = await File.ReadAllBytesAsync(protectedPath);
        await Assert.ThrowsAnyAsync<PdfPasswordRequiredException>(() => pdfium.OpenAsync(saved));
        using (var d = await pdfium.OpenAsync(saved, User)) Assert.Equal(2, d.PageCount);
        docs.Close(session);

        // 2) Usunięcie zabezpieczeń przy znanym haśle (właściciela)
        var reopened = (await docs.OpenAsync(protectedPath, Owner))!;
        Assert.Equal(DocumentPermissions.All, reopened.Permissions);
        await reopened.ExecuteAsync(new SetEncryptionCommand("Core.Undo.RemoveEncryption", null));
        var plainPath = Path.Combine(Path.GetDirectoryName(path)!, "bez hasła.pdf");
        await docs.SaveAsync(reopened, plainPath);
        var plain = await File.ReadAllBytesAsync(plainPath);
        using (var d = await pdfium.OpenAsync(plain)) Assert.Equal(2, d.PageCount);
        Assert.False((await new SecurityService().AnalyzeAsync(plain, null)).IsEncrypted);

        // 3) Hasło użytkownika z ograniczeniami → uprawnienia egzekwowane w sesji
        var restricted = await new SecurityService().EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings(User, Owner, PdfEncryptionMethod.Aes256, DocumentPermissions.Print));
        var rPath = TestPaths.WriteSample(restricted, "ograniczony.pdf");
        var rs = (await docs.OpenAsync(rPath, User))!;
        Assert.False(rs.Can(DocumentPermissions.ModifyContent));
        await Assert.ThrowsAsync<DocumentPermissionException>(() => rs.ExecuteAsync(new SetEncryptionCommand("Core.Undo.RemoveEncryption", null)));
    }
}

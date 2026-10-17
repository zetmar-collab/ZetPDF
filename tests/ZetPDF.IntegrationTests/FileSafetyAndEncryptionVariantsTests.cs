using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Bezpieczny zapis (plik zablokowany, tylko do odczytu) i warianty szyfrowania (AES-128, RC4, tylko hasło uprawnień).</summary>
public class FileSafetyAndEncryptionVariantsTests
{
    private static IDocumentCommand Note(TestHost host)
    {
        var op = Operations.Get("Annot.Note");
        return new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.Annotations, op.Permission);
    }

    [Fact]
    public async Task Save_to_locked_file_fails_cleanly_and_leaves_original_and_folder_intact()
    {
        using var host = new TestHost();
        var original = SamplePdfFactory.Text(1);
        var path = TestPaths.WriteSample(original, "zablokowany ąę.pdf");
        var dir = Path.GetDirectoryName(path)!;
        var s = (await host.Documents.OpenAsync(path))!;
        await s.ExecuteAsync(Note(host));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // inny program trzyma plik otwarty
        {
            var ex = await Record.ExceptionAsync(() => host.Documents.SaveAsync(s));
            Assert.NotNull(ex);
            Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().FullName + ": " + ex.Message);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir)); // brak pozostawionych plików tymczasowych i .bak
        Assert.True(s.IsModified);
        // po zwolnieniu blokady zapis się udaje
        await host.Documents.SaveAsync(s);
        Assert.False(s.IsModified);
        Assert.Contains((0, "/Text"), Inspect.Annotations(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task Save_to_read_only_file_fails_cleanly_and_save_as_elsewhere_works()
    {
        using var host = new TestHost();
        var original = SamplePdfFactory.Text(1);
        var path = TestPaths.WriteSample(original, "tylko do odczytu ść.pdf");
        var dir = Path.GetDirectoryName(path)!;
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var s = (await host.Documents.OpenAsync(path))!;
            await s.ExecuteAsync(Note(host));
            var ex = await Record.ExceptionAsync(() => host.Documents.SaveAsync(s));
            Assert.NotNull(ex);
            Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().FullName);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(dir));
            var other = Path.Combine(dir, "kopia zapisu ąę.pdf");
            await host.Documents.SaveAsync(s, other);
            Assert.Contains((0, "/Text"), Inspect.Annotations(File.ReadAllBytes(other)));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(PdfEncryptionMethod.Aes256, "/AESV3", PdfEncryptionMethod.Aes256, "/AESV3")]
    [InlineData(PdfEncryptionMethod.Aes128, "/AESV2", PdfEncryptionMethod.Aes128, "/AESV2")]
    // RC4 jest celowo podnoszony do AES-256 przy zapisie (CreateResaveSettings) – zachowanie nieopisane w DECYZJE/OGRANICZENIA (zgłoszone w raporcie).
    [InlineData(PdfEncryptionMethod.Rc4_128, "/V 2", PdfEncryptionMethod.Aes256, "/AESV3")]
    public async Task Encryption_method_after_resave_with_user_password(PdfEncryptionMethod method, string marker, PdfEncryptionMethod expectedMethod, string expectedMarker)
    {
        using var host = new TestHost();
        var enc = await host.Get<ISecurityService>().EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings("user1", "owner1", method, DocumentPermissions.Print | DocumentPermissions.Annotate));
        var encDict = System.Text.RegularExpressions.Regex.Match(Inspect.Latin1(enc), @"<<[^<>]*/Filter\s*/Standard[^<>]*(<<[^<>]*>>[^<>]*)*>>").Value;
        Assert.True(Inspect.Latin1(enc).Contains(marker), $"brak {marker} w słowniku szyfrowania: {encDict}");
        var path = TestPaths.WriteSample(enc, $"metoda {method} ą.pdf");
        var s = (await host.Documents.OpenAsync(path, "user1"))!;
        Assert.Equal(method, s.SecurityInfo.Method);
        Assert.False(s.Can(DocumentPermissions.ModifyContent));
        await s.ExecuteAsync(Note(host));
        await host.Documents.SaveAsync(s);
        var saved = File.ReadAllBytes(path);
        var info = PdfSecurityHelper.Analyze(saved, "user1");
        Assert.True(expectedMethod == info.Method, $"metoda szyfrowania po zapisie: {info.Method} (rewizja {info.Revision}), oczekiwano {expectedMethod}");
        Assert.Contains(expectedMarker, Inspect.Latin1(saved));
        Assert.Contains("gęślą", await Inspect.PdfiumText(saved, "user1"));
        Assert.Contains("gęślą", Inspect.PigText(saved, "user1"));
        var p = await Inspect.Renderer.ProbeAsync(saved, "user1");
        Assert.False(p.Permissions.HasFlag(DocumentPermissions.ModifyContent));
    }

    [Fact]
    public async Task Owner_only_encryption_opens_without_prompt_with_restricted_rights_and_keeps_owner_password()
    {
        using var host = new TestHost();
        var enc = await host.Get<ISecurityService>().EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings(null, "tylko-właściciel-ą", PdfEncryptionMethod.Aes256, DocumentPermissions.Print | DocumentPermissions.Annotate));
        // każdy czytnik otwiera bez hasła
        Assert.Contains("gęślą", await Inspect.PdfiumText(enc));
        Assert.Contains("gęślą", Inspect.PigText(enc));
        var path = TestPaths.WriteSample(enc, "bez hasła otwarcia ą.pdf");
        var s = (await host.Documents.OpenAsync(path))!; // bez okna hasła (brak IDialogService → wyjątek, gdyby pytał)
        Assert.True(s.SecurityInfo.IsEncrypted);
        Assert.False(s.Can(DocumentPermissions.ModifyContent));
        Assert.True(s.Can(DocumentPermissions.Annotate));
        await s.ExecuteAsync(Note(host));
        await host.Documents.SaveAsync(s);
        var saved = File.ReadAllBytes(path);
        Assert.True(PdfSecurityHelper.IsEncrypted(saved));
        Assert.Contains("gęślą", await Inspect.PdfiumText(saved)); // nadal bez hasła otwarcia
        var p = await Inspect.Renderer.ProbeAsync(saved);
        Assert.False(p.Permissions.HasFlag(DocumentPermissions.ModifyContent));

        // otwarte hasłem właściciela (oryginał): pełne prawa, zapis zachowuje puste hasło otwarcia i hasło właściciela.
        // (Po zapisie bez hasła właściciela ZetPDF nadaje nowe losowe hasło właściciela – DECYZJE D-08.)
        host.Documents.Close(s);
        var ownerPath = TestPaths.WriteSample(enc, "właściciel ą.pdf");
        var so = (await host.Documents.OpenAsync(ownerPath, "tylko-właściciel-ą"))!;
        Assert.Equal(DocumentPermissions.All, so.Permissions);
        await host.Documents.SaveAsync(so, Path.Combine(Path.GetDirectoryName(ownerPath)!, "właściciel zapisał.pdf"));
        var saved2 = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(ownerPath)!, "właściciel zapisał.pdf"));
        Assert.Contains("gęślą", await Inspect.PdfiumText(saved2));
        Assert.True(PdfSecurityHelper.IsOwnerPassword(saved2, "tylko-właściciel-ą"));
    }

    [Fact]
    public async Task Watermark_options_are_stored_for_update_and_removal_restores_text_only()
    {
        using var host = new TestHost();
        var st = host.Get<IStampingService>();
        var src = SamplePdfFactory.Text(2);
        var opts = new WatermarkOptions { Text = "PROJEKT – ŻÓŁĆ", Opacity = 0.5, RotationDegrees = 30, PageRange = "2" };
        var wm = await st.AddWatermarkAsync(src, opts);
        Assert.DoesNotContain("PROJEKT", Inspect.PigPageText(wm, 0));
        Assert.Contains("PROJEKT", Inspect.PigPageText(wm, 1));
        var stored = await st.GetStoredOptionsAsync<WatermarkOptions>(wm, PageMarkKind.Watermark);
        Assert.NotNull(stored);
        Assert.Equal(opts.Text, stored!.Text);
        Assert.Equal(opts.PageRange, stored.PageRange);
        Assert.Equal(opts.RotationDegrees, stored.RotationDegrees);
        var removed = await st.RemoveWatermarksAsync(wm);
        Assert.DoesNotContain("PROJEKT", Inspect.PigText(removed));
        Assert.Equal(PageMarkKind.None, await st.DetectMarksAsync(removed));
        Assert.Equal(Inspect.Squash(Inspect.PigText(src)), Inspect.Squash(Inspect.PigText(removed)));
    }
}

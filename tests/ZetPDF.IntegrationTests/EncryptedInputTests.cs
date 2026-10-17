using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Plik zaszyfrowany AES-256 jako wejście operacji każdego modułu: otwarcie hasłem (właściciela lub użytkownika) przez
/// IDocumentManager, operacja w sesji, wynik w pliku, zapis – plik nadal zaszyfrowany AES-256 i otwiera się w PDFium
/// i PdfPig tym hasłem, którym otwarto, a wynik operacji jest w nim obecny.
/// </summary>
public class EncryptedInputTests
{
    private const string User = "użytkownik-ąę";
    private const string Owner = "właściciel-ść";

    public static IEnumerable<object[]> Cases() =>
        Operations.All.SelectMany(o => new[] { new object[] { o.Name, true }, new object[] { o.Name, false } });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Operation_on_aes256_document_keeps_encryption_and_result_after_save(string name, bool ownerPassword)
    {
        var op = Operations.Get(name);
        using var host = new TestHost();
        var encrypted = await host.Get<ISecurityService>().EncryptAsync(op.Sample(), new PdfEncryptionSettings(User, Owner, PdfEncryptionMethod.Aes256, DocumentPermissions.All));
        Assert.True(PdfSecurityHelper.IsEncrypted(encrypted));
        var path = TestPaths.WriteSample(encrypted, "zaszyfrowany ąę.pdf");
        var password = ownerPassword ? Owner : User;

        await Assert.ThrowsAnyAsync<PdfPasswordRequiredException>(() => host.Documents.OpenAsync(path));
        var session = await host.Documents.OpenAsync(path, password);
        Assert.NotNull(session);
        Assert.True(session!.SecurityInfo.IsEncrypted);
        Assert.Equal(PdfEncryptionMethod.Aes256, session.SecurityInfo.Method);
        Assert.False(PdfSecurityHelper.IsEncrypted(session.Current.GetBytes()));

        Assert.True(await session.ExecuteAsync(new DelegateDocumentCommand("Test." + name, (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission)));
        await op.Verify(host, session.Current.GetBytes());

        var outPath = Path.Combine(Path.GetDirectoryName(path)!, $"zapisany {name} ść.pdf");
        await host.Documents.SaveAsync(session, outPath);
        var saved = File.ReadAllBytes(outPath);
        Assert.True(PdfSecurityHelper.IsEncrypted(saved), "zapisany plik nie jest zaszyfrowany");
        Assert.Contains("/AESV3", Inspect.Latin1(saved));
        await Assert.ThrowsAnyAsync<PdfPasswordRequiredException>(() => Inspect.Renderer.OpenAsync(saved));
        Assert.True(await Inspect.PdfiumPageCount(saved, password) >= 1);
        Assert.True(Inspect.PageCount(saved, password) >= 1); // PdfPig

        var decrypted = await Inspect.Renderer.RemoveSecurityAsync(saved, password);
        await op.Verify(host, decrypted);
    }

    [Fact]
    public async Task User_password_with_restricted_permissions_blocks_modification_but_allows_permitted_operations()
    {
        // SamplePdfFactory.EncryptedAes256: bez modyfikacji i kopiowania, komentarze i druk dozwolone.
        using var host = new TestHost();
        var path = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "ograniczony ąę.pdf");
        var session = (await host.Documents.OpenAsync(path, SamplePdfFactory.UserPassword))!;
        Assert.False(session.Can(DocumentPermissions.ModifyContent));
        Assert.False(session.Can(DocumentPermissions.CopyContent));
        Assert.True(session.Can(DocumentPermissions.Annotate));

        foreach (var name in new[] { "Pages.Watermark", "Edit.AddText", "Edit.Redact" })
        {
            var op = Operations.Get(name);
            await Assert.ThrowsAsync<DocumentPermissionException>(() =>
                session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission)));
        }
        Assert.False(session.History.CanUndo);

        var note = Operations.Get("Annot.Note");
        Assert.True(await session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => note.Run(host, pdf), DocumentChangeKind.Annotations, note.Permission)));
        var outPath = Path.Combine(Path.GetDirectoryName(path)!, "z notatką.pdf");
        await host.Documents.SaveAsync(session, outPath);
        var saved = File.ReadAllBytes(outPath);
        var info = PdfSecurityHelper.Analyze(saved, SamplePdfFactory.UserPassword);
        Assert.Equal(PdfEncryptionMethod.Aes256, info.Method);
        Assert.False(info.FilePermissions.HasFlag(DocumentPermissions.ModifyContent)); // ograniczenia zachowane
        Assert.False(info.FilePermissions.HasFlag(DocumentPermissions.CopyContent));
        await note.Verify(host, await Inspect.Renderer.RemoveSecurityAsync(saved, SamplePdfFactory.UserPassword));
    }

    [Fact]
    public async Task Merge_and_insert_accept_encrypted_sources_with_password()
    {
        using var host = new TestHost();
        var enc = SamplePdfFactory.EncryptedAes256(pages: 2);
        var encPath = TestPaths.WriteSample(enc, "źródło szyfrowane.pdf");
        var pages = host.Get<IPageOperations>();

        var merged = await pages.MergeAsync(new[] { new MergeSource(TestPaths.WriteSample(SamplePdfFactory.Text(1), "pierwszy ą.pdf")), new MergeSource(encPath, SamplePdfFactory.UserPassword) }, new MergeOptions());
        Assert.Equal(3, Inspect.PageCount(merged));
        Assert.Contains("gęślą", Inspect.PigPageText(merged, 2));

        var inserted = await pages.InsertFromFileAsync(SamplePdfFactory.Text(1), encPath, SamplePdfFactory.OwnerPassword, null, 0);
        Assert.Equal(3, Inspect.PageCount(inserted));

        await Assert.ThrowsAnyAsync<Exception>(() => pages.InsertFromFileAsync(SamplePdfFactory.Text(1), encPath, "złe hasło", null, 0));
    }

    [Fact]
    public async Task Signature_validation_and_compare_work_on_encrypted_files_with_password()
    {
        using var host = new TestHost();
        var a = SamplePdfFactory.EncryptedAes256();
        var b = await host.Get<ISecurityService>().EncryptAsync(SamplePdfFactory.Text(2, "Dodatkowa linia źdźbło"), new PdfEncryptionSettings(SamplePdfFactory.UserPassword, SamplePdfFactory.OwnerPassword));
        // porównanie dostaje odszyfrowane bajty (jak z sesji)
        var r = host.Get<IPdfRenderer>();
        var result = await host.Get<ICompareService>().CompareAsync(await r.RemoveSecurityAsync(a, SamplePdfFactory.UserPassword), "a.pdf",
            await r.RemoveSecurityAsync(b, SamplePdfFactory.UserPassword), "b.pdf", new CompareOptions());
        Assert.Contains(result.Changes, c => c.Kind == CompareChangeKind.Inserted && c.TextB.Contains("źdźbło"));
        Assert.Empty(await host.Get<ISignatureService>().ValidateAsync(a, SamplePdfFactory.UserPassword));
    }
}

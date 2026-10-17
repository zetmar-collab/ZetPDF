using System.Security.Cryptography.X509Certificates;
using UglyToad.PdfPig;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.IntegrationTests;

/// <summary>Redakcja, szyfrowanie AES-256, oczyszczanie, podpisy – sprawdzane niezależnie (PdfPig, PDFium, surowe bajty).</summary>
public class PrivacyAndProtectionTests
{
    private const string Secret = ExtraSamples.SecretPhrase;

    // ================================================================== redakcja

    [Fact]
    public async Task Redacted_phrase_disappears_from_pdfpig_pdfium_raw_streams_metadata_and_bookmarks()
    {
        using var host = new TestHost();
        var red = host.Get<IRedactionService>();
        var src = ExtraSamples.WithLiteralText(new[] { "Jawny wiersz pierwszy", $"Haslo: {Secret} koniec", "Jawny wiersz trzeci" }, title: $"Dokument {Secret}");
        src = await host.Get<IBookmarkService>().SetBookmarksAsync(src, new[] { new BookmarkNode($"Rozdzial {Secret}", 0, BookmarkNode.NoChildren) });
        // punkt wyjścia: fraza jest w tekście, w strumieniu (po dekompresji), w tytule i w zakładce
        Assert.Contains(Secret, Inspect.PigText(src));
        Assert.Contains(Secret, await Inspect.PdfiumText(src));
        Assert.Contains($"(Haslo: {Secret} koniec) Tj", Inspect.Deep(src)); // dosłownie w zdekodowanym strumieniu treści
        Assert.Equal($"Dokument {Secret}", (await host.Get<IMetadataService>().GetAsync(src)).Title);

        var found = await red.FindAsync(src, new RedactionSearch { Phrases = new[] { Secret } });
        var hit = Assert.Single(found);
        var marked = await red.MarkAsync(src, new[] { new RedactionMark(hit.PageIndex, hit.Quads) });
        Assert.Contains((0, "/Redact"), Inspect.Annotations(marked));
        Assert.Contains(Secret, Inspect.PigText(marked)); // samo oznaczenie niczego nie usuwa

        var result = await red.ApplyAsync(marked, new RedactionApplyOptions());
        Assert.DoesNotContain(Secret, Inspect.PigText(result));
        Assert.DoesNotContain(Secret, await Inspect.PdfiumText(result));
        Assert.False(Inspect.ContainsAnywhere(result, Secret), "fraza nadal w pliku (strumienie/ciągi/metadane/zakładki)");
        Assert.DoesNotContain((0, "/Redact"), Inspect.Annotations(result));
        // reszta tekstu zostaje wyszukiwalna
        Assert.Contains("Jawnywierszpierwszy", Inspect.Squash(Inspect.PigText(result)));
        Assert.Contains("Jawnywiersztrzeci", Inspect.Squash(await Inspect.PdfiumText(result)));
        // obszar zamalowany na czarno
        var box = hit.Quads[0].Bounds;
        using var doc = await Inspect.Renderer.OpenAsync(result);
        var bmp = await doc.RenderPageAsync(0, RenderRequest.ForDpi(72));
        int x = (int)((box.Left + box.Right) / 2), y = (int)(doc.Pages[0].Size.Height - (box.Bottom + box.Top) / 2);
        int o = y * bmp.Stride + x * 4;
        Assert.True(bmp.Bgra[o] < 60 && bmp.Bgra[o + 1] < 60 && bmp.Bgra[o + 2] < 60, "środek obszaru redakcji nie jest czarny");
    }

    [Fact]
    public async Task Area_redaction_without_search_removes_text_under_the_area_only()
    {
        using var host = new TestHost();
        var red = host.Get<IRedactionService>();
        var src = ExtraSamples.WithLiteralText(new[] { "Pierwszy wiersz jawny", $"{Secret}", "Trzeci wiersz jawny" });
        // drugi wiersz: linia bazowa y = 732, czcionka 14 pt
        var area = new PdfRect(40, 726, 560, 748);
        var result = await red.ApplyAsync(await red.MarkAsync(src, new[] { new RedactionMark(0, new[] { PdfQuad.FromRect(area) }) }), new RedactionApplyOptions());
        Assert.False(Inspect.ContainsAnywhere(result, Secret));
        var t = Inspect.Squash(await Inspect.PdfiumText(result));
        Assert.Contains("Pierwszywierszjawny", t);
        Assert.Contains("Trzeciwierszjawny", t);
    }

    [Fact]
    public async Task Search_and_redact_all_sensitive_patterns_removes_every_value()
    {
        using var host = new TestHost();
        var red = host.Get<IRedactionService>();
        var s = SamplePdfFactory.WithSensitiveData();
        foreach (var (pattern, value) in new[] { (SensitiveDataPattern.Pesel, s.Pesel), (SensitiveDataPattern.Nip, s.Nip), (SensitiveDataPattern.CardNumber, s.Card), (SensitiveDataPattern.IbanPl, s.Iban) })
            Assert.True(red.Validate(pattern, value), $"{pattern} {value}");
        Assert.False(red.Validate(SensitiveDataPattern.Pesel, "44051401358")); // zła suma kontrolna
        Assert.False(red.Validate(SensitiveDataPattern.CardNumber, "4111 1111 1111 1112"));

        var found = await red.FindAsync(s.Pdf!, new RedactionSearch { Patterns = SensitiveDataPattern.All });
        foreach (var p in new[] { SensitiveDataPattern.Pesel, SensitiveDataPattern.Nip, SensitiveDataPattern.Email, SensitiveDataPattern.PhonePl, SensitiveDataPattern.CardNumber, SensitiveDataPattern.IbanPl })
            Assert.Contains(found, c => c.Pattern == p);
        var result = await red.ApplyAsync(await red.MarkAsync(s.Pdf!, found.Select(c => new RedactionMark(c.PageIndex, c.Quads)).ToList()), new RedactionApplyOptions());
        var pig = Inspect.Squash(Inspect.PigText(result));
        var pdfium = Inspect.Squash(await Inspect.PdfiumText(result));
        foreach (var v in new[] { s.Pesel, s.Nip, s.Email, s.Phone, s.Card, s.Iban })
        {
            Assert.DoesNotContain(Inspect.Squash(v), pig);
            Assert.DoesNotContain(Inspect.Squash(v), pdfium);
            Assert.False(Inspect.ContainsAnywhere(result, v), v);
        }
        Assert.Contains("Daneosobowe", pdfium);
    }

    [Fact]
    public async Task Redaction_through_session_is_undoable_and_saved_file_has_no_phrase()
    {
        using var host = new TestHost();
        var red = host.Get<IRedactionService>();
        var path = TestPaths.WriteSample(ExtraSamples.WithLiteralText(new[] { "Jawne", Secret }), "do redakcji ąę.pdf");
        var session = (await host.Documents.OpenAsync(path))!;
        var before = session.Current.GetBytes();
        await session.ExecuteAsync(new DelegateDocumentCommand("Test.Redact", async (pdf, p, ct) =>
        {
            var hits = await red.FindAsync(pdf, new RedactionSearch { Phrases = new[] { Secret } }, null, ct);
            return await red.ApplyAsync(await red.MarkAsync(pdf, hits.Select(h => new RedactionMark(h.PageIndex, h.Quads)).ToList(), ct), new RedactionApplyOptions(), p, ct);
        }));
        var outPath = Path.Combine(Path.GetDirectoryName(path)!, "po redakcji ść.pdf");
        await host.Documents.SaveAsync(session, outPath);
        Assert.False(Inspect.ContainsAnywhere(File.ReadAllBytes(outPath), Secret));
        await session.History.UndoAsync();
        Assert.Equal(before, session.Current.GetBytes());
    }

    // ================================================================== AES-256

    [Fact]
    public async Task Aes256_saved_by_document_manager_opens_in_pdfium_and_pdfpig_only_with_password()
    {
        using var host = new TestHost();
        const string user = "Hasło-otwarcia ąę";
        const string owner = "Hasło-uprawnień źż";
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(2), "do zaszyfrowania ąę.pdf");
        var session = (await host.Documents.OpenAsync(path))!;
        await session.ExecuteAsync(new SetEncryptionCommand("Core.Undo.SetEncryption",
            new PdfEncryptionSettings(user, owner, PdfEncryptionMethod.Aes256, DocumentPermissions.Print | DocumentPermissions.CopyContent)));
        var outPath = Path.Combine(Path.GetDirectoryName(path)!, "zaszyfrowany AES-256 ść.pdf");
        await host.Documents.SaveAsync(session, outPath);
        var enc = File.ReadAllBytes(outPath);

        var raw = Inspect.Latin1(enc);
        Assert.Contains("/V 5", raw);
        Assert.Contains("/R 6", raw);
        Assert.Contains("/AESV3", raw);
        Assert.DoesNotContain("gęślą", Inspect.PigTextSafe(enc));

        var r = Inspect.Renderer;
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => r.OpenAsync(enc));
        await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => r.OpenAsync(enc, "złe"));
        Assert.Contains("gęślą", await Inspect.PdfiumText(enc, user));
        Assert.Contains("gęślą", await Inspect.PdfiumText(enc, owner));
        var probe = await r.ProbeAsync(enc, user);
        Assert.Equal(PdfProbeStatus.Ok, probe.Status);
        Assert.Equal(6, probe.SecurityRevision);
        Assert.True(probe.Permissions.HasFlag(DocumentPermissions.Print));
        Assert.False(probe.Permissions.HasFlag(DocumentPermissions.ModifyContent));

        Assert.ThrowsAny<Exception>(() => { using var _ = PigDocument.Open(enc); _.GetPage(1); });
        Assert.Contains("gęślą", Inspect.PigText(enc, user));
        Assert.Contains("gęślą", Inspect.PigText(enc, owner));

        // Usuń zabezpieczenia (cofalne) → zapis bez szyfrowania
        await session.ExecuteAsync(new SetEncryptionCommand("Core.Undo.RemoveEncryption", null));
        var plainPath = Path.Combine(Path.GetDirectoryName(path)!, "odszyfrowany ź.pdf");
        await host.Documents.SaveAsync(session, plainPath);
        var plain = File.ReadAllBytes(plainPath);
        Assert.False(PdfSecurityHelper.IsEncrypted(plain));
        Assert.Contains("gęślą", Inspect.PigText(plain));
        await session.History.UndoAsync();
        Assert.Equal(PdfEncryptionMethod.Aes256, session.Current.Encryption!.Method);
    }

    [Fact]
    public async Task Reopened_aes256_document_with_user_password_is_resaved_with_same_user_password_and_permissions()
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "użytkownik ą.pdf");
        var session = (await host.Documents.OpenAsync(path, SamplePdfFactory.UserPassword))!;
        var note = Operations.Get("Annot.Note");
        await session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => note.Run(host, pdf), DocumentChangeKind.Annotations, note.Permission));
        await host.Documents.SaveAsync(session);
        var saved = File.ReadAllBytes(path);
        Assert.Contains("/AESV3", Inspect.Latin1(saved));
        Assert.Contains("gęślą", await Inspect.PdfiumText(saved, SamplePdfFactory.UserPassword));
        var p = await Inspect.Renderer.ProbeAsync(saved, SamplePdfFactory.UserPassword);
        Assert.False(p.Permissions.HasFlag(DocumentPermissions.ModifyContent));
        // stare hasło właściciela przestaje działać (D-08: nowe losowe), więc otwarcie nim jest odrzucane
        await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => Inspect.Renderer.OpenAsync(saved, SamplePdfFactory.OwnerPassword));
    }

    // ================================================================== oczyszczanie

    [Fact]
    public async Task Sanitize_removes_info_xmp_javascript_attachments_and_comments_from_the_file()
    {
        using var host = new TestHost();
        var src = ExtraSamples.WithEverythingHidden();
        // punkt wyjścia
        foreach (var marker in new[] { "Tajny tytuł", "Autor Tajny", ExtraSamples.XmpSecret, ExtraSamples.JsMarker, "app.alert", ExtraSamples.AttachmentMarker, "zażółć" })
            Assert.True(Inspect.ContainsAnywhere(src, marker), "próbka bez " + marker);
        Assert.Equal(2, (await host.Get<IAttachmentService>().GetAttachmentsAsync(src)).Count);

        var res = await host.Get<ISanitizeService>().SanitizeAsync(src, new SanitizeOptions());
        var pdf = res.Pdf;
        Assert.True(res.MetadataEntriesRemoved > 0);
        Assert.True(res.ScriptsRemoved >= 3, $"usunięte skrypty: {res.ScriptsRemoved}");
        Assert.True(res.AttachmentsRemoved >= 2, $"usunięte załączniki: {res.AttachmentsRemoved}");
        Assert.True(res.CommentsRemoved >= 1);

        foreach (var marker in new[] { "Tajny tytuł", "Autor Tajny", "Temat poufny", ExtraSamples.XmpSecret, ExtraSamples.JsMarker, "app.alert", ExtraSamples.AttachmentMarker, "Treść załącznika", "komentarz do usunięcia" })
            Assert.False(Inspect.ContainsAnywhere(pdf, marker), "po oczyszczeniu nadal: " + marker);
        var raw = Inspect.Deep(pdf);
        Assert.DoesNotContain("/JavaScript", raw);
        Assert.DoesNotContain("/EmbeddedFiles", raw);
        Assert.DoesNotContain("/OpenAction", Inspect.Read(pdf, d => d.Internals.Catalog.Elements.ToString() ?? ""));
        Assert.DoesNotContain("/AA", Inspect.Read(pdf, d => d.Pages[0].Elements.ToString() ?? ""));
        Assert.DoesNotContain(Inspect.Annotations(pdf), a => a.Subtype is "/FileAttachment" or "/Text");
        Assert.Empty(await host.Get<IAttachmentService>().GetAttachmentsAsync(pdf));
        using (var pig = PigDocument.Open(pdf))
        {
            Assert.True(string.IsNullOrEmpty(pig.Information.Title));
            Assert.True(string.IsNullOrEmpty(pig.Information.Author));
            Assert.True(string.IsNullOrEmpty(pig.Information.Subject));
            Assert.True(string.IsNullOrEmpty(pig.Information.Keywords));
        }
        // treść zostaje
        Assert.Contains("gęślą", await Inspect.PdfiumText(pdf));
    }

    [Fact]
    public async Task Sanitize_respects_options_metadata_only_keeps_scripts_and_attachments()
    {
        using var host = new TestHost();
        var res = await host.Get<ISanitizeService>().SanitizeAsync(ExtraSamples.WithEverythingHidden(),
            new SanitizeOptions { Metadata = true, Comments = false, Attachments = false, JavaScript = false, HiddenLayers = false, ExternalLinks = false, Thumbnails = false });
        Assert.False(Inspect.ContainsAnywhere(res.Pdf, "Tajny tytuł"));
        Assert.False(Inspect.ContainsAnywhere(res.Pdf, ExtraSamples.XmpSecret));
        Assert.True(Inspect.ContainsAnywhere(res.Pdf, "app.alert"));
        Assert.Equal(2, (await host.Get<IAttachmentService>().GetAttachmentsAsync(res.Pdf)).Count);
    }

    // ================================================================== podpis cyfrowy

    private static async Task<X509Certificate2> NewIdAsync(TestHost host)
    {
        var pfx = await host.Get<ISignatureService>().CreateSelfSignedIdAsync(new DigitalIdRequest("Zenon Żółtowski", "Firma Ąę", "zenon@example.com", 1, "pfx-hasło-ź"));
        Assert.True(File.Exists(pfx));
        return new X509Certificate2(pfx, "pfx-hasło-ź", X509KeyStorageFlags.EphemeralKeySet);
    }

    private static SignRequest Request(X509Certificate2 cert, PdfRect rect = default) => new()
    {
        Certificate = new SigningCertificate(CertificateSource.PfxFile, cert),
        PageIndex = 0,
        Rect = rect,
        Reason = "Akceptuję – źdźbło",
        Location = "Łódź",
    };

    [Fact]
    public async Task Sign_saved_document_validate_and_detect_modifications()
    {
        using var host = new TestHost();
        using var cert = await NewIdAsync(host);
        var sig = host.Get<ISignatureService>();
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(2), "do podpisu ąę.pdf");
        var session = (await host.Documents.OpenAsync(path))!;
        var finalBytes = await host.Documents.GetBytesForSaveAsync(session);
        var signed = await sig.SignAsync(finalBytes, Request(cert, new PdfRect(72, 600, 300, 680)), null);
        var signedPath = Path.Combine(Path.GetDirectoryName(path)!, "podpisany źdźbło.pdf");
        File.WriteAllBytes(signedPath, signed);

        var r = Assert.Single(await sig.ValidateAsync(File.ReadAllBytes(signedPath), null));
        Assert.True(r.IntegrityOk);
        Assert.True(r.CoversWholeDocument);
        Assert.True(r.CertificateValidAtSigningTime);
        Assert.Contains(r.Status, new[] { SignatureStatus.Valid, SignatureStatus.UnknownSigner });
        Assert.Contains("Żółtowski", r.SignerName);
        Assert.Equal(1, Inspect.Read(signed, _ => 1)); // PDFsharp czyta podpisany plik
        Assert.Equal(2, await Inspect.PdfiumPageCount(signed));
        Assert.Contains("/ByteRange", Inspect.Latin1(signed));

        // 1) zmiana bajtu w podpisanym zakresie → Invalid
        var text = Inspect.Latin1(signed);
        int at = text.IndexOf("/Type /Catalog", StringComparison.Ordinal);
        if (at < 0) at = text.IndexOf("/Type/Catalog", StringComparison.Ordinal);
        Assert.True(at > 0);
        var tampered = (byte[])signed.Clone();
        tampered[at + 1] = (byte)'t';
        var bad = Assert.Single(await sig.ValidateAsync(tampered, null));
        Assert.False(bad.IntegrityOk);
        Assert.Equal(SignatureStatus.Invalid, bad.Status);

        // 2) dopisanie rewizji po podpisie → nie obejmuje całego dokumentu
        var appended = signed.Concat(System.Text.Encoding.ASCII.GetBytes("\n% dopisana rewizja\n")).ToArray();
        var later = Assert.Single(await sig.ValidateAsync(appended, null));
        Assert.True(later.IntegrityOk);
        Assert.False(later.CoversWholeDocument);
        Assert.NotEqual(SignatureStatus.Valid, later.Status);

        // 3) edycja w ZetPDF (pełny zapis) unieważnia podpis
        var opened = (await host.Documents.OpenAsync(signedPath))!;
        var note = Operations.Get("Annot.Note");
        await opened.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => note.Run(host, pdf), DocumentChangeKind.Annotations, note.Permission));
        await host.Documents.SaveAsync(opened);
        var edited = await sig.ValidateAsync(File.ReadAllBytes(signedPath), null);
        Assert.All(edited, e => Assert.False(e.IntegrityOk && e.CoversWholeDocument && e.Status is SignatureStatus.Valid or SignatureStatus.UnknownSigner,
            "po pełnym zapisie podpis nadal raportowany jako ważny"));
    }

    [Fact]
    public async Task Status_of_self_signed_signature_reports_modification_after_signing()
    {
        // ZetPDF tworzy identyfikatory z podpisem własnym (Identyfikatory cyfrowe), więc to najczęstszy przypadek.
        using var host = new TestHost();
        using var cert = await NewIdAsync(host);
        var sig = host.Get<ISignatureService>();
        var once = await sig.SignAsync(SamplePdfFactory.Text(1), Request(cert, new PdfRect(72, 600, 300, 680)), null);
        var twice = await sig.SignAsync(once, Request(cert, new PdfRect(72, 400, 300, 480)), null); // prawdziwa późniejsza rewizja
        var first = (await sig.ValidateAsync(twice, null)).Single(r => !r.CoversWholeDocument);
        Assert.True(first.IntegrityOk);
        Assert.Equal(SignatureStatus.ValidButModifiedAfter, first.Status);
    }

    [Fact]
    public async Task Sign_aes256_document_from_session_keeps_encryption_and_validates_with_password()
    {
        using var host = new TestHost();
        using var cert = await NewIdAsync(host);
        var sig = host.Get<ISignatureService>();
        var path = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "szyfrowany do podpisu ą.pdf");
        var session = (await host.Documents.OpenAsync(path, SamplePdfFactory.OwnerPassword))!;
        var final = await host.Documents.GetBytesForSaveAsync(session);
        Assert.True(PdfSecurityHelper.IsEncrypted(final));
        var signed = await sig.SignAsync(final, Request(cert, new PdfRect(72, 500, 250, 560)), SamplePdfFactory.OwnerPassword);
        Assert.True(PdfSecurityHelper.IsEncrypted(signed));
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => Inspect.Renderer.OpenAsync(signed));
        Assert.Contains("gęślą", await Inspect.PdfiumText(signed, SamplePdfFactory.OwnerPassword));
        var r = Assert.Single(await sig.ValidateAsync(signed, SamplePdfFactory.OwnerPassword));
        Assert.True(r.IntegrityOk);
        Assert.True(r.CoversWholeDocument);
    }

    [Fact]
    public async Task Visible_signature_is_drawn_on_page()
    {
        using var host = new TestHost();
        using var cert = await NewIdAsync(host);
        var src = SamplePdfFactory.Text(1);
        var rect = new PdfRect(300, 100, 560, 200);
        var signed = await host.Get<ISignatureService>().SignAsync(src, Request(cert, rect), null);
        using var doc = await Inspect.Renderer.OpenAsync(signed);
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(1.0, Clip: new PixelRect(300, (int)(doc.Pages[0].Size.Height - 200), 260, 100)));
        var (_, _, dark) = Inspect.Stats(bmp);
        Assert.True(dark > 0.005, $"widoczny podpis pusty (ciemne piksele {dark:P2})");
    }
}

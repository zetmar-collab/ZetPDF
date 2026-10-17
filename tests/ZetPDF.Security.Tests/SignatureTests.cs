using System.Security.Cryptography.X509Certificates;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using UglyToad.PdfPig;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Security.Tests;

public class SignatureTests
{
    private static readonly PdfRect Box = new(72, 600, 300, 680);

    [Fact]
    public async Task Sign_then_validate_ok_and_one_byte_change_is_detected()
    {
        using var cert = TestCerts.Signer();
        var svc = TestEnv.Signatures();
        var signed = await svc.SignAsync(SamplePdfFactory.Text(2), TestEnv.Request(cert, Box), null);

        var results = await svc.ValidateAsync(signed, null);
        var r = Assert.Single(results);
        Assert.True(r.IntegrityOk);
        Assert.True(r.CoversWholeDocument);
        Assert.True(r.CertificateValidAtSigningTime);
        Assert.False(r.ChainTrusted); // podpis własny – niezaufany w magazynie Windows
        Assert.Equal(SignatureStatus.UnknownSigner, r.Status);
        Assert.Equal(0, r.PageIndex);
        Assert.Equal(Box.Left, r.Rect.Left, 3);
        Assert.Contains("Zażółć", r.SignerName);
        Assert.NotNull(r.SigningTime);

        // PDFium otwiera podpisany plik
        using (var d = await new PdfiumRenderer().OpenAsync(signed)) Assert.Equal(2, d.PageCount);

        // zmiana jednego bajtu w zakresie objętym podpisem (w treści strony)
        var tampered = (byte[])signed.Clone();
        int idx = IndexOf(tampered, "%PDF-1."u8.ToArray());
        Assert.Equal(0, idx);
        tampered[7] = tampered[7] == (byte)'7' ? (byte)'6' : (byte)'7'; // wersja w nagłówku – wewnątrz /ByteRange
        var bad = Assert.Single(await svc.ValidateAsync(tampered, null));
        Assert.False(bad.IntegrityOk);
        Assert.Equal(SignatureStatus.Invalid, bad.Status);
    }

    [Fact]
    public async Task Signing_is_incremental_and_keeps_earlier_signature_valid()
    {
        using var cert = TestCerts.Signer();
        var svc = TestEnv.Signatures();
        var original = SamplePdfFactory.Text(1);
        var once = await svc.SignAsync(original, TestEnv.Request(cert, Box), null);
        Assert.True(once.AsSpan(0, original.Length).SequenceEqual(original)); // oryginalne bajty bez zmian

        var twice = await svc.SignAsync(once, TestEnv.Request(cert, new PdfRect(72, 400, 300, 480)), null);
        Assert.True(twice.AsSpan(0, once.Length).SequenceEqual(once));
        var results = await svc.ValidateAsync(twice, null);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IntegrityOk));
        Assert.Single(results, r => r.CoversWholeDocument);
        Assert.Single(results, r => !r.CoversWholeDocument); // pierwszy: dokument zmieniony po podpisaniu
        Assert.Equal(2, results.Select(r => r.FieldName).Distinct().Count());
    }

    [Fact]
    public async Task Sign_aes256_document_keeps_encryption()
    {
        using var cert = TestCerts.Signer();
        var sec = new SecurityService();
        var enc = await sec.EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings("user-ą", "owner-ę", PdfEncryptionMethod.Aes256, DocumentPermissions.Print | DocumentPermissions.FillForms));
        var svc = TestEnv.Signatures();
        var signed = await svc.SignAsync(enc, TestEnv.Request(cert, Box), "owner-ę");

        var pdfium = new PdfiumRenderer();
        await Assert.ThrowsAnyAsync<PdfPasswordRequiredException>(() => pdfium.OpenAsync(signed));
        using (var d = await pdfium.OpenAsync(signed, "user-ą")) Assert.Equal(1, d.PageCount);
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(signed, new ParsingOptions { Password = "user-ą" }))
            Assert.Equal(1, pig.NumberOfPages);

        var r = Assert.Single(await svc.ValidateAsync(signed, "user-ą"));
        Assert.True(r.IntegrityOk);
        Assert.True(r.CoversWholeDocument);
        Assert.Contains("Zażółć", r.SignerName);
        var info = await sec.AnalyzeAsync(signed, "user-ą");
        Assert.Equal(PdfEncryptionMethod.Aes256, info.Method);

        // także z hasłem użytkownika i AES-128
        var enc128 = await sec.EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings("u", "o", PdfEncryptionMethod.Aes128));
        var signed128 = await svc.SignAsync(enc128, TestEnv.Request(cert, Box), "u");
        using (var d = await pdfium.OpenAsync(signed128, "u")) Assert.Equal(1, d.PageCount);
        Assert.True(Assert.Single(await svc.ValidateAsync(signed128, "u")).IntegrityOk);
    }

    [Fact]
    public async Task Timestamp_from_fake_tsa_is_embedded_and_document_timestamp_validates()
    {
        using var cert = TestCerts.Signer();
        var tsa = new FakeTimestampClient();
        var svc = TestEnv.Signatures(tsa);
        var signed = await svc.SignAsync(SamplePdfFactory.Text(1), TestEnv.Request(cert, Box, tsa: "http://tsa.invalid/"), null);
        Assert.Equal(1, tsa.Calls);
        var r = Assert.Single(await svc.ValidateAsync(signed, null));
        Assert.True(r.IntegrityOk);
        Assert.True(r.HasTimestamp);
        Assert.Equal(tsa.Time, r.TimestampTime);

        var stamped = await svc.TimestampDocumentAsync(signed, "http://tsa.invalid/", null);
        Assert.Equal(2, tsa.Calls);
        var all = await svc.ValidateAsync(stamped, null);
        Assert.Equal(2, all.Count);
        var doc = Assert.Single(all, x => x.FieldName.StartsWith("Timestamp", StringComparison.Ordinal));
        Assert.True(doc.IntegrityOk);
        Assert.True(doc.HasTimestamp);
        Assert.True(doc.CoversWholeDocument);
    }

    [Fact]
    public async Task No_tsa_address_means_no_network_call()
    {
        using var cert = TestCerts.Signer();
        var tsa = new FakeTimestampClient();
        var svc = TestEnv.Signatures(tsa);
        var signed = await svc.SignAsync(SamplePdfFactory.Text(1), TestEnv.Request(cert, Box, tsa: "  "), null);
        Assert.Equal(0, tsa.Calls);
        Assert.False(Assert.Single(await svc.ValidateAsync(signed, null)).HasTimestamp);
    }

    [Fact]
    public async Task Sign_existing_empty_signature_field_and_invisible_signature()
    {
        using var cert = TestCerts.Signer();
        var svc = TestEnv.Signatures();
        var withField = WithEmptySignatureField("Podpis klienta", new PdfRect(100, 100, 300, 160));
        var signed = await svc.SignAsync(withField, TestEnv.Request(cert, field: "Podpis klienta"), null);
        var r = Assert.Single(await svc.ValidateAsync(signed, null));
        Assert.Equal("Podpis klienta", r.FieldName);
        Assert.True(r.IntegrityOk);
        Assert.Equal(100, r.Rect.Left, 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SignAsync(signed, TestEnv.Request(cert, field: "Podpis klienta"), null));

        var invisible = await svc.SignAsync(SamplePdfFactory.Text(1), TestEnv.Request(cert), null);
        var ri = Assert.Single(await svc.ValidateAsync(invisible, null));
        Assert.True(ri.IntegrityOk);
        Assert.True(ri.Rect.IsEmpty);
    }

    [Fact]
    public async Task Signature_with_image_on_rotated_page_renders_in_pdfium()
    {
        using var cert = TestCerts.Signer();
        var svc = TestEnv.Signatures();
        var pdf = PdfSharpEdit.Modify(SamplePdfFactory.Text(1), d => d.Pages[0].Rotate = 90);
        var png = TinyPng();
        var req = TestEnv.Request(cert, Box) with { Appearance = new SignatureAppearance { Image = png } };
        var signed = await svc.SignAsync(pdf, req, null);
        Assert.True(Assert.Single(await svc.ValidateAsync(signed, null)).IntegrityOk);
        using var d = await new PdfiumRenderer().OpenAsync(signed);
        var bmp = await d.RenderPageAsync(0, new RenderRequest(1.0));
        Assert.True(bmp.Width > 0);
    }

    [Fact]
    public async Task Validates_signature_created_by_pdfsharp()
    {
        using var cert = TestCerts.Signer();
        PdfSharpEdit.EnsureInitialized();
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(SamplePdfFactory.Text(1)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var options = new PdfSharp.Pdf.Signatures.DigitalSignatureOptions { Reason = "test", Rectangle = new PdfSharp.Drawing.XRect(50, 50, 200, 50) };
        PdfSharp.Pdf.Signatures.DigitalSignatureHandler.ForDocument(doc, new PdfSharp.Pdf.Signatures.PdfSharpDefaultSigner(cert, PdfSharp.Pdf.Signatures.PdfMessageDigestType.SHA256, null), options);
        var ms = new MemoryStream();
        doc.Save(ms, false);
        var r = Assert.Single(await TestEnv.Signatures().ValidateAsync(ms.ToArray(), null));
        Assert.True(r.IntegrityOk);
        Assert.True(r.CoversWholeDocument);
    }

    [Fact]
    public async Task Created_digital_id_can_sign()
    {
        var paths = TestEnv.Paths();
        var svc = new SignatureService(paths, new FakeTimestampClient());
        var pfxPath = await svc.CreateSelfSignedIdAsync(new DigitalIdRequest("Anna Żółkiewska", "Firma ąę", "anna@example.com", 2, "hasło-PFX"));
        Assert.StartsWith(paths.DigitalIdsDirectory, pfxPath);
        Assert.True(File.Exists(pfxPath));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => SignatureService.LoadPfx(pfxPath, "złe"));
        using var cert = SignatureService.LoadPfx(pfxPath, "hasło-PFX");
        Assert.True(cert.HasPrivateKey);
        Assert.Contains("Żółkiewska", cert.Subject);
        var signed = await svc.SignAsync(SamplePdfFactory.Text(1), TestEnv.Request(cert, Box), null);
        Assert.True(Assert.Single(await svc.ValidateAsync(signed, null)).IntegrityOk);
    }

    [Fact]
    public async Task Unsigned_document_has_no_results()
    {
        Assert.Empty(await TestEnv.Signatures().ValidateAsync(SamplePdfFactory.Text(1), null));
        Assert.Empty(await TestEnv.Signatures().ValidateAsync(SamplePdfFactory.WithForm(), null));
    }

    // ------------------------------------------------------------------ pomocnicze

    internal static byte[] WithEmptySignatureField(string name, PdfRect rect)
    {
        return PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            var page = doc.Pages[0];
            var widget = new PdfDictionary(doc);
            widget.Elements["/Type"] = new PdfName("/Annot");
            widget.Elements["/Subtype"] = new PdfName("/Widget");
            widget.Elements["/FT"] = new PdfName("/Sig");
            widget.Elements["/T"] = new PdfString(name);
            widget.Elements["/F"] = new PdfInteger(4);
            widget.Elements["/Rect"] = new PdfArray(doc, new PdfReal(rect.Left), new PdfReal(rect.Bottom), new PdfReal(rect.Right), new PdfReal(rect.Top));
            widget.Elements["/P"] = page.Reference;
            doc.Internals.AddObject(widget);
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
            annots.Elements.Add(widget.Reference!);
            var acro = new PdfDictionary(doc);
            acro.Elements["/Fields"] = new PdfArray(doc, widget.Reference!);
            doc.Internals.AddObject(acro);
            doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;
        });
    }

    private static byte[] TinyPng()
    {
        return StaHelper(() =>
        {
            var px = new byte[16 * 16 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 50; px[i + 2] = 20; px[i + 3] = (byte)(i % 8 == 0 ? 128 : 255); }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 64);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        });
    }

    private static T StaHelper<T>(Func<T> f)
    {
        T r = default!;
        var t = new Thread(() => r = f());
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return r;
    }

    private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);
}

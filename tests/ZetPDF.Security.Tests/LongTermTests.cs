using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Security.Pdf;
using ZetPDF.Security.Signatures;
using ZetPDF.TestData;
using BcX509 = Org.BouncyCastle.X509;

namespace ZetPDF.Security.Tests;

/// <summary>Mini-CA w pamięci: korzeń, pośredni, podpisujący z adresami AIA i CDP, responder delegowany. Bez sieci.</summary>
internal sealed class MiniCa
{
    public const string OcspUrl = "http://ocsp.ca.invalid/", CrlUrl = "http://crl.ca.invalid/int.crl", IssuerUrl = "http://aia.ca.invalid/int.cer";
    public const string RootOcspUrl = "http://ocsp.ca.invalid/root", RootCertUrl = "http://aia.ca.invalid/root.cer";
    public readonly RSA RootKey = RSA.Create(2048), IntermediateKey = RSA.Create(2048), LeafKey = RSA.Create(2048), OtherKey = RSA.Create(2048);
    public readonly X509Certificate2 Root, Intermediate, Leaf, Other;

    public MiniCa(string? leafOcsp = OcspUrl, string? leafCrl = CrlUrl, bool withIssuerUrl = false)
    {
        var now = DateTimeOffset.UtcNow;
        var rootReq = new CertificateRequest("CN=ZetPDF Test Root, O=ZetPDF Test", RootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = rootReq.CreateSelfSigned(now.AddDays(-30), now.AddYears(5));

        var intReq = new CertificateRequest("CN=ZetPDF Test Intermediate, O=ZetPDF Test", IntermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intReq.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(new[] { RootOcspUrl }, new[] { RootCertUrl }));
        intReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intReq.PublicKey, false));
        Intermediate = intReq.Create(Root, now.AddDays(-20), now.AddYears(3), new byte[] { 1, 2, 3, 4 });

        var leafReq = new CertificateRequest("CN=Jan Kowalski, O=ZetPDF Test", LeafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var ocsp = leafOcsp is null ? Array.Empty<string>() : new[] { leafOcsp };
        var issuers = withIssuerUrl ? new[] { IssuerUrl } : Array.Empty<string>();
        if (ocsp.Length + issuers.Length > 0) leafReq.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(ocsp, issuers));
        if (leafCrl is not null) leafReq.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { leafCrl }));
        using var leafPublic = leafReq.Create(Intermediate.SubjectName, X509SignatureGenerator.CreateForRSA(IntermediateKey, RSASignaturePadding.Pkcs1), now.AddDays(-10), now.AddYears(1), new byte[] { 9, 8, 7, 6 });
        using var withKey = leafPublic.CopyWithPrivateKey(LeafKey);
        Leaf = new X509Certificate2(withKey.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.EphemeralKeySet);
        Other = new CertificateRequest("CN=Ktos Inny", OtherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(now.AddDays(-5), now.AddYears(1));
    }

    private static CertificateID Id(byte[] request) => new OcspReq(request).GetRequestList()[0].GetCertID();

    private static Asn1SignatureFactory Factory(RSA key) =>
        new("SHA256WITHRSA", DotNetUtilities.GetRsaKeyPair(key.ExportParameters(true)).Private);

    private static byte[] Response(byte[] request, RSA key, BcX509.X509Certificate responderId, CertificateStatus? status, BcX509.X509Certificate[]? chain = null, DateTime? thisUpdate = null, DateTime? nextUpdate = null)
    {
        var id = Id(request);
        var gen = new BasicOcspRespGenerator(new RespID(responderId.SubjectDN));
        gen.AddResponse(id, status, thisUpdate ?? DateTime.UtcNow.AddMinutes(-1), nextUpdate ?? DateTime.UtcNow.AddDays(7), null);
        var basic = gen.Generate(Factory(key), chain, DateTime.UtcNow);
        return new OCSPRespGenerator().Generate(OCSPRespGenerator.Successful, basic).GetEncoded();
    }

    private static BcX509.X509Certificate Bc(X509Certificate2 c) => new BcX509.X509CertificateParser().ReadCertificate(c.RawData);

    /// <summary>Odpowiedź podpisana przez wystawcę certyfikatu.</summary>
    public byte[] OcspBy(byte[] request, CertificateStatus? status) => Response(request, IntermediateKey, Bc(Intermediate), status);

    /// <summary>Odpowiedź korzenia o certyfikacie pośrednim.</summary>
    public byte[] OcspRoot(byte[] request) => Response(request, RootKey, Bc(Root), CertificateStatus.Good);

    public byte[] OcspForged(byte[] request) => Response(request, OtherKey, Bc(Other), CertificateStatus.Good);

    /// <summary>Responder delegowany przez wystawcę; bez EKU OCSP Signing odpowiedź nie może przejść.</summary>
    public byte[] OcspDelegated(byte[] request, bool withEku)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Responder, O=ZetPDF Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (withEku) req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.9") }, false));
        using var responder = req.Create(Intermediate.SubjectName, X509SignatureGenerator.CreateForRSA(IntermediateKey, RSASignaturePadding.Pkcs1), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), new byte[] { 5, 5, 5 });
        var bc = Bc(responder);
        return Response(request, key, bc, CertificateStatus.Good, new[] { bc });
    }

    public byte[] OcspForOtherSerial(byte[] request)
    {
        var id = Id(request);
        var other = CertificateID.DeriveCertificateID(id, new Org.BouncyCastle.Math.BigInteger("123456"));
        var gen = new BasicOcspRespGenerator(new RespID(Bc(Intermediate).SubjectDN));
        gen.AddResponse(other, CertificateStatus.Good);
        return new OCSPRespGenerator().Generate(OCSPRespGenerator.Successful, gen.Generate(Factory(IntermediateKey), null, DateTime.UtcNow)).GetEncoded();
    }

    public byte[] OcspOld(byte[] request) => Response(request, IntermediateKey, Bc(Intermediate), CertificateStatus.Good, null, DateTime.UtcNow.AddDays(-400), DateTime.UtcNow.AddDays(-393));

    public byte[] Crl(RSA? key = null, X509Certificate2? issuer = null, byte[]? revokedSerial = null, DateTimeOffset? thisUpdate = null, DateTimeOffset? next = null)
    {
        var builder = new CertificateRevocationListBuilder();
        if (revokedSerial is not null) builder.AddEntry(revokedSerial, DateTimeOffset.UtcNow.AddMinutes(-5));
        var gen = X509SignatureGenerator.CreateForRSA(key ?? IntermediateKey, RSASignaturePadding.Pkcs1);
        return builder.Build((issuer ?? Intermediate).SubjectName, gen, 1, next ?? DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer ?? Intermediate, true, false), thisUpdate ?? DateTimeOffset.UtcNow.AddMinutes(-1));
    }
}

/// <summary>Atrapa transportu: odpowiedzi z funkcji, licznik wywołań, żadnej sieci.</summary>
internal sealed class FakeRevocationTransport : IRevocationTransport
{
    /// <summary>Infrastruktura CA: wystawcy i stan certyfikatu pośredniego; testy zajmują się certyfikatem podpisującym.</summary>
    public required MiniCa Ca { get; init; }
    public Func<Uri, byte[], byte[]>? Ocsp { get; init; }
    public Func<Uri, byte[]>? Get { get; init; }
    public List<string> Calls { get; } = new();

    public List<bool> AllowLocalFlags { get; } = new();

    public Task<byte[]> GetAsync(Uri url, bool allowLocalNetwork, CancellationToken ct)
    {
        AllowLocalFlags.Add(allowLocalNetwork);
        Calls.Add("GET " + url.AbsoluteUri);
        if (url.AbsoluteUri == MiniCa.RootCertUrl) return Task.FromResult(Ca.Root.RawData);
        return Get is null ? throw new HttpRequestException("brak") : Task.FromResult(Get(url));
    }

    public Task<byte[]> PostOcspAsync(Uri url, byte[] request, bool allowLocalNetwork, CancellationToken ct)
    {
        AllowLocalFlags.Add(allowLocalNetwork);
        Calls.Add("POST " + url.AbsoluteUri);
        if (url.AbsoluteUri == MiniCa.RootOcspUrl) return Task.FromResult(Ca.OcspRoot(request));
        return Ocsp is null ? throw new HttpRequestException("brak") : Task.FromResult(Ocsp(url, request));
    }
}

public class LongTermTests
{
    private const string Tsa = "http://tsa.invalid/";
    private static readonly PdfRect Box = new(72, 600, 300, 680);
    private static readonly LongTermConnectionPlan NoExtra = LongTermConnectionPlan.Of(new[] { MiniCa.OcspUrl, MiniCa.CrlUrl, MiniCa.IssuerUrl, MiniCa.RootOcspUrl, MiniCa.RootCertUrl });

    private static async Task<byte[]> SignedAsync(MiniCa ca, SignatureService? svc = null, byte[]? pdf = null, string? password = null)
    {
        svc ??= TestEnv.Signatures();
        var request = TestEnv.Request(ca.Leaf, Box, tsa: Tsa) with { Pades = true };
        // pośredni w podpisie jak z magazynu Windows nie przyjdzie: dokładamy go przez DSS w scenariuszu z AIA, a tu przez pulę transportu
        return await svc.SignAsync(pdf ?? SamplePdfFactory.Text(1), request, password);
    }

    private static FakeRevocationTransport Good(MiniCa ca) => new()
    {
        Ca = ca,
        Ocsp = (_, request) => ca.OcspBy(request, CertificateStatus.Good),
        Get = url => url.AbsoluteUri == MiniCa.IssuerUrl ? ca.Intermediate.RawData : ca.Crl(),
    };

    private static MiniCa CaWithIssuerUrl() => new(withIssuerUrl: true);

    private static SignatureValidationResult First(IReadOnlyList<SignatureValidationResult> results) => results[0];

    [Fact]
    public async Task Dss_is_added_and_earlier_signature_keeps_integrity_and_uses_dss_status()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var signed = await SignedAsync(ca, svc);
        var before = First(await svc.ValidateAsync(signed, null));
        Assert.Equal(SignatureRevocationStatus.NotChecked, before.RevocationStatus);
        Assert.Equal("B-T", before.BaselineLevel);

        var fake = Good(ca);
        var result = await new LongTermService(fake).AddLongTermDataAsync(signed, null, NoExtra);
        Assert.True(result.Pdf.Length > signed.Length);
        Assert.Equal(signed, result.Pdf.AsSpan(0, signed.Length).ToArray()); // tylko dopisanie
        Assert.Equal(SignatureRevocationStatus.Good, result.Status);
        Assert.Equal(2, result.OcspResponses); // podpisujący i pośredni
        Assert.Contains("POST " + MiniCa.OcspUrl, fake.Calls);
        Assert.Empty(result.Problems);

        int callsBefore = fake.Calls.Count;
        var after = First(await svc.ValidateAsync(result.Pdf, null));
        Assert.Equal(callsBefore, fake.Calls.Count); // weryfikacja nie łączy się z siecią
        Assert.True(after.IntegrityOk);
        Assert.False(after.CoversWholeDocument);
        Assert.True(after.ModifiedOnlyByLongTermData);
        Assert.True(after.HasDss);
        Assert.Equal(SignatureRevocationStatus.Good, after.RevocationStatus);
        Assert.Equal("B-LT", after.BaselineLevel);
        Assert.NotEqual(SignatureStatus.ValidButModifiedAfter, after.Status);
        Assert.NotEqual(SignatureStatus.Invalid, after.Status);

        var doc = CosDocument.Open(result.Pdf, null);
        var dss = DssData.Read(doc);
        Assert.Contains(dss.Certificates, c => c.AsSpan().SequenceEqual(ca.Intermediate.RawData));
        Assert.Equal(2, dss.Ocsp.Count);
        var vri = (CosDictionary)doc.Resolve(doc.ResolveDict(doc.Catalog["DSS"])!["VRI"])!;
        Assert.Single(vri.Entries);
        Assert.Matches("^[0-9A-F]{40}$", vri.Entries[0].Key);
    }

    [Fact]
    public async Task Archive_timestamp_gives_lta_and_earlier_signature_stays_clean()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var withDss = (await new LongTermService(Good(ca)).AddLongTermDataAsync(await SignedAsync(ca, svc), null, NoExtra)).Pdf;
        var stamped = await svc.TimestampDocumentAsync(withDss, Tsa, null);
        var results = await svc.ValidateAsync(stamped, null);
        var sig = results.Single(r => r.BaselineLevel.Length > 0);
        Assert.Equal("B-LTA", sig.BaselineLevel);
        Assert.True(sig.ModifiedOnlyByLongTermData);
        Assert.True(sig.IntegrityOk);
        Assert.NotEqual(SignatureStatus.ValidButModifiedAfter, sig.Status);
        Assert.Contains(results, r => r.HasTimestamp && r.CoversWholeDocument);
    }

    [Fact]
    public async Task Real_change_after_signature_is_still_reported()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var withDss = (await new LongTermService(Good(ca)).AddLongTermDataAsync(await SignedAsync(ca, svc), null, NoExtra)).Pdf;
        // inna zmiana niż dane długoterminowe: podmiana treści strony przyrostowo
        var doc = CosDocument.Open(withDss, null);
        var upd = new IncrementalUpdate(doc);
        var page = doc.GetPageReferences()[0];
        var dict = (CosDictionary)doc.ResolveDict(page)!.Clone();
        dict["Rotate"] = new CosNumber(90L);
        upd.Set(page, dict);
        var r = First(await svc.ValidateAsync(upd.Write(), null));
        Assert.False(r.ModifiedOnlyByLongTermData);
        Assert.Equal(SignatureStatus.ValidButModifiedAfter, r.Status);
    }

    [Fact]
    public async Task Change_of_another_catalog_key_is_not_excused()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var signed = await SignedAsync(ca, svc);
        var doc = CosDocument.Open(signed, null);
        var upd = new IncrementalUpdate(doc);
        var catalog = (CosDictionary)doc.Catalog.Clone();
        catalog["DSS"] = new CosDictionary();
        catalog["OpenAction"] = new CosDictionary { ["S"] = new CosName("Named") };
        upd.Set(doc.CatalogReference, catalog);
        Assert.False(First(await svc.ValidateAsync(upd.Write(), null)).ModifiedOnlyByLongTermData);
    }

    [Fact]
    public async Task Revoked_certificate_makes_signature_invalid_offline()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var fake = new FakeRevocationTransport
        {
            Ca = ca,
            Ocsp = (_, request) => ca.OcspBy(request, new RevokedStatus(DateTime.UtcNow.AddDays(-2), 0)),
            Get = url => ca.Intermediate.RawData,
        };
        var result = await new LongTermService(fake).AddLongTermDataAsync(await SignedAsync(ca, svc), null, NoExtra);
        Assert.Equal(SignatureRevocationStatus.Revoked, result.Status);
        var r = First(await svc.ValidateAsync(result.Pdf, null));
        Assert.Equal(SignatureRevocationStatus.Revoked, r.RevocationStatus);
        Assert.Equal(SignatureStatus.Invalid, r.Status);
    }

    [Fact]
    public async Task Crl_is_used_when_ocsp_fails_and_revoked_entry_is_found()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var good = new FakeRevocationTransport { Ca = ca, Get = url => url.AbsoluteUri == MiniCa.IssuerUrl ? ca.Intermediate.RawData : ca.Crl() };
        var result = await new LongTermService(good).AddLongTermDataAsync(await SignedAsync(ca, svc), null, NoExtra);
        Assert.Equal(SignatureRevocationStatus.Good, result.Status);
        Assert.Equal(1, result.OcspResponses); // tylko pośredni
        Assert.Equal(1, result.Crls);
        Assert.NotEmpty(result.Problems); // OCSP nie odpowiedział
        Assert.Equal(SignatureRevocationStatus.Good, First(await svc.ValidateAsync(result.Pdf, null)).RevocationStatus);

        var revoked = new FakeRevocationTransport { Ca = ca, Get = url => url.AbsoluteUri == MiniCa.IssuerUrl ? ca.Intermediate.RawData : ca.Crl(revokedSerial: ca.Leaf.SerialNumberBytes.ToArray()) };
        Assert.Equal(SignatureRevocationStatus.Revoked, (await new LongTermService(revoked).CheckOnlineAsync(await SignedAsync(ca, svc), null, NoExtra)).Status);
    }

    public static IEnumerable<object[]> BadResponses() => new[]
    {
        new object[] { "podpisana innym kluczem" },
        new object[] { "inny numer seryjny" },
        new object[] { "śmieci" },
        new object[] { "pusta" },
        new object[] { "responder bez EKU" },
        new object[] { "stara odpowiedź" },
    };

    [Theory]
    [MemberData(nameof(BadResponses))]
    public async Task Bad_ocsp_responses_do_not_count(string kind)
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var fake = new FakeRevocationTransport
        {
            Ca = ca,
            Ocsp = (_, request) => kind switch
            {
                "podpisana innym kluczem" => ca.OcspForged(request),
                "inny numer seryjny" => ca.OcspForOtherSerial(request),
                "śmieci" => new byte[] { 0x30, 0x80, 1, 2, 3 },
                "pusta" => Array.Empty<byte>(),
                "responder bez EKU" => ca.OcspDelegated(request, withEku: false),
                _ => ca.OcspOld(request),
            },
            Get = url => url.AbsoluteUri == MiniCa.IssuerUrl ? ca.Intermediate.RawData : throw new HttpRequestException("brak CRL"),
        };
        var result = await new LongTermService(fake).AddLongTermDataAsync(await SignedAsync(ca, svc), null, NoExtra);
        var r = First(await svc.ValidateAsync(result.Pdf, null));
        if (kind == "stara odpowiedź")
        {
            // poprawny podpis, ale nieaktualna: nie mówi nic o chwili podpisu
            Assert.Equal(SignatureRevocationStatus.Unknown, r.RevocationStatus);
            return;
        }
        Assert.Equal(SignatureRevocationStatus.Unknown, result.Status);
        Assert.Equal(1, result.OcspResponses); // tylko poprawna odpowiedź o pośrednim
        Assert.NotEmpty(result.Problems);
        Assert.Equal(SignatureRevocationStatus.Unknown, r.RevocationStatus);
        Assert.Single(DssData.Read(CosDocument.Open(result.Pdf, null)).Ocsp);
    }

    [Fact]
    public async Task Delegated_responder_with_eku_is_accepted()
    {
        var ca = CaWithIssuerUrl();
        var fake = new FakeRevocationTransport { Ca = ca, Ocsp = (_, request) => ca.OcspDelegated(request, withEku: true), Get = _ => ca.Intermediate.RawData };
        Assert.Equal(SignatureRevocationStatus.Good, (await new LongTermService(fake).CheckOnlineAsync(await SignedAsync(ca), null, NoExtra)).Status);
    }

    [Fact]
    public async Task Forged_crl_and_crl_of_other_issuer_do_not_count()
    {
        var ca = CaWithIssuerUrl();
        var cases = new Func<byte[]>[]
        {
            () => ca.Crl(key: ca.OtherKey),
            () => ca.Crl(key: ca.OtherKey, issuer: ca.Other),
            () => new byte[16],
        };
        foreach (var make in cases)
        {
            var fake = new FakeRevocationTransport { Ca = ca, Get = url => url.AbsoluteUri == MiniCa.IssuerUrl ? ca.Intermediate.RawData : make() };
            var result = await new LongTermService(fake).CheckOnlineAsync(await SignedAsync(ca), null, NoExtra);
            Assert.Equal(SignatureRevocationStatus.Unknown, result.Status);
        }
    }

    [Fact]
    public async Task Encrypted_file_stays_encrypted_and_dss_is_readable_with_password()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var encrypted = SamplePdfFactory.EncryptedAes256();
        var signed = await SignedAsync(ca, svc, encrypted, SamplePdfFactory.OwnerPassword);
        var result = await new LongTermService(Good(ca)).AddLongTermDataAsync(signed, SamplePdfFactory.OwnerPassword, NoExtra);
        Assert.True(CosDocument.Open(result.Pdf, SamplePdfFactory.UserPassword).IsEncrypted);
        var probe = await new ZetPDF.Rendering.PdfiumRenderer().ProbeAsync(result.Pdf);
        Assert.Equal(PdfProbeStatus.PasswordRequired, probe.Status);
        var r = First(await svc.ValidateAsync(result.Pdf, SamplePdfFactory.UserPassword));
        Assert.True(r.IntegrityOk);
        Assert.Equal(SignatureRevocationStatus.Good, r.RevocationStatus);
        Assert.Equal(2, DssData.Read(CosDocument.Open(result.Pdf, SamplePdfFactory.UserPassword)).Ocsp.Count);
    }

    [Fact]
    public async Task Only_http_and_https_and_only_listed_addresses_are_used()
    {
        var ca = new MiniCa(leafOcsp: "ftp://ocsp.ca.invalid/", leafCrl: "file:///C:/x.crl", withIssuerUrl: true);
        var read = RevocationSources.From(ca.Leaf);
        Assert.Empty(read.Ocsp);
        Assert.Empty(read.Crl);
        var fake = new FakeRevocationTransport { Ca = ca, Get = url => url.AbsoluteUri == MiniCa.IssuerUrl ? ca.Intermediate.RawData : Array.Empty<byte>() };
        var svc = new LongTermService(fake);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CheckOnlineAsync(await0(ca), null, LongTermConnectionPlan.Of(Array.Empty<string>(), new[] { "file:///C:/windows/x" })));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CheckOnlineAsync(await0(ca), null, LongTermConnectionPlan.Of(Array.Empty<string>(), new[] { "http://user:haslo@x.invalid/" })));
        var result = await svc.CheckOnlineAsync(await0(ca), null, LongTermConnectionPlan.Of(new[] { MiniCa.IssuerUrl, MiniCa.RootCertUrl, MiniCa.RootOcspUrl }, new[] { "https://moj.serwer.invalid/ocsp" }));
        Assert.Contains(fake.Calls, c => c.Contains("moj.serwer.invalid"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("ftp:") || c.Contains("file:"));
        Assert.NotEqual(SignatureRevocationStatus.Good, result.Status);
    }

    private static byte[] await0(MiniCa ca) => SignedAsync(ca).GetAwaiter().GetResult();

    [Fact]
    public void Urls_are_read_from_certificate_for_the_window()
    {
        var ca = CaWithIssuerUrl();
        var urls = RevocationSources.From(ca.Leaf);
        Assert.Equal(MiniCa.OcspUrl, Assert.Single(urls.Ocsp).AbsoluteUri);
        Assert.Equal(MiniCa.CrlUrl, Assert.Single(urls.Crl).AbsoluteUri);
        Assert.Equal(MiniCa.IssuerUrl, Assert.Single(urls.Issuers).AbsoluteUri);
        var listed = new LongTermService(new FakeRevocationTransport { Ca = ca }).GetCertificateUrls(ca.Leaf);
        Assert.Contains(MiniCa.OcspUrl, listed);
        Assert.Contains(MiniCa.CrlUrl, listed);
    }

    [Fact]
    public async Task Listing_urls_does_not_connect_and_issuer_must_sign_the_child()
    {
        var ca = CaWithIssuerUrl();
        var signed = await SignedAsync(ca);
        var fake = new FakeRevocationTransport { Ca = ca };
        var urls = await new LongTermService(fake).GetDocumentUrlsAsync(signed, null);
        Assert.Contains(MiniCa.OcspUrl, urls);
        Assert.Empty(fake.Calls);

        var evil = new FakeRevocationTransport { Ca = ca, Get = url => ca.Other.RawData, Ocsp = (_, r) => ca.OcspBy(r, CertificateStatus.Good) };
        Assert.NotEqual(SignatureRevocationStatus.Good, (await new LongTermService(evil).CheckOnlineAsync(signed, null, NoExtra)).Status);
    }

    [Fact]
    public async Task Pades_signature_has_etsi_subfilter_and_no_signing_time_attribute()
    {
        var ca = CaWithIssuerUrl();
        var signed = await SignedAsync(ca);
        var doc = CosDocument.Open(signed, null);
        var sig = doc.ResolveDict(doc.GetFields().Single(f => f.FieldType == "Sig").Dictionary["V"])!;
        Assert.Equal("ETSI.CAdES.detached", sig.GetName("SubFilter"));
        var cms = new System.Security.Cryptography.Pkcs.SignedCms();
        cms.Decode(SignatureValidator.TrimDer(SignatureValidator.ContentsBytes(doc, signed, sig)));
        var attrs = cms.SignerInfos[0].SignedAttributes;
        Assert.DoesNotContain(attrs.Cast<System.Security.Cryptography.CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.5");
        Assert.Contains(attrs.Cast<System.Security.Cryptography.CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47");
    }

    [Fact]
    public void Signature_without_long_term_option_stays_as_before()
    {
        Assert.False(new SignRequest { Certificate = new SigningCertificate(CertificateSource.PfxFile, TestCerts.Signer()) }.Pades);
    }

    [Fact]
    public async Task Address_from_downloaded_certificate_is_not_contacted_without_consent()
    {
        var ca = CaWithIssuerUrl();
        var signed = await SignedAsync(ca);
        // okno pokazało tylko adresy z certyfikatu podpisującego; pośredni wskazuje nowe adresy
        var shown = LongTermConnectionPlan.Of(new[] { MiniCa.OcspUrl, MiniCa.CrlUrl, MiniCa.IssuerUrl });
        var fake = Good(ca);
        var result = await new LongTermService(fake).CheckOnlineAsync(signed, null, shown);
        Assert.DoesNotContain(fake.Calls, c => c.Contains(MiniCa.RootOcspUrl) || c.Contains(MiniCa.RootCertUrl));
        Assert.Contains(MiniCa.RootCertUrl, result.PendingUrls);
        Assert.NotEqual(SignatureRevocationStatus.Good, result.Status);

        // druga runda po zgodzie
        var asked = new List<IReadOnlyList<string>>();
        var fake2 = Good(ca);
        var svc = new LongTermService(fake2);
        var final = await ZetPDF.Security.UI.LongTermRounds.RunAsync(shown, step => WrapStep(step), (pending, allow) => { asked.Add(pending); return allow; }, (plan, ct) => svc.CheckOnlineAsync(signed, null, plan, ct));
        Assert.Equal(SignatureRevocationStatus.Good, final!.Status);
        Assert.Empty(final.PendingUrls);
        Assert.Contains(asked, list => list.Contains(MiniCa.RootCertUrl));

        // odmowa: nowy adres nigdy nie zostaje wywołany
        var fake3 = Good(ca);
        var svc3 = new LongTermService(fake3);
        var refused = await ZetPDF.Security.UI.LongTermRounds.RunAsync(shown, step => WrapStep(step), (_, _) => null, (plan, ct) => svc3.CheckOnlineAsync(signed, null, plan, ct));
        Assert.DoesNotContain(fake3.Calls, c => c.Contains(MiniCa.RootCertUrl) || c.Contains(MiniCa.RootOcspUrl));
        Assert.Contains(MiniCa.RootCertUrl, refused!.PendingUrls);
    }

    private static async Task<LongTermResult?> WrapStep(Func<CancellationToken, Task<LongTermResult>> step) => await step(default);

    [Fact]
    public async Task Consent_rounds_are_limited()
    {
        int round = 0;
        var result = await ZetPDF.Security.UI.LongTermRounds.RunAsync(LongTermConnectionPlan.Of(Array.Empty<string>()), step => WrapStep(step), (_, allow) => allow, (_, _) =>
        {
            round++;
            return Task.FromResult(new LongTermResult(Array.Empty<byte>(), 0, 0, 0, SignatureRevocationStatus.Unknown, Array.Empty<LongTermSignatureStatus>(), Array.Empty<string>()) { PendingUrls = new[] { "http://kolejny" + round + ".invalid/" } });
        });
        Assert.Equal(ZetPDF.Security.UI.LongTermRounds.MaxRounds, round);
        Assert.NotEmpty(result!.PendingUrls);
    }

    [Fact]
    public async Task Only_approved_addresses_are_called_even_from_the_signer_certificate()
    {
        var ca = CaWithIssuerUrl();
        var fake = Good(ca);
        var result = await new LongTermService(fake).CheckOnlineAsync(await SignedAsync(ca), null, LongTermConnectionPlan.Of(Array.Empty<string>()));
        Assert.Empty(fake.Calls);
        Assert.Contains(MiniCa.IssuerUrl, result.PendingUrls);
    }

    [Fact]
    public async Task Shadow_object_defined_after_signature_is_not_excused()
    {
        // przed podpisem strona wskazuje obiekt jeszcze niezdefiniowany (dla czytnika to null)
        var plain = SamplePdfFactory.Text(1);
        var prepare = CosDocument.Open(plain, null);
        int dangling = prepare.Size + 500;
        var before = new IncrementalUpdate(prepare);
        var pageRef = prepare.GetPageReferences()[0];
        var page = (CosDictionary)prepare.ResolveDict(pageRef)!.Clone();
        page["Thumb"] = new CosReference(dangling, 0);
        before.Set(pageRef, page);
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var signed = await SignedAsync(ca, svc, before.Write());

        var doc = CosDocument.Open(signed, null);
        var after = new IncrementalUpdate(doc);
        after.Set(new CosReference(dangling, 0), new CosStream(new CosDictionary(), new byte[] { 1, 2, 3 }));
        var r = First(await svc.ValidateAsync(after.Write(), null));
        Assert.False(r.ModifiedOnlyByLongTermData);
        Assert.Equal(SignatureStatus.ValidButModifiedAfter, r.Status);

        // nowy obiekt, do którego stary graf się nie odwołuje, to nie cień
        var unrelated = new IncrementalUpdate(doc);
        unrelated.Set(new CosReference(dangling + 1, 0), new CosStream(new CosDictionary(), new byte[] { 1 }));
        Assert.True(First(await svc.ValidateAsync(unrelated.Write(), null)).ModifiedOnlyByLongTermData);
    }

    [Fact]
    public void Handlers_do_not_use_proxy_for_revocation_and_do_not_follow_redirects()
    {
        using var revocation = HttpRevocationTransport.CreateHandler();
        Assert.False(revocation.UseProxy);
        Assert.False(revocation.AllowAutoRedirect);
        Assert.NotNull(revocation.ConnectCallback);
        using var tsa = HttpTimestampClient.CreateHandler();
        Assert.False(tsa.AllowAutoRedirect);
    }

    public class NullDialogs : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method!.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
    }

    [Fact]
    public void Long_term_option_is_off_by_default_in_sign_window()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var loc = new ZetPDF.Core.Localization.LocalizationService(initial: new System.Globalization.CultureInfo("pl"));
                loc.RegisterResources(SecurityModule.Strings);
                var dialog = new ZetPDF.Security.UI.SignDialog(loc, TestEnv.Signatures(), TestEnv.Paths(), System.Reflection.DispatchProxy.Create<IDialogService, NullDialogs>(), new ZetPDF.Security.UI.SecurityModuleSettings(), Tsa, hasRectangle: true);
                Assert.False(dialog.LongTerm);
                Assert.False(dialog.ArchiveTimestamp);
                Assert.Empty(dialog.ExtraUrls);
                dialog.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public async Task Timestamp_field_with_action_is_not_excused()
    {
        var ca = CaWithIssuerUrl();
        var svc = TestEnv.Signatures();
        var stamped = await svc.TimestampDocumentAsync(await SignedAsync(ca, svc), Tsa, null);
        var doc = CosDocument.Open(stamped, null);
        var field = doc.GetFields().Single(f => f.FullName.StartsWith("Timestamp", StringComparison.Ordinal));
        var upd = new IncrementalUpdate(doc);
        var dict = (CosDictionary)field.Dictionary.Clone();
        dict["AA"] = new CosDictionary();
        upd.Set(field.Reference!, dict);
        var result = (await svc.ValidateAsync(upd.Write(), null)).Single(r => r.BaselineLevel.Length > 0);
        Assert.False(result.ModifiedOnlyByLongTermData);
        Assert.Equal(SignatureStatus.ValidButModifiedAfter, result.Status);
    }

    // ------------------------------------------------------------------ transport

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task Transport_limits_size_scheme_and_does_not_follow_redirects()
    {
        using (var big = new HttpRevocationTransport(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[RevocationLimits.MaxResponseBytes + 1]) })))
            await Assert.ThrowsAsync<InvalidDataException>(() => big.GetAsync(new Uri("http://x.invalid/"), false, default));
        using var redirect = new HttpRevocationTransport(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)));
        await Assert.ThrowsAsync<HttpRequestException>(() => redirect.GetAsync(new Uri("http://x.invalid/"), false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => redirect.GetAsync(new Uri("ftp://x.invalid/"), false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => redirect.PostOcspAsync(new Uri("file:///C:/x"), new byte[1], false, default));
    }

    // adres, zablokowany bez opcji, zablokowany z opcją sieci lokalnej
    [Theory]
    [InlineData("10.0.0.1", true, false)]
    [InlineData("10.255.255.255", true, false)]
    [InlineData("172.16.0.1", true, false)]
    [InlineData("172.31.255.255", true, false)]
    [InlineData("172.32.0.1", false, false)]
    [InlineData("172.15.0.1", false, false)]
    [InlineData("192.168.1.1", true, false)]
    [InlineData("100.64.0.1", true, false)]
    [InlineData("100.127.255.255", true, false)]
    [InlineData("100.128.0.1", false, false)]
    [InlineData("127.0.0.1", true, true)]
    [InlineData("127.1.2.3", true, true)]
    [InlineData("0.0.0.0", true, true)]
    [InlineData("0.1.2.3", true, true)]
    [InlineData("169.254.169.254", true, true)]
    [InlineData("192.0.0.1", true, true)]
    [InlineData("198.18.0.1", true, true)]
    [InlineData("198.19.255.255", true, true)]
    [InlineData("198.20.0.1", false, false)]
    [InlineData("224.0.0.1", true, true)]
    [InlineData("240.0.0.1", true, true)]
    [InlineData("255.255.255.255", true, true)]
    [InlineData("93.184.216.34", false, false)]
    [InlineData("::", true, true)]
    [InlineData("::1", true, true)]
    [InlineData("fe80::1", true, true)]
    [InlineData("febf::1", true, true)]
    [InlineData("ff02::1", true, true)]
    [InlineData("fc00::1", true, false)]
    [InlineData("fd12:3456::1", true, false)]
    [InlineData("2606:2800:220:1::1", false, false)]
    [InlineData("::ffff:127.0.0.1", true, true)]
    [InlineData("::ffff:10.0.0.1", true, false)]
    [InlineData("::ffff:169.254.169.254", true, true)]
    [InlineData("::ffff:93.184.216.34", false, false)]
    [InlineData("64:ff9b::7f00:1", true, true)]
    [InlineData("64:ff9b::a00:1", true, false)]
    [InlineData("64:ff9b::a9fe:a9fe", true, true)]
    [InlineData("64:ff9b::5db8:d822", false, false)]
    [InlineData("2002:7f00:1::", true, true)]
    [InlineData("2002:c0a8:101::", true, false)]
    [InlineData("::7f00:1", true, true)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", true, true)] // Teredo
    [InlineData("2001:db8::1", true, true)] // dokumentacja
    [InlineData("64:ff9b:1::1", true, true)] // lokalne tłumaczenie NAT64
    [InlineData("100::1", true, true)] // zakres odrzucany
    [InlineData("3fff::1", false, false)]
    [InlineData("2001:4860:4860::8888", false, false)]
    public void Address_table(string address, bool forbidden, bool forbiddenWithOption)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(forbidden, HttpRevocationTransport.IsForbiddenAddress(ip));
        Assert.Equal(forbiddenWithOption, HttpRevocationTransport.IsForbiddenAddress(ip, allowLocalNetwork: true));
    }

    [Fact]
    public async Task Local_option_reaches_the_transport_and_is_off_by_default()
    {
        var ca = CaWithIssuerUrl();
        var signed = await SignedAsync(ca);
        var off = Good(ca);
        await new LongTermService(off).CheckOnlineAsync(signed, null, NoExtra);
        Assert.NotEmpty(off.AllowLocalFlags);
        Assert.All(off.AllowLocalFlags, Assert.False);
        var on = Good(ca);
        await new LongTermService(on).CheckOnlineAsync(signed, null, NoExtra with { AllowLocalNetwork = true });
        Assert.All(on.AllowLocalFlags, Assert.True);
        Assert.False(LongTermConnectionPlan.Of(Array.Empty<string>()).AllowLocalNetwork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_connection_to_this_computer_is_refused_with_or_without_the_option(bool allowLocal)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var transport = new HttpRevocationTransport();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => transport.GetAsync(new Uri($"http://127.0.0.1:{port}/"), allowLocal, default));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => transport.PostOcspAsync(new Uri($"http://localhost:{port}/"), new byte[1], allowLocal, default));
        Assert.False(listener.Pending());
    }

    [Fact]
    public void Confirm_window_has_local_option_off_by_default()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var loc = new ZetPDF.Core.Localization.LocalizationService(initial: new System.Globalization.CultureInfo("pl"));
                loc.RegisterResources(SecurityModule.Strings);
                var window = new ZetPDF.Security.UI.LongTermConfirmDialog(loc, "adresy", false);
                Assert.False(window.AllowLocalNetwork);
                window.Close();
                // anulowanie okna to brak połączenia
                Assert.Null(ZetPDF.Security.UI.LongTermConfirmDialog.Ask(System.Reflection.DispatchProxy.Create<IDialogService, NullDialogs>(), loc, "adresy", false));
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void Malformed_dss_entries_are_ignored()
    {
        var doc = CosDocument.Open(SamplePdfFactory.Text(1), null);
        Assert.False(DssData.Read(doc).Any);
        var ca = CaWithIssuerUrl();
        Assert.Null(OcspMessages.Verify(new byte[] { 0x30, 0x03, 1, 1, 1 }, ca.Leaf, ca.Intermediate));
        Assert.Null(CrlMessages.Verify(new byte[1024], ca.Leaf, ca.Intermediate));
    }
}

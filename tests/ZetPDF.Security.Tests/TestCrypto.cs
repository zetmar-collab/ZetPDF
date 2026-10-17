using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.Security.Signatures;

namespace ZetPDF.Security.Tests;

/// <summary>Certyfikaty testowe generowane w locie (żadnych kluczy w repozytorium).</summary>
internal static class TestCerts
{
    public static X509Certificate2 Signer(string name = "Jan Kowalski – Zażółć", int validDays = 365)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}, O=ZetPDF Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var now = DateTimeOffset.Now;
        using var c = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(validDays));
        // eksport/import, żeby klucz był użyteczny dla SignedCms niezależnie od dostawcy
        return new X509Certificate2(c.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.EphemeralKeySet);
    }

    public static X509Certificate2 Tsa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=ZetPDF Test TSA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, true));
        var now = DateTimeOffset.Now;
        using var c = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(365));
        return new X509Certificate2(c.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.EphemeralKeySet);
    }
}

/// <summary>Lokalna atrapa serwera TSA (RFC 3161) – testy nie łączą się z siecią.</summary>
internal sealed class FakeTimestampClient : ITimestampClient
{
    private readonly X509Certificate2 _cert = TestCerts.Tsa();
    public int Calls { get; private set; }
    public DateTimeOffset Time { get; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public Task<byte[]> RequestAsync(Uri tsaUrl, byte[] request, CancellationToken ct)
    {
        Calls++;
        if (!Rfc3161TimestampRequest.TryDecode(request, out var req, out _) || req is null) throw new InvalidOperationException("bad request");

        // TSTInfo
        var serial = BitConverter.GetBytes(RandomNumberGenerator.GetInt32(1, int.MaxValue)).Reverse().ToArray();
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5"), req.HashAlgorithmId, req.GetMessageHash(), serial, Time,
            nonce: req.GetNonce());
        var tstInfo = info.Encode();

        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), tstInfo));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = req.RequestSignerCertificate ? X509IncludeOption.EndCertOnly : X509IncludeOption.None,
        };
        // ESS signing-certificate-v2 (wymagany przez Rfc3161TimestampToken)
        var ess = new AsnWriter(AsnEncodingRules.DER);
        using (ess.PushSequence())
        using (ess.PushSequence())
        using (ess.PushSequence())
            ess.WriteOctetString(SHA256.HashData(_cert.RawData));
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), ess.Encode()));
        cms.ComputeSignature(signer, silent: true);

        var resp = new AsnWriter(AsnEncodingRules.DER);
        using (resp.PushSequence())
        {
            using (resp.PushSequence()) resp.WriteInteger(0);
            resp.WriteEncodedValue(cms.Encode());
        }
        return Task.FromResult(resp.Encode());
    }
}

internal static class TestEnv
{
    public static AppPaths Paths() => new(Path.Combine(Path.GetTempPath(), "ZetPDF-tests", "home-" + Guid.NewGuid().ToString("N")[..8]));

    public static SignatureService Signatures(ITimestampClient? tsa = null) => new(Paths(), tsa ?? new FakeTimestampClient());

    public static SignRequest Request(X509Certificate2 cert, PdfRect rect = default, int page = 0, string? tsa = null, string? field = null) => new()
    {
        Certificate = new SigningCertificate(CertificateSource.PfxFile, cert),
        PageIndex = page,
        Rect = rect,
        Reason = "Zatwierdzam – zażółć gęślą jaźń",
        Location = "Kraków",
        TsaUrl = tsa,
        FieldName = field,
    };
}

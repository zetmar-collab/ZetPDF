using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ZetPDF.Core.Localization;

namespace ZetPDF.Security.Signatures;

/// <summary>Transport zapytań RFC 3161 (podmieniany w testach – testy nie łączą się z siecią).</summary>
public interface ITimestampClient
{
    /// <summary>Wysyła TimeStampReq (DER) i zwraca TimeStampResp (DER).</summary>
    Task<byte[]> RequestAsync(Uri tsaUrl, byte[] request, CancellationToken ct);
}

/// <summary>
/// HTTP(S) POST application/timestamp-query. Połączenie sieciowe wykonywane tylko, gdy użytkownik
/// wpisał adres TSA w Ustawieniach i świadomie wybrał znacznik czasu.
/// </summary>
public sealed class HttpTimestampClient : ITimestampClient
{
    /// <summary>Adres wpisuje użytkownik, więc proxy systemu zostaje; bez przekierowań, odpowiedź ograniczona rozmiarem.</summary>
    internal static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    private static readonly HttpClient Http = new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 16 * 1024 * 1024 };

    public async Task<byte[]> RequestAsync(Uri tsaUrl, byte[] request, CancellationToken ct)
    {
        if (tsaUrl.Scheme != Uri.UriSchemeHttp && tsaUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("TSA address must use http or https.", nameof(tsaUrl)).WithMessageKey("Security.Error.TsaScheme");
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var msg = new HttpRequestMessage(HttpMethod.Post, tsaUrl) { Content = content };
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/timestamp-reply"));
        using var resp = await Http.SendAsync(msg, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Budowa podpisu CMS (PKCS#7 detached, SHA-256) z opcjonalnym tokenem znacznika czasu.</summary>
internal static class CmsSignatureBuilder
{
    public const string SignatureTimeStampTokenOid = "1.2.840.113549.1.9.16.2.14";
    public const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    public static async Task<byte[]> SignAsync(byte[] data, X509Certificate2 certificate, DateTimeOffset signingTime,
        ITimestampClient? tsaClient, Uri? tsaUrl, bool silent, CancellationToken ct, bool pades = false)
    {
        if (!certificate.HasPrivateKey) throw new CryptographicException("The certificate has no private key.").WithMessageKey("Security.Sign.Error.NoPrivateKey");
        var cms = new SignedCms(new ContentInfo(data), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid(Sha256Oid),
            // łańcuch dokładamy sami – WholeChain mógłby pobierać certyfikaty z sieci (AIA)
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        foreach (var extra in BuildChainOffline(certificate, signingTime))
            signer.Certificates.Add(extra);
        if (pades)
        {
            // ESS signing-certificate-v2 z SHA-256; PAdES nie dopuszcza atrybutu czasu podpisu, czas niesie /M i znacznik
            var ess = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            using (ess.PushSequence())
            using (ess.PushSequence())
            using (ess.PushSequence())
                ess.WriteOctetString(SHA256.HashData(certificate.RawData));
            signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), ess.Encode()));
        }
        else signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        cms.ComputeSignature(signer, silent);

        if (tsaClient is not null && tsaUrl is not null)
        {
            var si = cms.SignerInfos[0];
            var token = await RequestTokenAsync(SHA256.HashData(si.GetSignature()), tsaClient, tsaUrl, ct).ConfigureAwait(false);
            si.AddUnsignedAttribute(new AsnEncodedData(new Oid(SignatureTimeStampTokenOid), token.AsSignedCms().Encode()));
        }
        return cms.Encode();
    }

    /// <summary>Token RFC 3161 dla skrótu SHA-256 (sprawdzony względem zapytania: skrót, nonce).</summary>
    public static async Task<Rfc3161TimestampToken> RequestTokenAsync(byte[] sha256, ITimestampClient client, Uri url, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] &= 0x7F; // dodatnia liczba
        var req = Rfc3161TimestampRequest.CreateFromHash(sha256, HashAlgorithmName.SHA256, requestedPolicyId: null, nonce: nonce, requestSignerCertificates: true);
        var response = await client.RequestAsync(url, req.Encode(), ct).ConfigureAwait(false);
        return req.ProcessResponse(response, out _);
    }

    /// <summary>Certyfikaty pośrednie z magazynów Windows, bez pobierania z sieci i bez CRL/OCSP (bez korzenia).</summary>
    public static IEnumerable<X509Certificate2> BuildChainOffline(X509Certificate2 certificate, DateTimeOffset time)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = time.LocalDateTime;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.Build(certificate);
        var list = new List<X509Certificate2>();
        for (int i = 1; i < chain.ChainElements.Count; i++)
        {
            var c = chain.ChainElements[i].Certificate;
            if (c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) continue; // korzeń
            list.Add(c);
        }
        return list;
    }
}

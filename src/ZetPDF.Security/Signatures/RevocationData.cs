using System.Formats.Asn1;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.Security.Signatures;

/// <summary>Transport OCSP i CRL. Odpowiedź jest niezaufana; limit rozmiaru obowiązuje też w implementacjach testowych przez <see cref="RevocationLimits"/>.</summary>
public interface IRevocationTransport
{
    Task<byte[]> GetAsync(Uri url, CancellationToken ct);
    Task<byte[]> PostOcspAsync(Uri url, byte[] request, CancellationToken ct);
}

internal static class RevocationLimits
{
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    public const int MaxUrlsPerCertificate = 8;
    public const int MaxPathLength = 8;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Połączenie sieciowe na wyraźne polecenie użytkownika (docs/DECYZJE.md D-46): tylko http i https, bez przekierowań (mogłyby prowadzić pod adres, którego
/// użytkownik nie widział), bez połączeń do adresów pętli zwrotnej i lokalnych łącza (adres z certyfikatu w obcym dokumencie nie może sięgać do usług na tym
/// komputerze), limit czasu i rozmiaru odpowiedzi.
/// </summary>
public sealed class HttpRevocationTransport : IRevocationTransport, IDisposable
{
    private readonly HttpClient _http;

    public HttpRevocationTransport() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectAsync,
    })
    {
    }

    internal HttpRevocationTransport(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = RevocationLimits.RequestTimeout };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Czy z tego adresu IP nie wolno korzystać.</summary>
    public static bool IsForbiddenAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return true; // adresy łącza i usługi metadanych
            if (b[0] >= 224) return true; // multicast i zarezerwowane
            if (b[0] == 0) return true;
        }
        return false;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        var allowed = addresses.Where(a => !IsForbiddenAddress(a)).ToArray();
        if (allowed.Length == 0) throw new HttpRequestException("Address not allowed.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<byte[]> GetAsync(Uri url, CancellationToken ct)
    {
        RevocationSources.RequireAllowed(url);
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> PostOcspAsync(Uri url, byte[] request, CancellationToken ct)
    {
        RevocationSources.RequireAllowed(url);
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ocsp-request");
        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        return await SendAsync(msg, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)response.StatusCode);
        if (response.Content.Headers.ContentLength > RevocationLimits.MaxResponseBytes) throw new InvalidDataException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > RevocationLimits.MaxResponseBytes) throw new InvalidDataException("Response too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

/// <summary>Adresy z certyfikatu (AIA: OCSP i wystawca, CDP: CRL). Tylko http i https, bez danych logowania.</summary>
internal sealed record CertificateUrls(IReadOnlyList<Uri> Ocsp, IReadOnlyList<Uri> Crl, IReadOnlyList<Uri> Issuers)
{
    public IEnumerable<Uri> All => Ocsp.Concat(Crl).Concat(Issuers);
}

internal static class RevocationSources
{
    private const string AiaOid = "1.3.6.1.5.5.7.1.1", CdpOid = "2.5.29.31", OcspMethod = "1.3.6.1.5.5.7.48.1", IssuersMethod = "1.3.6.1.5.5.7.48.2";
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    public static bool IsAllowed(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.UserInfo.Length == 0 && url.Host.Length > 0;

    public static void RequireAllowed(Uri url)
    {
        if (!IsAllowed(url)) throw new ArgumentException("Only http and https addresses are allowed.", nameof(url)).WithMessageKey("Security.LongTerm.Error.Scheme");
    }

    private static void AddUrl(List<Uri> list, string text)
    {
        if (list.Count < RevocationLimits.MaxUrlsPerCertificate && Uri.TryCreate(text, UriKind.Absolute, out var u) && IsAllowed(u) && !list.Contains(u)) list.Add(u);
    }

    public static CertificateUrls From(X509Certificate2 certificate)
    {
        var ocsp = new List<Uri>();
        var crl = new List<Uri>();
        var issuers = new List<Uri>();
        foreach (var ext in certificate.Extensions)
        {
            try
            {
                if (ext.Oid?.Value == AiaOid) ReadAia(ext.RawData, ocsp, issuers);
                else if (ext.Oid?.Value == CdpOid) ReadCdp(ext.RawData, crl);
            }
            catch (AsnContentException) { }
        }
        return new CertificateUrls(ocsp, crl, issuers);
    }

    private static void ReadAia(byte[] raw, List<Uri> ocsp, List<Uri> issuers)
    {
        var seq = new AsnReader(raw, AsnEncodingRules.DER).ReadSequence();
        while (seq.HasData)
        {
            var access = seq.ReadSequence();
            var method = access.ReadObjectIdentifier();
            if (access.PeekTag().HasSameClassAndValue(UriTag))
            {
                var text = access.ReadCharacterString(UniversalTagNumber.IA5String, UriTag);
                if (method == OcspMethod) AddUrl(ocsp, text);
                else if (method == IssuersMethod) AddUrl(issuers, text);
            }
            else access.ReadEncodedValue();
        }
    }

    private static void ReadCdp(byte[] raw, List<Uri> crl)
    {
        var seq = new AsnReader(raw, AsnEncodingRules.DER).ReadSequence();
        while (seq.HasData)
        {
            var point = seq.ReadSequence();
            if (!point.HasData || point.PeekTag().TagValue != 0) continue;
            var name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
            if (!name.HasData || name.PeekTag().TagValue != 0) continue;
            var full = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
            while (full.HasData)
            {
                if (full.PeekTag().HasSameClassAndValue(UriTag)) AddUrl(crl, full.ReadCharacterString(UniversalTagNumber.IA5String, UriTag));
                else full.ReadEncodedValue();
            }
        }
    }
}

/// <summary>Podpisy X.509 i struktur podpisanych: algorytmy z listy, bez MD5.</summary>
internal static class CertCrypto
{
    public static bool VerifyIssuedBy(X509Certificate2 child, X509Certificate2 issuer)
    {
        try
        {
            if (!child.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData)) return false;
            var cert = new AsnReader(child.RawData, AsnEncodingRules.DER).ReadSequence();
            var tbs = cert.ReadEncodedValue();
            var alg = cert.ReadSequence();
            var oid = alg.ReadObjectIdentifier();
            var parameters = alg.HasData ? alg.ReadEncodedValue() : default;
            var signature = cert.ReadBitString(out _);
            return Verify(tbs.Span, oid, parameters, signature, issuer);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Podpis danych kluczem certyfikatu. Nieznany algorytm to false.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, string algorithmOid, ReadOnlyMemory<byte> parameters, byte[] signature, X509Certificate2 signer)
    {
        try
        {
            switch (algorithmOid)
            {
                case "1.2.840.113549.1.1.5": return VerifyRsa(data, signature, signer, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.11": return VerifyRsa(data, signature, signer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.12": return VerifyRsa(data, signature, signer, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.13": return VerifyRsa(data, signature, signer, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.10": return VerifyRsa(data, signature, signer, PssHash(parameters), RSASignaturePadding.Pss);
                case "1.2.840.10045.4.1": return VerifyEc(data, signature, signer, HashAlgorithmName.SHA1);
                case "1.2.840.10045.4.3.2": return VerifyEc(data, signature, signer, HashAlgorithmName.SHA256);
                case "1.2.840.10045.4.3.3": return VerifyEc(data, signature, signer, HashAlgorithmName.SHA384);
                case "1.2.840.10045.4.3.4": return VerifyEc(data, signature, signer, HashAlgorithmName.SHA512);
                default: return false;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool VerifyRsa(ReadOnlySpan<byte> data, byte[] signature, X509Certificate2 signer, HashAlgorithmName hash, RSASignaturePadding padding)
    {
        using var key = signer.GetRSAPublicKey();
        return key is not null && key.VerifyData(data, signature, hash, padding);
    }

    private static bool VerifyEc(ReadOnlySpan<byte> data, byte[] signature, X509Certificate2 signer, HashAlgorithmName hash)
    {
        using var key = signer.GetECDsaPublicKey();
        return key is not null && key.VerifyData(data, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static HashAlgorithmName PssHash(ReadOnlyMemory<byte> parameters)
    {
        if (parameters.IsEmpty) return HashAlgorithmName.SHA1;
        var seq = new AsnReader(parameters, AsnEncodingRules.DER).ReadSequence();
        if (!seq.HasData || seq.PeekTag().TagValue != 0) return HashAlgorithmName.SHA1;
        var alg = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
        return alg.ReadObjectIdentifier() switch
        {
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => throw new NotSupportedException(),
        };
    }

    public static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().TagValue == (int)UniversalTagNumber.UtcTime ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    /// <summary>Numer seryjny bez wiodących zer, do porównań.</summary>
    public static ReadOnlySpan<byte> TrimSerial(ReadOnlySpan<byte> serial)
    {
        int i = 0;
        while (i < serial.Length - 1 && serial[i] == 0) i++;
        return serial[i..];
    }
}

/// <summary>Wynik odpowiedzi OCSP lub CRL po weryfikacji kryptograficznej.</summary>
internal sealed record VerifiedRevocation(SignatureRevocationStatus Status, DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate, DateTimeOffset? RevokedAt);

/// <summary>OCSP (RFC 6960): żądanie i weryfikacja odpowiedzi. Odpowiedź jest uznana tylko z podpisem wystawcy albo delegowanego respondera wystawionego przez niego.</summary>
internal static class OcspMessages
{
    private const string Sha1 = "1.3.14.3.2.26", Sha256 = "2.16.840.1.101.3.4.2.1", BasicType = "1.3.6.1.5.5.7.48.1.1", OcspSigningEku = "1.3.6.1.5.5.7.3.9";

    public static byte[] BuildRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(Sha1);
                w.WriteNull();
            }
            w.WriteOctetString(SHA1.HashData(issuer.SubjectName.RawData));
            w.WriteOctetString(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData));
            w.WriteInteger(certificate.SerialNumberBytes.Span);
        }
        return w.Encode();
    }

    /// <summary>Null, gdy odpowiedź nieprawidłowa, nie dotyczy tego certyfikatu albo podpis się nie zgadza. Nigdy wyjątek na danych z sieci.</summary>
    public static VerifiedRevocation? Verify(byte[] der, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        try
        {
            var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            if (outer.ReadEnumeratedBytes().Span is not [0]) return null;
            var bytes = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
            if (bytes.ReadObjectIdentifier() != BasicType) return null;
            var basic = new AsnReader(bytes.ReadOctetString(), AsnEncodingRules.DER).ReadSequence();
            var tbs = basic.ReadEncodedValue();
            var alg = basic.ReadSequence();
            var oid = alg.ReadObjectIdentifier();
            var parameters = alg.HasData ? alg.ReadEncodedValue() : default;
            var signature = basic.ReadBitString(out _);
            var embedded = new List<X509Certificate2>();
            if (basic.HasData)
            {
                var certs = basic.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
                while (certs.HasData && embedded.Count < 8) embedded.Add(new X509Certificate2(certs.ReadEncodedValue().ToArray()));
            }

            var data = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
            if (data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) data.ReadEncodedValue(); // wersja
            data.ReadEncodedValue(); // responderID
            var produced = data.ReadGeneralizedTime();
            if (!SignatureOk(tbs.Span, oid, parameters, signature, issuer, embedded, produced)) return null;

            var responses = data.ReadSequence();
            while (responses.HasData)
            {
                var single = responses.ReadSequence();
                var id = single.ReadSequence();
                if (!Matches(id, certificate, issuer))
                    continue;
                var statusTag = single.PeekTag();
                SignatureRevocationStatus status;
                DateTimeOffset? revokedAt = null;
                switch (statusTag.TagValue)
                {
                    case 0:
                        single.ReadEncodedValue();
                        status = SignatureRevocationStatus.Good;
                        break;
                    case 1:
                        var revoked = single.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true));
                        revokedAt = revoked.ReadGeneralizedTime();
                        status = SignatureRevocationStatus.Revoked;
                        break;
                    default:
                        single.ReadEncodedValue();
                        status = SignatureRevocationStatus.Unknown;
                        break;
                }
                var thisUpdate = single.ReadGeneralizedTime();
                DateTimeOffset? nextUpdate = null;
                if (single.HasData && single.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    nextUpdate = single.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadGeneralizedTime();
                return new VerifiedRevocation(status, thisUpdate, nextUpdate, revokedAt);
            }
            return null;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Matches(AsnReader id, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var hash = id.ReadSequence().ReadObjectIdentifier();
        var nameHash = id.ReadOctetString();
        var keyHash = id.ReadOctetString();
        var serial = id.ReadIntegerBytes();
        Func<byte[], byte[]>? compute = hash switch
        {
            Sha1 => SHA1.HashData,
            Sha256 => SHA256.HashData,
            _ => null,
        };
        return compute is not null
            && CertCrypto.TrimSerial(serial.Span).SequenceEqual(CertCrypto.TrimSerial(certificate.SerialNumberBytes.Span))
            && compute(issuer.SubjectName.RawData).AsSpan().SequenceEqual(nameHash)
            && compute(issuer.PublicKey.EncodedKeyValue.RawData).AsSpan().SequenceEqual(keyHash);
    }

    private static bool SignatureOk(ReadOnlySpan<byte> tbs, string oid, ReadOnlyMemory<byte> parameters, byte[] signature, X509Certificate2 issuer, List<X509Certificate2> embedded, DateTimeOffset produced)
    {
        if (CertCrypto.Verify(tbs, oid, parameters, signature, issuer)) return true;
        foreach (var responder in embedded)
        {
            if (!CertCrypto.VerifyIssuedBy(responder, issuer)) continue;
            if (produced.UtcDateTime < responder.NotBefore.ToUniversalTime() || produced.UtcDateTime > responder.NotAfter.ToUniversalTime()) continue;
            bool eku = responder.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == OcspSigningEku));
            if (eku && CertCrypto.Verify(tbs, oid, parameters, signature, responder)) return true;
        }
        return false;
    }
}

/// <summary>CRL (RFC 5280): weryfikacja podpisu wystawcy i odszukanie numeru seryjnego. Delty i listy częściowe nie są obsługiwane.</summary>
internal static class CrlMessages
{
    private const string DeltaIndicator = "2.5.29.27", Idp = "2.5.29.28";

    /// <summary>Null, gdy lista nieprawidłowa, innego wystawcy lub z błędnym podpisem. Nigdy wyjątek na danych z sieci.</summary>
    public static VerifiedRevocation? Verify(byte[] der, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        try
        {
            var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            var tbsRaw = outer.ReadEncodedValue();
            var alg = outer.ReadSequence();
            var oid = alg.ReadObjectIdentifier();
            var parameters = alg.HasData ? alg.ReadEncodedValue() : default;
            var signature = outer.ReadBitString(out _);
            if (!CertCrypto.Verify(tbsRaw.Span, oid, parameters, signature, issuer)) return null;

            var tbs = new AsnReader(tbsRaw, AsnEncodingRules.DER).ReadSequence();
            if (tbs.PeekTag().TagValue == (int)UniversalTagNumber.Integer && tbs.PeekTag().TagClass == TagClass.Universal) tbs.ReadEncodedValue();
            tbs.ReadSequence(); // algorytm w treści
            if (!tbs.ReadEncodedValue().Span.SequenceEqual(issuer.SubjectName.RawData)) return null;
            var thisUpdate = CertCrypto.ReadTime(tbs);
            DateTimeOffset? nextUpdate = null;
            if (tbs.HasData && tbs.PeekTag().TagClass == TagClass.Universal && tbs.PeekTag().TagValue is (int)UniversalTagNumber.UtcTime or (int)UniversalTagNumber.GeneralizedTime)
                nextUpdate = CertCrypto.ReadTime(tbs);

            DateTimeOffset? revokedAt = null;
            if (tbs.HasData && tbs.PeekTag().TagClass == TagClass.Universal && tbs.PeekTag().TagValue == (int)UniversalTagNumber.Sequence)
            {
                var list = tbs.ReadSequence();
                var wanted = CertCrypto.TrimSerial(certificate.SerialNumberBytes.Span).ToArray();
                while (list.HasData)
                {
                    var entry = list.ReadSequence();
                    var serial = entry.ReadIntegerBytes();
                    var date = CertCrypto.ReadTime(entry);
                    if (revokedAt is null && CertCrypto.TrimSerial(serial.Span).SequenceEqual(wanted)) revokedAt = date;
                }
            }
            if (tbs.HasData)
            {
                var extensions = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
                while (extensions.HasData)
                {
                    var ext = extensions.ReadSequence();
                    // delta lub lista częściowa nie mówi nic o certyfikatach spoza niej
                    if (ext.ReadObjectIdentifier() is DeltaIndicator or Idp) return revokedAt is not null ? new VerifiedRevocation(SignatureRevocationStatus.Revoked, thisUpdate, nextUpdate, revokedAt) : null;
                }
            }
            return new VerifiedRevocation(revokedAt is null ? SignatureRevocationStatus.Good : SignatureRevocationStatus.Revoked, thisUpdate, nextUpdate, revokedAt);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Ścieżka certyfikatów po sprawdzeniu podpisów wystawców. Tylko do znalezienia wystawcy; o zaufaniu decyduje magazyn Windows.</summary>
internal static class CertPath
{
    public static List<X509Certificate2> Build(X509Certificate2 certificate, IEnumerable<X509Certificate2> pool)
    {
        var candidates = pool.ToList();
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            chain.ChainPolicy.ExtraStore.AddRange(candidates.ToArray());
            chain.Build(certificate);
            foreach (var element in chain.ChainElements) candidates.Add(element.Certificate);
        }
        var path = new List<X509Certificate2> { certificate };
        var current = certificate;
        while (path.Count < RevocationLimits.MaxPathLength)
        {
            if (IsSelfSigned(current)) break;
            var issuer = candidates.FirstOrDefault(c => !c.RawData.AsSpan().SequenceEqual(current.RawData) && CertCrypto.VerifyIssuedBy(current, c));
            if (issuer is null) break;
            path.Add(issuer);
            current = issuer;
        }
        return path;
    }

    public static bool IsSelfSigned(X509Certificate2 c) => c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData) && CertCrypto.VerifyIssuedBy(c, c);
}

/// <summary>Ocena danych unieważnień dla ścieżki certyfikatu w chwili sprawdzenia.</summary>
internal static class RevocationEvaluator
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(1);

    /// <param name="timeTrusted">Czas podpisu z zaufanego znacznika: tylko wtedy unieważnienie po tym czasie nie unieważnia podpisu.</param>
    public static SignatureRevocationStatus Evaluate(IReadOnlyList<X509Certificate2> path, IReadOnlyList<byte[]> ocsp, IReadOnlyList<byte[]> crls, DateTimeOffset checkTime, bool timeTrusted)
    {
        if (path.Count == 0) return SignatureRevocationStatus.Unknown;
        var overall = SignatureRevocationStatus.Good;
        for (int i = 0; i < path.Count; i++)
        {
            var certificate = path[i];
            if (CertPath.IsSelfSigned(certificate)) continue;
            if (i + 1 >= path.Count) return overall == SignatureRevocationStatus.Revoked ? overall : SignatureRevocationStatus.Unknown; // brak wystawcy
            var status = One(certificate, path[i + 1], ocsp, crls, checkTime, timeTrusted);
            if (status == SignatureRevocationStatus.Revoked) return status;
            if (status != SignatureRevocationStatus.Good) overall = SignatureRevocationStatus.Unknown;
        }
        return overall;
    }

    public static SignatureRevocationStatus One(X509Certificate2 certificate, X509Certificate2 issuer, IEnumerable<byte[]> ocsp, IEnumerable<byte[]> crls, DateTimeOffset checkTime, bool timeTrusted)
    {
        var result = SignatureRevocationStatus.Unknown;
        foreach (var verified in ocsp.Select(d => OcspMessages.Verify(d, certificate, issuer)).Concat(crls.Select(d => CrlMessages.Verify(d, certificate, issuer))))
        {
            if (verified is null) continue;
            if (verified.ThisUpdate > DateTimeOffset.UtcNow + ClockSkew) continue;
            if (verified.Status == SignatureRevocationStatus.Revoked)
            {
                if (!timeTrusted || verified.RevokedAt is null || verified.RevokedAt <= checkTime) return SignatureRevocationStatus.Revoked;
                result = SignatureRevocationStatus.Good; // unieważniony później niż zaufany czas podpisu
                continue;
            }
            bool fresh = verified.ThisUpdate >= checkTime - Freshness || verified.NextUpdate >= checkTime;
            if (verified.Status == SignatureRevocationStatus.Good && fresh) result = SignatureRevocationStatus.Good;
        }
        return result;
    }
}

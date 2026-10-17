using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZetPDF.Core.Contracts;
using ZetPDF.Security.Pdf;
using ZetPDF.Security.Signatures;

namespace ZetPDF.Security;

/// <summary>
/// Podpisy cyfrowe: własna implementacja zapisu przyrostowego (PAdES-zgodny słownik /Sig, adbe.pkcs7.detached,
/// SHA-256, CMS przez System.Security.Cryptography.Pkcs). Zapis przyrostowy nie zmienia wcześniejszych bajtów,
/// więc nie unieważnia wcześniejszych podpisów, i zachowuje szyfrowanie pliku (AES-256/AES-128/RC4) –
/// nowe obiekty są szyfrowane kluczem pliku. Wbudowane podpisywanie PDFsharp 6.2.4 zapisuje cały plik od nowa
/// (unieważnia wcześniejsze podpisy i gubi szyfrowanie), dlatego go nie używamy.
/// </summary>
public sealed class SignatureService : ISignatureService
{
    private readonly IAppPaths _paths;
    private readonly ITimestampClient _tsa;
    private readonly ILocalizationService? _loc;

    public SignatureService(IAppPaths paths, ITimestampClient tsa, ILocalizationService? loc = null)
    {
        _paths = paths;
        _tsa = tsa;
        _loc = loc;
    }

    private CultureInfo Culture => _loc?.CurrentCulture ?? CultureInfo.CurrentUICulture;

    private string T(string key) => SecurityModule.Strings.GetString(key, Culture) ?? key;

    public async Task<byte[]> SignAsync(byte[] finalPdf, SignRequest request, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(finalPdf);
        ArgumentNullException.ThrowIfNull(request);
        var cert = request.Certificate.Certificate;
        if (!cert.HasPrivateKey) throw new CryptographicException(T("Security.Sign.Error.NoPrivateKey"));
        var time = DateTimeOffset.Now;
        Uri? tsaUrl = string.IsNullOrWhiteSpace(request.TsaUrl) ? null : new Uri(request.TsaUrl.Trim(), UriKind.Absolute);
        string signerName = cert.GetNameInfo(X509NameType.SimpleName, false);

        var sig = new CosDictionary
        {
            ["Type"] = new CosName("Sig"),
            ["Filter"] = new CosName("Adobe.PPKLite"),
            ["SubFilter"] = new CosName("adbe.pkcs7.detached"),
            ["M"] = CosString.FromText(PdfSignatureWriter.PdfDate(time)),
            ["Name"] = CosString.FromText(signerName),
        };
        if (!string.IsNullOrWhiteSpace(request.Reason)) sig["Reason"] = CosString.FromText(request.Reason);
        if (!string.IsNullOrWhiteSpace(request.Location)) sig["Location"] = CosString.FromText(request.Location);
        if (!string.IsNullOrWhiteSpace(request.ContactInfo)) sig["ContactInfo"] = CosString.FromText(request.ContactInfo);
        sig["Prop_Build"] = new CosDictionary { ["App"] = new CosDictionary { ["Name"] = new CosName("ZetPDF") } };

        var lines = BuildAppearanceLines(request, signerName, time);
        var placement = new PdfSignatureWriter.Placement { FieldName = request.FieldName, PageIndex = request.PageIndex, Rect = request.Rect };
        var chain = CmsSignatureBuilder.BuildChainOffline(cert, time).ToList();
        int reserve = cert.RawData.Length + chain.Sum(c => c.RawData.Length) + 4096 + (tsaUrl is null ? 0 : 12000);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PdfSignatureWriter.SignAsync(finalPdf, password, placement, sig,
                    ctx => SignatureAppearanceBuilder.Build(ctx.Width, ctx.Height, lines, request.Appearance.Image),
                    (data, token) => CmsSignatureBuilder.SignAsync(data, cert, time, tsaUrl is null ? null : _tsa, tsaUrl, silent: false, token),
                    reserve, ct).ConfigureAwait(false);
            }
            catch (SignatureSpaceException ex) when (attempt < 2)
            {
                reserve = ex.Required + 4096;
            }
        }
    }

    internal IReadOnlyList<SignatureAppearanceBuilder.Line> BuildAppearanceLines(SignRequest request, string signerName, DateTimeOffset time)
    {
        var a = request.Appearance;
        var culture = Culture;
        var lines = new List<SignatureAppearanceBuilder.Line>();
        if (a.ShowName) lines.Add(new(string.Format(culture, T("Security.Sign.Appearance.SignedBy"), signerName), true));
        if (a.ShowDate) lines.Add(new(string.Format(culture, T("Security.Sign.Appearance.Date"), time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)), false));
        if (a.ShowReason && !string.IsNullOrWhiteSpace(request.Reason)) lines.Add(new(string.Format(culture, T("Security.Sign.Appearance.Reason"), request.Reason), false));
        if (a.ShowLocation && !string.IsNullOrWhiteSpace(request.Location)) lines.Add(new(string.Format(culture, T("Security.Sign.Appearance.Location"), request.Location), false));
        return lines;
    }

    public Task<IReadOnlyList<SignatureValidationResult>> ValidateAsync(byte[] pdf, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var culture = Culture;
        return Task.Run(() => SignatureValidator.Validate(pdf, password, culture), ct);
    }

    public async Task<byte[]> TimestampDocumentAsync(byte[] finalPdf, string tsaUrl, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(finalPdf);
        if (string.IsNullOrWhiteSpace(tsaUrl)) throw new ArgumentException(T("Security.Timestamp.Error.NoTsa"), nameof(tsaUrl));
        var url = new Uri(tsaUrl.Trim(), UriKind.Absolute);
        var sig = new CosDictionary
        {
            ["Type"] = new CosName("DocTimeStamp"),
            ["Filter"] = new CosName("Adobe.PPKLite"),
            ["SubFilter"] = new CosName("ETSI.RFC3161"),
        };
        var placement = new PdfSignatureWriter.Placement { PageIndex = 0, Rect = default, NewFieldPrefix = "Timestamp" };
        int reserve = 16000;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PdfSignatureWriter.SignAsync(finalPdf, password, placement, sig, null,
                    async (data, token) => (await CmsSignatureBuilder.RequestTokenAsync(SHA256.HashData(data), _tsa, url, token).ConfigureAwait(false)).AsSignedCms().Encode(),
                    reserve, ct).ConfigureAwait(false);
            }
            catch (SignatureSpaceException ex) when (attempt < 2)
            {
                reserve = ex.Required + 4096;
            }
        }
    }

    public IReadOnlyList<X509Certificate2> GetStoreCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        var result = new List<X509Certificate2>();
        foreach (var c in store.Certificates)
        {
            if (!c.HasPrivateKey || c.NotAfter < now) continue;
            var ku = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (ku is not null && (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0) continue;
            result.Add(new X509Certificate2(c));
        }
        return result;
    }

    /// <summary>Wczytuje PFX/P12 (klucz tylko w pamięci – nie zostaje w magazynie Windows).</summary>
    public static X509Certificate2 LoadPfx(string path, string? password)
    {
        try
        {
            return new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            // niektóre dostawcy nie obsługują kluczy efemerycznych – zwykłe wczytanie (bez zapisu w magazynie)
            return new X509Certificate2(path, password, X509KeyStorageFlags.UserKeySet);
        }
    }

    public Task<string> CreateSelfSignedIdAsync(DigitalIdRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException(T("Security.DigitalId.Error.NameRequired"), nameof(request));
        if (string.IsNullOrEmpty(request.Password)) throw new ArgumentException(T("Security.DigitalId.Error.PasswordRequired"), nameof(request));
        return Task.Run(() =>
        {
            var pfx = CreateSelfSignedPfx(request);
            Directory.CreateDirectory(_paths.DigitalIdsDirectory);
            var baseName = string.Concat(request.Name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(_paths.DigitalIdsDirectory, baseName + ".pfx");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(_paths.DigitalIdsDirectory, $"{baseName} ({i}).pfx");
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, pfx);
            File.Move(tmp, path);
            return path;
        }, ct);
    }

    /// <summary>Certyfikat z podpisem własnym (RSA 3072, SHA-256, użycie: podpis cyfrowy + niezaprzeczalność) jako PFX.</summary>
    internal static byte[] CreateSelfSignedPfx(DigitalIdRequest request)
    {
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(request.Name.Trim());
        if (!string.IsNullOrWhiteSpace(request.Organization)) dn.AddOrganizationName(request.Organization.Trim());
        if (!string.IsNullOrWhiteSpace(request.Email)) dn.AddEmailAddress(request.Email.Trim());
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(dn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.4"),      // ochrona poczty
            new Oid("1.3.6.1.4.1.311.10.3.12"), // podpisywanie dokumentów
        }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.Now;
        using var cert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(Math.Clamp(request.ValidityYears, 1, 30)));
        return cert.Export(X509ContentType.Pkcs12, request.Password);
    }
}

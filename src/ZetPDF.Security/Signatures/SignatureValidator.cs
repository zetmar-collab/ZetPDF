using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ZetPDF.Core.Contracts;
using ZetPDF.Security.Pdf;
using ZetPDF.Core.Localization;

namespace ZetPDF.Security.Signatures;

/// <summary>
/// Weryfikacja podpisów: dla każdego pola podpisu – zgodność skrótu zakresów /ByteRange z podpisem CMS,
/// zgodność /Contents z luką w ByteRange (ochrona przed podmianą), pokrycie całego pliku (późniejsze rewizje),
/// ważność certyfikatu w chwili podpisu, łańcuch zaufania z magazynów Windows BEZ pobierania CRL/OCSP
/// i certyfikatów z sieci, znacznik czasu (atrybut signatureTimeStampToken lub DocTimeStamp).
/// </summary>
internal static class SignatureValidator
{
    public static IReadOnlyList<SignatureValidationResult> Validate(byte[] pdf, string? password, CultureInfo culture)
    {
        var doc = CosDocument.Open(pdf, password);
        var pages = doc.GetPageReferences();
        var results = new List<SignatureValidationResult>();
        var dss = DssData.Read(doc);
        foreach (var field in doc.GetFields().Where(f => f.FieldType == "Sig"))
        {
            var sigDict = doc.ResolveDict(field.Dictionary["V"]);
            if (sigDict is null) continue; // pole puste
            int page = field.Widgets.Count > 0 ? doc.FindWidgetPage(field.Widgets[0], pages) : -1;
            var rect = field.Widgets.Count > 0 ? CosDocument.ToRect(doc.Resolve(doc.ResolveDict(field.Widgets[0])?["Rect"])) : default;
            SignatureValidationResult result;
            try
            {
                result = ValidateOne(doc, pdf, field.FullName, sigDict, culture, password, dss);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or NotSupportedException)
            {
                result = new SignatureValidationResult
                {
                    FieldName = field.FullName,
                    Status = SignatureStatus.Error,
                    Details = Text(culture, "Security.Validate.Detail.Error", ErrorText(culture, ex)),
                };
            }
            results.Add(result with { PageIndex = page, Rect = rect });
        }
        return results;
    }

    /// <summary>Bajty /Contents z luki w /ByteRange, razem z dopełnieniem zerami (klucz /VRI). Działa też w plikach zaszyfrowanych.</summary>
    internal static byte[] ContentsBytes(CosDocument doc, byte[] pdf, CosDictionary sig)
    {
        var br = (doc.Resolve(sig["ByteRange"]) as CosArray)?.Items.Select(i => (doc.Resolve(i) as CosNumber)?.AsLong ?? -1).ToArray();
        if (br is null || br.Length != 4 || br.Any(v => v < 0) || br[0] != 0 || br[0] + br[1] > br[2] || br[2] + br[3] > pdf.LongLength) throw new FormatException("Bad ByteRange.");
        int gapStart = (int)(br[0] + br[1]), gapEnd = (int)br[2];
        while (gapStart < gapEnd && CosParser.IsWhitespace(pdf[gapStart])) gapStart++;
        while (gapEnd > gapStart && CosParser.IsWhitespace(pdf[gapEnd - 1])) gapEnd--;
        if (gapEnd - gapStart < 2 || pdf[gapStart] != '<' || pdf[gapEnd - 1] != '>') throw new FormatException("Bad ByteRange.");
        return HexToBytes(pdf, gapStart + 1, gapEnd - 1);
    }

    private static bool AnyDocTimestampAfter(CosDocument doc, long end)
    {
        foreach (var field in doc.GetFields().Where(f => f.FieldType == "Sig"))
        {
            var sig = doc.ResolveDict(field.Dictionary["V"]);
            if (sig?.GetName("Type") != "DocTimeStamp") continue;
            var br = (doc.Resolve(sig["ByteRange"]) as CosArray)?.Items.Select(i => (doc.Resolve(i) as CosNumber)?.AsLong ?? -1).ToArray();
            if (br is { Length: 4 } && br[2] + br[3] > end) return true;
        }
        return false;
    }

    private static SignatureValidationResult ValidateOne(CosDocument doc, byte[] pdf, string fieldName, CosDictionary sig, CultureInfo culture, string? password, DssData dss)
    {
        var details = new List<string>();
        var br = (doc.Resolve(sig["ByteRange"]) as CosArray)?.Items.Select(i => (doc.Resolve(i) as CosNumber)?.AsLong ?? -1).ToArray();
        if (br is null || br.Length != 4 || br.Any(v => v < 0) || br[0] != 0 || br[0] + br[1] > br[2] || br[2] + br[3] > pdf.LongLength)
            throw new FormatException(Text(culture, "Security.Validate.Detail.BadByteRange")).MarkLocalized();

        // /Contents z luki w ByteRange (czytamy surowe bajty pliku – działa też w plikach zaszyfrowanych)
        int gapStart = (int)(br[0] + br[1]), gapEnd = (int)br[2];
        while (gapStart < gapEnd && CosParser.IsWhitespace(pdf[gapStart])) gapStart++;
        while (gapEnd > gapStart && CosParser.IsWhitespace(pdf[gapEnd - 1])) gapEnd--;
        if (gapEnd - gapStart < 2 || pdf[gapStart] != '<' || pdf[gapEnd - 1] != '>')
            throw new FormatException(Text(culture, "Security.Validate.Detail.BadByteRange")).MarkLocalized();
        var contents = HexToBytes(pdf, gapStart + 1, gapEnd - 1);
        if (sig["Contents"] is CosString cs && !TrimDer(cs.Bytes).AsSpan().SequenceEqual(TrimDer(contents)))
            throw new FormatException(Text(culture, "Security.Validate.Detail.ContentsMismatch")).MarkLocalized();
        var der = TrimDer(contents);

        var signed = new byte[br[1] + br[3]];
        Buffer.BlockCopy(pdf, 0, signed, 0, (int)br[1]);
        Buffer.BlockCopy(pdf, (int)br[2], signed, (int)br[1], (int)br[3]);

        long end = br[2] + br[3];
        bool covers = end == pdf.LongLength || pdf.AsSpan((int)end).ToArray().All(b => CosParser.IsWhitespace(b));
        var subFilter = sig.GetName("SubFilter") ?? string.Empty;
        var mTime = PdfSignatureWriter.ParsePdfDate((doc.Resolve(sig["M"]) as CosString)?.Text);

        bool integrity;
        X509Certificate2? cert = null;
        X509Certificate2Collection extra = new();
        DateTimeOffset? signingTime = mTime;
        bool hasTs = false;
        DateTimeOffset? tsTime = null;

        if (subFilter == "ETSI.RFC3161" || sig.GetName("Type") == "DocTimeStamp")
        {
            if (!Rfc3161TimestampToken.TryDecode(der, out var token, out _) || token is null)
                throw new CryptographicException(Text(culture, "Security.Validate.Detail.BadToken")).MarkLocalized();
            integrity = token.VerifySignatureForData(signed, out cert, null);
            hasTs = true;
            tsTime = token.TokenInfo.Timestamp;
            signingTime = tsTime;
            extra.AddRange(token.AsSignedCms().Certificates);
            details.Add(Text(culture, "Security.Validate.Detail.DocTimestamp"));
        }
        else if (subFilter is "adbe.pkcs7.detached" or "ETSI.CAdES.detached" or "adbe.pkcs7.sha1" || subFilter.Length == 0)
        {
            var cms = new SignedCms();
            cms.Decode(der);
            bool sha1Embedded = subFilter == "adbe.pkcs7.sha1" || (!cms.Detached && cms.ContentInfo.Content.Length > 0);
            if (sha1Embedded)
            {
                integrity = SHA1.HashData(signed).AsSpan().SequenceEqual(cms.ContentInfo.Content);
            }
            else
            {
                cms = new SignedCms(new ContentInfo(signed), detached: true);
                cms.Decode(der);
                integrity = true;
            }
            extra.AddRange(cms.Certificates);
            var si = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : throw new CryptographicException("No signer.").WithMessageKey("Security.Error.NoSigner");
            cert = si.Certificate;
            try { si.CheckSignature(verifySignatureOnly: true); }
            catch (CryptographicException) { integrity = false; }

            foreach (var attr in si.SignedAttributes)
                foreach (var v in attr.Values)
                    if (v is Pkcs9SigningTime st) signingTime = new DateTimeOffset(DateTime.SpecifyKind(st.SigningTime, DateTimeKind.Utc));

            foreach (var attr in si.UnsignedAttributes)
            {
                if (attr.Oid.Value != CmsSignatureBuilder.SignatureTimeStampTokenOid || attr.Values.Count == 0) continue;
                if (Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _) && token is not null)
                {
                    if (token.VerifySignatureForSignerInfo(si, out _))
                    {
                        hasTs = true;
                        tsTime = token.TokenInfo.Timestamp;
                    }
                    else details.Add(Text(culture, "Security.Validate.Detail.TimestampInvalid"));
                }
            }
        }
        else
        {
            throw new NotSupportedException(Text(culture, "Security.Validate.Detail.UnsupportedSubFilter", subFilter)).MarkLocalized();
        }

        var checkTime = tsTime ?? signingTime ?? DateTimeOffset.Now;
        // Dane z /DSS, bez sieci: uznane tylko po weryfikacji kryptograficznej przez wystawcę z łańcucha.
        var revocation = SignatureRevocationStatus.NotChecked;
        bool isDocTs = subFilter == "ETSI.RFC3161" || sig.GetName("Type") == "DocTimeStamp";
        if (dss.Any && !isDocTs && cert is not null)
        {
            var pool = extra.Cast<X509Certificate2>().Concat(dss.ParsedCertificates());
            revocation = RevocationEvaluator.Evaluate(CertPath.Build(cert, pool), dss.Ocsp, dss.Crls, checkTime, hasTs);
            extra.AddRange(dss.ParsedCertificates().ToArray());
        }
        bool onlyLongTerm = !covers && LongTermRevision.OnlyLongTermAfter(doc, pdf, end, password);
        bool validAtTime = cert is not null && checkTime.UtcDateTime >= cert.NotBefore.ToUniversalTime() && checkTime.UtcDateTime <= cert.NotAfter.ToUniversalTime();
        bool trusted = cert is not null && IsTrusted(cert, extra, checkTime);

        details.Add(Text(culture, integrity ? "Security.Validate.Detail.IntegrityOk" : "Security.Validate.Detail.IntegrityFailed"));
        details.Add(Text(culture, covers ? "Security.Validate.Detail.Covers" : onlyLongTerm ? "Security.Validate.Detail.OnlyLongTermAfter" : "Security.Validate.Detail.ModifiedAfter"));
        if (revocation != SignatureRevocationStatus.NotChecked) details.Add(Text(culture, "Security.Validate.Detail.Revocation." + revocation));
        if (cert is null) details.Add(Text(culture, "Security.Validate.Detail.NoCertificate"));
        else
        {
            details.Add(Text(culture, validAtTime ? "Security.Validate.Detail.CertValid" : "Security.Validate.Detail.CertNotValid",
                cert.NotBefore.ToString("g", culture), cert.NotAfter.ToString("g", culture)));
            details.Add(Text(culture, trusted ? "Security.Validate.Detail.Trusted" : "Security.Validate.Detail.Untrusted"));
        }
        if (hasTs && tsTime is { } t) details.Add(Text(culture, "Security.Validate.Detail.Timestamp", t.ToLocalTime().ToString("G", culture)));
        details.Add(Text(culture, "Security.Validate.Detail.Offline"));

        string level = isDocTs || subFilter != "ETSI.CAdES.detached" ? string.Empty
            : !hasTs ? "B-B"
            : revocation != SignatureRevocationStatus.Good ? "B-T"
            : AnyDocTimestampAfter(doc, end) ? "B-LTA" : "B-LT";
        if (level.Length > 0) details.Add(Text(culture, "Security.Validate.Detail.Level", level));

        var status = !integrity ? SignatureStatus.Invalid
            : cert is null ? SignatureStatus.UnknownSigner
            : !validAtTime ? SignatureStatus.Invalid
            : revocation == SignatureRevocationStatus.Revoked ? SignatureStatus.Invalid
            : !covers && !onlyLongTerm ? SignatureStatus.ValidButModifiedAfter
            : !trusted ? SignatureStatus.UnknownSigner
            : SignatureStatus.Valid;

        return new SignatureValidationResult
        {
            FieldName = fieldName,
            Status = status,
            SignerName = cert?.GetNameInfo(X509NameType.SimpleName, false) ?? (doc.Resolve(sig["Name"]) as CosString)?.Text ?? string.Empty,
            Issuer = cert?.GetNameInfo(X509NameType.SimpleName, true) ?? string.Empty,
            SigningTime = signingTime,
            HasTimestamp = hasTs,
            TimestampTime = tsTime,
            IntegrityOk = integrity,
            CoversWholeDocument = covers,
            CertificateValidAtSigningTime = validAtTime,
            ChainTrusted = trusted,
            RevocationStatus = revocation,
            HasDss = dss.Any,
            ModifiedOnlyByLongTermData = onlyLongTerm,
            BaselineLevel = level,
            Details = string.Join(Environment.NewLine, details),
        };
    }

    /// <summary>Łańcuch z magazynów Windows + certyfikatów z podpisu; bez sieci (CRL/OCSP/AIA wyłączone).</summary>
    public static bool IsTrusted(X509Certificate2 cert, X509Certificate2Collection extra, DateTimeOffset time)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = time.LocalDateTime;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreCtlNotTimeValid | X509VerificationFlags.IgnoreNotTimeNested;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        return chain.Build(cert);
    }

    private static byte[] HexToBytes(byte[] data, int start, int end)
    {
        var list = new List<byte>((end - start) / 2);
        int hi = -1;
        for (int i = start; i < end; i++)
        {
            byte c = data[i];
            if (!CosParser.IsHex(c)) continue;
            if (hi < 0) hi = CosParser.HexVal(c);
            else { list.Add((byte)(hi * 16 + CosParser.HexVal(c))); hi = -1; }
        }
        return list.ToArray();
    }

    /// <summary>Odcina dopełnienie zerami za strukturą DER (miejsce zarezerwowane jest większe niż podpis).</summary>
    public static byte[] TrimDer(byte[] der)
    {
        if (der.Length < 2 || der[0] != 0x30) return der;
        int lenByte = der[1];
        long len;
        int header;
        if (lenByte < 0x80) { len = lenByte; header = 2; }
        else
        {
            int n = lenByte & 0x7F;
            if (n == 0 || n > 4 || der.Length < 2 + n) return der;
            len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | der[2 + i];
            header = 2 + n;
        }
        long total = header + len;
        return total <= der.Length ? der.AsSpan(0, (int)total).ToArray() : der;
    }

    /// <summary>Opis błędu w języku raportu (O-3): klucz z wyjątku, tekst już przetłumaczony albo ogólny opis.</summary>
    private static string ErrorText(CultureInfo culture, Exception ex) =>
        LocalizedError.TryGetMessageKey(ex, out var key, out var args) ? Text(culture, key, args)
        : LocalizedError.IsLocalized(ex) ? ex.Message
        : Text(culture, "Security.Validate.Detail.Malformed");

    private static string Text(CultureInfo culture, string key, params object?[] args)
    {
        var s = SecurityModule.Strings.GetString(key, culture) ?? key;
        return args.Length == 0 ? s : string.Format(culture, s, args);
    }
}

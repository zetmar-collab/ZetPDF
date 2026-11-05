using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Security.Pdf;

namespace ZetPDF.Security.Signatures;

/// <summary>Dane z /DSS dokumentu, z limitami (dane z pliku są niezaufane).</summary>
internal sealed record DssData(IReadOnlyList<byte[]> Certificates, IReadOnlyList<byte[]> Ocsp, IReadOnlyList<byte[]> Crls)
{
    public const int MaxStreams = 512;
    public const int MaxBytes = 64 * 1024 * 1024;

    public bool Any => Certificates.Count + Ocsp.Count + Crls.Count > 0;

    public static DssData Read(CosDocument doc)
    {
        if (doc.ResolveDict(doc.Catalog["DSS"]) is not { } dss) return new DssData(Array.Empty<byte[]>(), Array.Empty<byte[]>(), Array.Empty<byte[]>());
        long budget = MaxBytes;
        List<byte[]> Streams(string key)
        {
            var list = new List<byte[]>();
            if (doc.Resolve(dss[key]) is not CosArray array) return list;
            foreach (var item in array.Items.Take(MaxStreams))
            {
                if (doc.Resolve(item) is not CosStream stream) continue;
                try
                {
                    var data = CosFilters.Decode(stream, doc.Resolve);
                    budget -= data.Length;
                    if (budget < 0) break;
                    list.Add(data);
                }
                catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidDataException or ZetPDF.Core.Documents.PdfContentLimitException) { }
            }
            return list;
        }
        return new DssData(Streams("Certs"), Streams("OCSPs"), Streams("CRLs"));
    }

    public IEnumerable<X509Certificate2> ParsedCertificates()
    {
        foreach (var der in Certificates)
        {
            X509Certificate2? c = null;
            try { c = new X509Certificate2(der); }
            catch (CryptographicException) { }
            if (c is not null) yield return c;
        }
    }
}

/// <summary>Dopisanie danych do /DSS przyrostowo: oryginalne bajty bez zmian, pliki zaszyfrowane zostają zaszyfrowane.</summary>
internal static class DssWriter
{
    public sealed record VriEntry(string Key, IReadOnlyList<byte[]> Certificates, IReadOnlyList<byte[]> Ocsp, IReadOnlyList<byte[]> Crls);

    public static byte[] Write(byte[] pdf, string? password, IEnumerable<VriEntry> entries)
    {
        var doc = CosDocument.Open(pdf, password);
        if (doc.IsEncrypted && doc.Security?.FileKey is null) throw new PdfPasswordRequiredException();
        var upd = new IncrementalUpdate(doc);
        var old = doc.ResolveDict(doc.Catalog["DSS"]);

        // Istniejące strumienie zostają; te same dane nie są zapisywane drugi raz.
        var known = new Dictionary<string, CosReference>(StringComparer.Ordinal);
        CosArray Existing(string key)
        {
            var result = new CosArray();
            if (old is not null && doc.Resolve(old[key]) is CosArray array)
                foreach (var item in array.Items.Take(DssData.MaxStreams))
                {
                    result.Add(item);
                    if (item is CosReference r && doc.Resolve(r) is CosStream s)
                    {
                        try { known[key + Hash(CosFilters.Decode(s, doc.Resolve))] = r; }
                        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidDataException or ZetPDF.Core.Documents.PdfContentLimitException) { }
                    }
                }
            return result;
        }
        var certs = Existing("Certs");
        var ocsps = Existing("OCSPs");
        var crls = Existing("CRLs");
        CosReference Stream(string key, CosArray target, byte[] data)
        {
            var id = key + Hash(data);
            if (known.TryGetValue(id, out var existing)) return existing;
            var reference = upd.Add(new CosStream(new CosDictionary { ["Filter"] = new CosName("FlateDecode") }, CosFilters.Deflate(data)));
            known[id] = reference;
            target.Add(reference);
            return reference;
        }

        var vri = new CosDictionary();
        if (old is not null && doc.Resolve(old["VRI"]) is CosDictionary oldVri)
            foreach (var e in oldVri.Entries) vri[e.Key] = e.Value;
        foreach (var entry in entries)
        {
            var item = new CosDictionary { ["Type"] = new CosName("VRI") };
            if (entry.Certificates.Count > 0) item["Cert"] = new CosArray(entry.Certificates.Select(d => (CosObject)Stream("Certs", certs, d)));
            if (entry.Ocsp.Count > 0) item["OCSP"] = new CosArray(entry.Ocsp.Select(d => (CosObject)Stream("OCSPs", ocsps, d)));
            if (entry.Crls.Count > 0) item["CRL"] = new CosArray(entry.Crls.Select(d => (CosObject)Stream("CRLs", crls, d)));
            vri[entry.Key] = upd.Add(item);
        }

        var dss = new CosDictionary { ["Type"] = new CosName("DSS"), ["VRI"] = upd.Add(vri) };
        if (certs.Count > 0) dss["Certs"] = certs;
        if (ocsps.Count > 0) dss["OCSPs"] = ocsps;
        if (crls.Count > 0) dss["CRLs"] = crls;

        var catalog = (CosDictionary)doc.Catalog.Clone();
        catalog["DSS"] = upd.Add(dss);
        var extensions = doc.Resolve(catalog["Extensions"]) is CosDictionary ex ? (CosDictionary)ex.Clone() : new CosDictionary();
        extensions["ESIC"] = new CosDictionary { ["BaseVersion"] = new CosName("1.7"), ["ExtensionLevel"] = new CosNumber(5L) };
        catalog["Extensions"] = extensions;
        upd.Set(doc.CatalogReference, catalog);
        return upd.Write();
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

/// <summary>Czy po podpisie doszły tylko dane długoterminowe: /DSS, oznaczenie rozszerzenia i znaczniki czasu dokumentu.</summary>
internal static class LongTermRevision
{
    private const int MaxObjects = 200_000;

    public static bool OnlyLongTermAfter(CosDocument current, byte[] pdf, long signedEnd, string? password)
    {
        try
        {
            if (signedEnd <= 0 || signedEnd > pdf.Length) return false;
            var earlierBytes = new byte[signedEnd];
            Buffer.BlockCopy(pdf, 0, earlierBytes, 0, (int)signedEnd);
            var earlier = CosDocument.Open(earlierBytes, password);
            var numbers = earlier.ObjectNumbers.Take(MaxObjects + 1).ToList();
            if (numbers.Count > MaxObjects) return false;
            int catalogNumber = earlier.CatalogReference.Number;
            if (current.CatalogReference.Number != catalogNumber) return false;
            if (!SameTrailerInfo(earlier, current)) return false;
            if (DefinesDanglingReference(earlier, current, numbers)) return false;
            foreach (int n in numbers)
            {
                if (!earlier.Exists(n)) continue;
                if (!current.Exists(n)) return false;
                var before = earlier.GetObject(n);
                var after = current.GetObject(n);
                if (Same(before, after)) continue;
                if (n == catalogNumber)
                {
                    if (!CatalogOnlyLongTerm((CosDictionary)before, (CosDictionary)after, earlier, current)) return false;
                }
                else if (!OnlyNewTimestampFields(before, after, earlier, current)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException or PdfCorruptException or PdfPasswordRequiredException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return false; // niepewność to brak zwolnienia
        }
    }

    private static bool SameTrailerInfo(CosDocument earlier, CosDocument current) => Same(earlier.Trailer["Info"], current.Trailer["Info"]);

    /// <summary>
    /// Cień: obiekt zdefiniowany dopiero po podpisie, do którego wcześniejsza rewizja już się odwołuje (jako do niezdefiniowanego, czyli null). Zmienia on znaczenie
    /// podpisanej treści, choć żaden istniejący obiekt się nie zmienił. Skan obejmuje wszystkie obiekty wcześniejszej rewizji i jej trailer, bez rekurencji.
    /// </summary>
    private static bool DefinesDanglingReference(CosDocument earlier, CosDocument current, List<int> earlierNumbers)
    {
        var added = new HashSet<int>(current.ObjectNumbers.Where(n => current.Exists(n) && !earlier.Exists(n)));
        if (added.Count == 0) return false;
        var stack = new Stack<CosObject>();
        stack.Push(earlier.Trailer);
        foreach (int n in earlierNumbers)
            if (earlier.Exists(n)) stack.Push(earlier.GetObject(n));
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case CosReference r:
                    if (added.Contains(r.Number)) return true;
                    break;
                case CosArray a:
                    foreach (var item in a.Items) stack.Push(item);
                    break;
                case CosDictionary d:
                    foreach (var e in d.Entries) stack.Push(e.Value);
                    break;
                case CosStream s:
                    stack.Push(s.Dictionary);
                    break;
            }
        }
        return false;
    }

    private static bool CatalogOnlyLongTerm(CosDictionary before, CosDictionary after, CosDocument earlier, CosDocument current)
    {
        foreach (var key in before.Keys.Union(after.Keys))
        {
            if (key is "DSS" or "Extensions") continue;
            var a = before[key];
            var b = after[key];
            if (a is null || b is null)
            {
                // nowy /AcroForm tylko z polem znacznika czasu
                if (key == "AcroForm" && a is null && current.ResolveDict(b) is { } form && AllTimestampFields(form["Fields"], earlier, current)) continue;
                return false;
            }
            if (!Same(a, b) && !OnlyNewTimestampFields(a, b, earlier, current)) return false;
        }
        return true;
    }

    private static bool AllTimestampFields(CosObject? fields, CosDocument earlier, CosDocument current) =>
        current.Resolve(fields) is CosArray array && array.Items.All(i => IsNewTimestampField(i, earlier, current));

    /// <summary>Słownik lub tablica, w której doszły wyłącznie nowe pola znacznika czasu (/Fields, /Annots), a /SigFlags może się zmienić.</summary>
    private static bool OnlyNewTimestampFields(CosObject before, CosObject after, CosDocument earlier, CosDocument current)
    {
        if (before is CosArray x && after is CosArray y)
        {
            if (y.Count <= x.Count) return false;
            for (int i = 0; i < x.Count; i++)
                if (!Same(x[i], y[i])) return false;
            for (int i = x.Count; i < y.Count; i++)
                if (!IsNewTimestampField(y[i], earlier, current)) return false;
            return true;
        }
        if (before is CosDictionary a && after is CosDictionary b)
        {
            foreach (var key in a.Keys.Union(b.Keys))
            {
                if (key == "SigFlags") continue;
                var p = a[key];
                var q = b[key];
                if (p is null && q is not null && key == "Annots" && current.Resolve(q) is CosArray added && added.Items.All(i => IsNewTimestampField(i, earlier, current))) continue;
                if (p is null || q is null) return false;
                if (Same(p, q)) continue;
                var rp = earlier.Resolve(p);
                var rq = current.Resolve(q);
                if (rp is null || rq is null || !OnlyNewTimestampFields(rp, rq, earlier, current)) return false;
            }
            return true;
        }
        return false;
    }

    private static bool IsNewTimestampField(CosObject item, CosDocument earlier, CosDocument current)
    {
        if (item is not CosReference r || earlier.Exists(r.Number)) return false;
        var field = current.ResolveDict(r);
        // tylko klucze widżetu podpisu: pole z /A, /AA lub inną treścią to nie jest znacznik czasu
        if (field is null || field.Keys.Any(k => k is not ("Type" or "Subtype" or "FT" or "T" or "F" or "Rect" or "P" or "V" or "AP"))) return false;
        return current.ResolveDict(field["V"])?.GetName("Type") == "DocTimeStamp" && IsInvisible(field, current);
    }

    /// <summary>Znacznik czasu nie zmienia wyglądu: prostokąt pusty, a wygląd (zwykle pusty formularz) bez znaczenia dla strony o zerowym rozmiarze.</summary>
    private static bool IsInvisible(CosDictionary field, CosDocument current)
    {
        if (current.Resolve(field["Rect"]) is not CosArray rect || rect.Count != 4) return false;
        double Value(int i) => (current.Resolve(rect[i]) as CosNumber)?.AsDouble ?? 1;
        return Math.Abs(Value(2) - Value(0)) < 1e-6 || Math.Abs(Value(3) - Value(1)) < 1e-6;
    }

    /// <summary>Porównanie strukturalne; głębokość ograniczona przez parser COS.</summary>
    private static bool Same(CosObject? a, CosObject? b)
    {
        switch (a, b)
        {
            case (null, null): return true;
            case (CosName x, CosName y): return x.Value == y.Value;
            case (CosNumber x, CosNumber y): return x.Raw == y.Raw;
            case (CosBoolean x, CosBoolean y): return x.Value == y.Value;
            case (CosNull, CosNull): return true;
            case (CosReference x, CosReference y): return x.Equals(y);
            case (CosString x, CosString y): return x.Bytes.AsSpan().SequenceEqual(y.Bytes);
            case (CosArray x, CosArray y):
                if (x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++)
                    if (!Same(x[i], y[i])) return false;
                return true;
            case (CosDictionary x, CosDictionary y):
                if (x.Entries.Count != y.Entries.Count) return false;
                foreach (var e in x.Entries)
                    if (!Same(e.Value, y[e.Key])) return false;
                return true;
            case (CosStream x, CosStream y):
                return x.Data.AsSpan().SequenceEqual(y.Data) && Same(x.Dictionary, y.Dictionary);
            default:
                return false;
        }
    }
}

/// <summary>
/// Dane długoterminowe (PAdES): pobranie OCSP i CRL, weryfikacja, dopisanie do /DSS. Połączenie tylko pod adresy z certyfikatów podpisów, które wskazano, lub
/// wpisane przez użytkownika; okno pokazuje je przed uruchomieniem.
/// </summary>
public sealed class LongTermService : ILongTermSignatureService
{
    private readonly IRevocationTransport _transport;

    public LongTermService(IRevocationTransport transport) => _transport = transport;

    private sealed record SignatureInfo(string FieldName, X509Certificate2? Certificate, X509Certificate2Collection Extra, byte[] Contents, bool IsDocTimestamp);

    private static List<SignatureInfo> ReadSignatures(CosDocument doc, byte[] pdf)
    {
        var list = new List<SignatureInfo>();
        foreach (var field in doc.GetFields().Where(f => f.FieldType == "Sig").Take(1024))
        {
            var sig = doc.ResolveDict(field.Dictionary["V"]);
            if (sig is null) continue;
            try
            {
                var contents = SignatureValidator.ContentsBytes(doc, pdf, sig);
                var der = SignatureValidator.TrimDer(contents);
                var extra = new X509Certificate2Collection();
                X509Certificate2? cert = null;
                bool isDocTs = sig.GetName("Type") == "DocTimeStamp" || sig.GetName("SubFilter") == "ETSI.RFC3161";
                var cms = new SignedCms();
                cms.Decode(der);
                extra.AddRange(cms.Certificates);
                if (cms.SignerInfos.Count > 0)
                {
                    var si = cms.SignerInfos[0];
                    cert = si.Certificate;
                    foreach (var attr in si.UnsignedAttributes)
                    {
                        if (attr.Oid.Value != CmsSignatureBuilder.SignatureTimeStampTokenOid || attr.Values.Count == 0) continue;
                        var token = new SignedCms();
                        token.Decode(attr.Values[0].RawData);
                        extra.AddRange(token.Certificates);
                    }
                }
                list.Add(new SignatureInfo(field.FullName, cert, extra, contents, isDocTs));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or InvalidOperationException)
            {
                list.Add(new SignatureInfo(field.FullName, null, new X509Certificate2Collection(), Array.Empty<byte>(), false));
            }
        }
        return list;
    }

    private static IEnumerable<X509Certificate2> Pool(SignatureInfo info, DssData dss) => info.Extra.Cast<X509Certificate2>().Concat(dss.ParsedCertificates());

    public IReadOnlyList<string> GetCertificateUrls(X509Certificate2 certificate)
    {
        var path = CertPath.Build(certificate, Array.Empty<X509Certificate2>());
        return path.Where(c => !CertPath.IsSelfSigned(c)).SelectMany(c => RevocationSources.From(c).All).Distinct().Select(u => u.AbsoluteUri).ToList();
    }

    public Task<IReadOnlyList<string>> GetDocumentUrlsAsync(byte[] pdf, string? password, CancellationToken ct = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var doc = CosDocument.Open(pdf, password);
        var dss = DssData.Read(doc);
        var urls = new List<string>();
        foreach (var info in ReadSignatures(doc, pdf).Where(s => !s.IsDocTimestamp && s.Certificate is not null))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var c in CertPath.Build(info.Certificate!, Pool(info, dss)).Where(c => !CertPath.IsSelfSigned(c)))
                urls.AddRange(RevocationSources.From(c).All.Select(u => u.AbsoluteUri));
        }
        return urls.Distinct().ToList();
    }, ct);

    public async Task<LongTermResult> AddLongTermDataAsync(byte[] pdf, string? password, LongTermConnectionPlan plan, CancellationToken ct = default)
    {
        var run = await CollectAsync(pdf, password, plan, ct).ConfigureAwait(false);
        var output = run.Entries.Count == 0 ? pdf : DssWriter.Write(pdf, password, run.Entries);
        return run.Result with { Pdf = output };
    }

    public async Task<LongTermResult> CheckOnlineAsync(byte[] pdf, string? password, LongTermConnectionPlan plan, CancellationToken ct = default) =>
        (await CollectAsync(pdf, password, plan, ct).ConfigureAwait(false)).Result;

    private sealed record Run(LongTermResult Result, List<DssWriter.VriEntry> Entries);

    /// <summary>Granica połączeń jednego przebiegu: tylko zatwierdzone adresy; pozostałe trafiają na listę do kolejnej zgody i nie są wywoływane.</summary>
    private sealed class Gate(IEnumerable<string> approved)
    {
        private readonly HashSet<string> _approved = new(approved, StringComparer.Ordinal);
        private readonly List<string> _pending = new();

        public IReadOnlyList<string> Pending => _pending;

        public bool Allows(Uri url)
        {
            if (_approved.Contains(url.AbsoluteUri)) return true;
            if (_pending.Count < MaxPendingUrls && !_pending.Contains(url.AbsoluteUri)) _pending.Add(url.AbsoluteUri);
            return false;
        }
    }

    private const int MaxPendingUrls = 32;
    private const int MaxDownloadedCertificates = 8;

    private async Task<Run> CollectAsync(byte[] pdf, string? password, LongTermConnectionPlan plan, CancellationToken ct)
    {
        var extras = new List<Uri>();
        foreach (var text in plan.Extra.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) || !RevocationSources.IsAllowed(u))
                throw new ArgumentException("Only http and https addresses are allowed.", nameof(plan)).WithMessageKey("Security.LongTerm.Error.Scheme");
            extras.Add(u);
        }
        var gate = new Gate(plan.Approved.Concat(extras.Select(u => u.AbsoluteUri)));
        var doc = CosDocument.Open(pdf, password);
        var dss = DssData.Read(doc);
        var signatures = ReadSignatures(doc, pdf);
        var problems = new List<string>();
        var statuses = new List<LongTermSignatureStatus>();
        var entries = new List<DssWriter.VriEntry>();
        var downloadedIssuers = new List<X509Certificate2>();
        int certCount = 0, ocspCount = 0, crlCount = 0;

        foreach (var info in signatures.Where(s => !s.IsDocTimestamp))
        {
            ct.ThrowIfCancellationRequested();
            if (info.Certificate is null)
            {
                statuses.Add(new LongTermSignatureStatus(info.FieldName, SignatureRevocationStatus.Unknown));
                continue;
            }
            var path = await BuildPathAsync(info, dss, downloadedIssuers, gate, plan.AllowLocalNetwork, problems, ct).ConfigureAwait(false);
            var ocsp = new List<byte[]>();
            var crls = new List<byte[]>();
            var overall = SignatureRevocationStatus.Good;
            for (int i = 0; i < path.Count; i++)
            {
                var c = path[i];
                if (CertPath.IsSelfSigned(c)) continue;
                if (i + 1 >= path.Count)
                {
                    overall = overall == SignatureRevocationStatus.Revoked ? overall : SignatureRevocationStatus.Unknown;
                    problems.Add(c.Subject + ": no issuer");
                    continue;
                }
                var status = await FetchOneAsync(c, path[i + 1], extras, gate, plan.AllowLocalNetwork, ocsp, crls, problems, ct).ConfigureAwait(false);
                if (status == SignatureRevocationStatus.Revoked) overall = status;
                else if (status != SignatureRevocationStatus.Good && overall != SignatureRevocationStatus.Revoked) overall = SignatureRevocationStatus.Unknown;
            }
            statuses.Add(new LongTermSignatureStatus(info.FieldName, overall));
            var certs = path.Select(c => c.RawData).Concat(info.Extra.Cast<X509Certificate2>().Select(c => c.RawData)).DistinctBy(Convert.ToHexString).ToList();
            certCount += certs.Count;
            ocspCount += ocsp.Count;
            crlCount += crls.Count;
            entries.Add(new DssWriter.VriEntry(Convert.ToHexString(SHA1.HashData(info.Contents)), certs, ocsp, crls));
        }
        var worst = statuses.Count == 0 ? SignatureRevocationStatus.Unknown
            : statuses.Any(s => s.Status == SignatureRevocationStatus.Revoked) ? SignatureRevocationStatus.Revoked
            : statuses.All(s => s.Status == SignatureRevocationStatus.Good) ? SignatureRevocationStatus.Good : SignatureRevocationStatus.Unknown;
        return new Run(new LongTermResult(pdf, certCount, ocspCount, crlCount, worst, statuses, problems) { PendingUrls = gate.Pending.ToList() }, entries);
    }

    /// <summary>Ścieżka z wystawcami z podpisu, DSS i magazynów; brakujących szuka pod adresem wystawcy z certyfikatu, a pobrany musi podpisywać potomka.</summary>
    private async Task<List<X509Certificate2>> BuildPathAsync(SignatureInfo info, DssData dss, List<X509Certificate2> downloaded, Gate gate, bool allowLocal, List<string> problems, CancellationToken ct)
    {
        var pool = Pool(info, dss).Concat(downloaded).ToList();
        var path = CertPath.Build(info.Certificate!, pool);
        while (path.Count < RevocationLimits.MaxPathLength && !CertPath.IsSelfSigned(path[^1]))
        {
            bool found = false;
            foreach (var url in RevocationSources.From(path[^1]).Issuers)
            {
                ct.ThrowIfCancellationRequested();
                if (!gate.Allows(url)) continue;
                if (downloaded.Count >= MaxDownloadedCertificates)
                {
                    problems.Add(url.AbsoluteUri + ": too many downloaded certificates");
                    break;
                }
                try
                {
                    var data = await _transport.GetAsync(url, allowLocal, ct).ConfigureAwait(false);
                    if (data.Length > RevocationLimits.MaxResponseBytes) continue;
                    var candidate = new X509Certificate2(data);
                    if (!CertCrypto.VerifyIssuedBy(path[^1], candidate)) continue;
                    downloaded.Add(candidate);
                    path.Add(candidate);
                    found = true;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    problems.Add(url.AbsoluteUri + ": " + ex.Message);
                }
            }
            if (!found) break;
        }
        return path;
    }

    /// <summary>OCSP, potem CRL. Do wyniku trafia tylko odpowiedź po weryfikacji kryptograficznej przez wystawcę z łańcucha.</summary>
    private async Task<SignatureRevocationStatus> FetchOneAsync(X509Certificate2 certificate, X509Certificate2 issuer, IReadOnlyList<Uri> extras, Gate gate, bool allowLocal, List<byte[]> ocsp, List<byte[]> crls, List<string> problems, CancellationToken ct)
    {
        var urls = RevocationSources.From(certificate);
        byte[] request = OcspMessages.BuildRequest(certificate, issuer);
        foreach (var url in urls.Ocsp.Concat(extras))
        {
            if (!gate.Allows(url)) continue;
            var verified = await TryAsync(url, () => _transport.PostOcspAsync(url, request, allowLocal, ct), d => OcspMessages.Verify(d, certificate, issuer), ocsp, problems, ct).ConfigureAwait(false);
            if (verified is { Status: SignatureRevocationStatus.Good or SignatureRevocationStatus.Revoked }) return verified.Status;
        }
        foreach (var url in urls.Crl.Concat(extras))
        {
            if (!gate.Allows(url)) continue;
            var verified = await TryAsync(url, () => _transport.GetAsync(url, allowLocal, ct), d => CrlMessages.Verify(d, certificate, issuer), crls, problems, ct).ConfigureAwait(false);
            if (verified is not null) return verified.Status;
        }
        return SignatureRevocationStatus.Unknown;
    }

    private static async Task<VerifiedRevocation?> TryAsync(Uri url, Func<Task<byte[]>> fetch, Func<byte[], VerifiedRevocation?> verify, List<byte[]> accepted, List<string> problems, CancellationToken ct)
    {
        try
        {
            var data = await fetch().ConfigureAwait(false);
            if (data.Length > RevocationLimits.MaxResponseBytes)
            {
                problems.Add(url.AbsoluteUri + ": response too large");
                return null;
            }
            var verified = verify(data);
            if (verified is null)
            {
                problems.Add(url.AbsoluteUri + ": response not verified");
                return null;
            }
            accepted.Add(data);
            return verified;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add(url.AbsoluteUri + ": " + ex.Message);
            return null;
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.Pdf;

/// <summary>
/// Odczyt struktury pliku PDF na potrzeby zapisu przyrostowego i weryfikacji podpisów: tablice/strumienie xref
/// (z /Prev i /XRefStm), strumienie obiektów, odszyfrowanie (handler Standard). Obiekty zwracane są
/// odszyfrowane; /Contents słowników podpisu nie są szyfrowane (zgodnie z praktyką czytników).
/// </summary>
internal sealed class CosDocument
{
    private readonly record struct XrefEntry(int Type, long Offset, int Generation, int StreamNumber, int StreamIndex);

    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, CosObject> _cache = new();
    private readonly Dictionary<int, (byte[] Data, int First, List<(int Num, int Offset)> Index)> _objStreams = new();
    private int _encryptObjectNumber = -1;

    private CosDocument(byte[] data) => Data = data;

    public byte[] Data { get; }

    /// <summary>Najnowszy trailer (dla strumienia xref – słownik strumienia).</summary>
    public CosDictionary Trailer { get; private set; } = new();

    /// <summary>Pozycja ostatniej sekcji xref (wartość startxref) – /Prev w zapisie przyrostowym.</summary>
    public long StartXref { get; private set; }

    /// <summary>Czy ostatnia sekcja to strumień xref (wtedy zapis przyrostowy też używa strumienia).</summary>
    public bool UsesXrefStream { get; private set; }

    /// <summary>Czy tablicę odtworzono skanowaniem (plik z uszkodzonym xref).</summary>
    public bool WasRebuilt { get; private set; }

    public int Size { get; private set; }

    public StandardSecurityHandler? Security { get; private set; }

    public bool IsEncrypted => Trailer["Encrypt"] is not null;

    /// <summary>
    /// Otwiera plik. Dla zaszyfrowanego wymaga poprawnego hasła (puste hasło użytkownika – bez hasła).
    /// Wyjątki: <see cref="PdfInvalidPasswordException"/>, <see cref="PdfPasswordRequiredException"/>, <see cref="PdfCorruptException"/>.
    /// </summary>
    public static CosDocument Open(byte[] data, string? password)
    {
        var doc = new CosDocument(data);
        try
        {
            doc.LoadXref();
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            doc._xref.Clear();
            doc.Rebuild();
        }
        if (doc.Trailer["Root"] is null) throw new PdfCorruptException("Missing document catalog (/Root).");
        doc.InitSecurity(password);
        return doc;
    }

    // ------------------------------------------------------------------ xref

    private void LoadXref()
    {
        StartXref = FindStartXref();
        var visited = new HashSet<long>();
        long offset = StartXref;
        bool first = true;
        while (offset >= 0 && visited.Add(offset))
        {
            var trailer = ReadXrefSection(offset, out bool isStream);
            if (first)
            {
                Trailer = trailer;
                UsesXrefStream = isStream;
                first = false;
            }
            if (!isStream && trailer["XRefStm"] is CosNumber xs && visited.Add(xs.AsLong))
                ReadXrefSection(xs.AsLong, out _);
            offset = trailer["Prev"] is CosNumber prev ? prev.AsLong : -1;
        }
        Size = (Trailer["Size"] as CosNumber)?.AsInt ?? 0;
        if (_xref.Count == 0) throw new FormatException("Empty cross-reference table.");
        if (_xref.Count > 0) Size = Math.Max(Size, _xref.Keys.Max() + 1);
    }

    private long FindStartXref()
    {
        int from = Math.Max(0, Data.Length - 2048);
        var tail = Encoding.Latin1.GetString(Data, from, Data.Length - from);
        int idx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (idx < 0) throw new FormatException("startxref not found.");
        var p = new CosParser(Data, from + idx + "startxref".Length);
        long off = p.ReadInteger();
        if (off < 0 || off >= Data.Length) throw new FormatException("Invalid startxref.");
        return off;
    }

    private CosDictionary ReadXrefSection(long offset, out bool isStream)
    {
        var p = new CosParser(Data, (int)offset);
        if (p.TryReadKeyword("xref"))
        {
            isStream = false;
            while (true)
            {
                if (p.TryReadKeyword("trailer")) break;
                long start = p.ReadInteger();
                long count = p.ReadInteger();
                for (long i = 0; i < count; i++)
                {
                    long off = p.ReadInteger();
                    int gen = (int)p.ReadInteger();
                    bool inUse;
                    if (p.TryReadKeyword("n")) inUse = true;
                    else if (p.TryReadKeyword("f")) inUse = false;
                    else throw new FormatException("Invalid xref entry.");
                    int num = (int)(start + i);
                    if (!_xref.ContainsKey(num))
                        _xref[num] = inUse ? new XrefEntry(1, off, gen, 0, 0) : new XrefEntry(0, 0, gen, 0, 0);
                }
            }
            return p.ReadObject() as CosDictionary ?? throw new FormatException("Trailer dictionary expected.");
        }

        isStream = true;
        p.ReadObjectHeader();
        var obj = ReadObjectBody(p, -1, -1, decrypt: false);
        if (obj is not CosStream xs || xs.Dictionary.GetName("Type") != "XRef") throw new FormatException("Cross-reference stream expected.");
        var data = CosFilters.Decode(xs, o => o);
        var w = (xs.Dictionary["W"] as CosArray)?.Items.Select(i => ((CosNumber)i).AsInt).ToArray() ?? throw new FormatException("/W missing.");
        int size = (xs.Dictionary["Size"] as CosNumber)?.AsInt ?? 0;
        var index = (xs.Dictionary["Index"] as CosArray)?.Items.Select(i => ((CosNumber)i).AsInt).ToArray() ?? new[] { 0, size };
        int rowLen = w.Sum();
        int pos = 0;
        for (int s = 0; s + 1 < index.Length; s += 2)
        {
            for (int k = 0; k < index[s + 1]; k++)
            {
                if (pos + rowLen > data.Length) break;
                long f1 = w[0] == 0 ? 1 : ReadBe(data, pos, w[0]);
                long f2 = ReadBe(data, pos + w[0], w[1]);
                long f3 = ReadBe(data, pos + w[0] + w[1], w[2]);
                pos += rowLen;
                int num = index[s] + k;
                if (_xref.ContainsKey(num)) continue;
                _xref[num] = f1 switch
                {
                    1 => new XrefEntry(1, f2, (int)f3, 0, 0),
                    2 => new XrefEntry(2, 0, 0, (int)f2, (int)f3),
                    _ => new XrefEntry(0, 0, 0, 0, 0),
                };
            }
        }
        return xs.Dictionary;
    }

    private static long ReadBe(byte[] d, int pos, int len)
    {
        long v = 0;
        for (int i = 0; i < len; i++) v = (v << 8) | d[pos + i];
        return v;
    }

    /// <summary>Odtworzenie tablicy obiektów przez skanowanie (plik z uszkodzonym xref).</summary>
    private void Rebuild()
    {
        WasRebuilt = true;
        var text = Encoding.Latin1.GetString(Data);
        foreach (Match m in Regex.Matches(text, @"(?<![0-9])(\d{1,10})\s+(\d{1,5})\s+obj\b"))
        {
            int num = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            int gen = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            _xref[num] = new XrefEntry(1, m.Index, gen, 0, 0); // późniejsze wystąpienie wygrywa
        }
        int t = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (t >= 0)
        {
            var p = new CosParser(Data, t + 7);
            Trailer = p.ReadObject() as CosDictionary ?? new CosDictionary();
        }
        if (Trailer["Root"] is null)
        {
            // szukamy katalogu wśród obiektów
            foreach (var num in _xref.Keys.OrderByDescending(k => k))
            {
                if (TryGetObjectSafe(num) is CosDictionary d && d.GetName("Type") == "Catalog")
                {
                    Trailer["Root"] = new CosReference(num, _xref[num].Generation);
                    break;
                }
            }
        }
        StartXref = text.LastIndexOf("startxref", StringComparison.Ordinal) is int sx and >= 0
            ? (long)new CosParser(Data, sx + 9).ReadInteger() : 0;
        Size = _xref.Count == 0 ? 0 : _xref.Keys.Max() + 1;
        _cache.Clear();
    }

    private CosObject? TryGetObjectSafe(int num)
    {
        try { return GetObject(num); } catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    // ------------------------------------------------------------------ zabezpieczenia

    private void InitSecurity(string? password)
    {
        var enc = Trailer["Encrypt"];
        if (enc is null) return;
        if (enc is CosReference r) _encryptObjectNumber = r.Number;
        var dict = Resolve(enc) as CosDictionary ?? throw new PdfCorruptException("Invalid /Encrypt dictionary.");
        var id0 = (Trailer["ID"] as CosArray)?.Items.FirstOrDefault() is CosString s ? s.Bytes : Array.Empty<byte>();
        Security = StandardSecurityHandler.Create(dict, id0);
        if (!Security.Authenticate(password))
        {
            if (string.IsNullOrEmpty(password)) throw new PdfPasswordRequiredException();
            throw new PdfInvalidPasswordException();
        }
        _cache.Clear(); // obiekty wczytane przed uwierzytelnieniem (np. /Encrypt) – bez odszyfrowania
    }

    public int GetGeneration(int number) => _xref.TryGetValue(number, out var e) && e.Type == 1 ? e.Generation : 0;

    public bool Exists(int number) => _xref.TryGetValue(number, out var e) && e.Type != 0;

    // ------------------------------------------------------------------ obiekty

    public CosObject? Resolve(CosObject? o)
    {
        int guard = 0;
        while (o is CosReference r && guard++ < 32) o = GetObject(r.Number);
        return o;
    }

    public CosDictionary? ResolveDict(CosObject? o) => Resolve(o) switch
    {
        CosDictionary d => d,
        CosStream s => s.Dictionary,
        _ => null,
    };

    /// <summary>Obiekt pośredni (odszyfrowany). Brak – <see cref="CosNull"/>.</summary>
    public CosObject GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached)) return cached;
        if (!_xref.TryGetValue(number, out var e) || e.Type == 0) return CosNull.Instance;
        CosObject result;
        if (e.Type == 1)
        {
            var p = new CosParser(Data, (int)e.Offset);
            var (num, gen) = p.ReadObjectHeader();
            if (num != number) throw new FormatException($"Object {number} expected at offset {e.Offset}, found {num}.");
            bool decrypt = Security?.FileKey is not null && number != _encryptObjectNumber;
            result = ReadObjectBody(p, num, gen, decrypt);
        }
        else
        {
            result = ReadFromObjectStream(e.StreamNumber, number);
        }
        _cache[number] = result;
        return result;
    }

    private CosObject ReadObjectBody(CosParser p, int num, int gen, bool decrypt)
    {
        var obj = p.ReadObject();
        if (obj is CosDictionary dict && p.TryReadKeyword("stream"))
        {
            int start = p.Position;
            if (start < Data.Length && Data[start] == 13) start++;
            if (start < Data.Length && Data[start] == 10) start++;
            long length = -1;
            var lenObj = dict["Length"];
            if (lenObj is CosNumber ln) length = ln.AsLong;
            else if (lenObj is CosReference lr && lr.Number != num)
            {
                try { length = (Resolve(lr) as CosNumber)?.AsLong ?? -1; } catch (FormatException) { length = -1; }
            }
            if (length < 0 || start + length > Data.Length || !EndstreamFollows(start + (int)length))
                length = FindEndstream(start) - start;
            var data = Data.AsSpan(start, (int)length).ToArray();
            if (decrypt && dict.GetName("Type") != "XRef") data = Security!.DecryptStream(data, num, gen);
            if (decrypt) DecryptStrings(dict, num, gen, isSignature: false);
            return new CosStream(dict, data);
        }
        if (decrypt) obj = DecryptStrings(obj, num, gen, isSignature: obj is CosDictionary d && IsSignatureDictionary(d));
        return obj;
    }

    private static bool IsSignatureDictionary(CosDictionary d) =>
        d.GetName("Type") is "Sig" or "DocTimeStamp" || (d.ContainsKey("ByteRange") && d.ContainsKey("Contents") && d.ContainsKey("Filter"));

    private CosObject DecryptStrings(CosObject o, int num, int gen, bool isSignature)
    {
        switch (o)
        {
            case CosString s:
                return new CosString(Security!.DecryptString(s.Bytes, num, gen), s.Hex);
            case CosArray a:
                for (int i = 0; i < a.Items.Count; i++) a.Items[i] = DecryptStrings(a.Items[i], num, gen, false);
                return a;
            case CosDictionary d:
                bool sig = isSignature || IsSignatureDictionary(d);
                foreach (var key in d.Keys.ToList())
                {
                    if (sig && key == "Contents") continue;
                    d[key] = DecryptStrings(d[key]!, num, gen, false);
                }
                return d;
            default:
                return o;
        }
    }

    private bool EndstreamFollows(int pos)
    {
        var p = new CosParser(Data, pos);
        return p.TryReadKeyword("endstream");
    }

    private int FindEndstream(int start)
    {
        var marker = "endstream"u8;
        int idx = Data.AsSpan(start).IndexOf(marker);
        if (idx < 0) throw new FormatException("endstream not found.");
        int end = start + idx;
        if (end > start && Data[end - 1] == 10) end--;
        if (end > start && Data[end - 1] == 13) end--;
        return end;
    }

    private CosObject ReadFromObjectStream(int streamNumber, int number)
    {
        if (!_objStreams.TryGetValue(streamNumber, out var os))
        {
            var s = GetObject(streamNumber) as CosStream ?? throw new FormatException($"Object stream {streamNumber} missing.");
            var data = CosFilters.Decode(s, Resolve);
            int n = (Resolve(s.Dictionary["N"]) as CosNumber)?.AsInt ?? 0;
            int first = (Resolve(s.Dictionary["First"]) as CosNumber)?.AsInt ?? 0;
            var p = new CosParser(data);
            var index = new List<(int, int)>(n);
            for (int i = 0; i < n; i++) index.Add(((int)p.ReadInteger(), (int)p.ReadInteger()));
            os = (data, first, index);
            _objStreams[streamNumber] = os;
        }
        foreach (var (num, off) in os.Index)
        {
            if (num != number) continue;
            var p = new CosParser(os.Data, os.First + off);
            return p.ReadObject();
        }
        return CosNull.Instance;
    }

    // ------------------------------------------------------------------ nawigacja po dokumencie

    public CosDictionary Catalog => ResolveDict(Trailer["Root"]) ?? throw new PdfCorruptException("Missing catalog.");

    public CosReference CatalogReference => Trailer["Root"] as CosReference ?? throw new PdfCorruptException("Catalog is not an indirect object.");

    /// <summary>Strony w kolejności (referencje do obiektów /Page) – drzewo stron przechodzone od korzenia.</summary>
    public IReadOnlyList<CosReference> GetPageReferences()
    {
        var result = new List<CosReference>();
        var visited = new HashSet<int>();
        void Walk(CosObject? node)
        {
            if (node is not CosReference r || !visited.Add(r.Number)) return;
            var d = ResolveDict(r);
            if (d is null) return;
            var type = d.GetName("Type");
            if (type == "Pages" || (type is null && d.ContainsKey("Kids")))
            {
                if (Resolve(d["Kids"]) is CosArray kids)
                    foreach (var k in kids.Items) Walk(k);
            }
            else result.Add(r);
        }
        Walk(Catalog["Pages"]);
        return result;
    }

    /// <summary>Wartość dziedziczona w drzewie stron (np. /Rotate, /MediaBox).</summary>
    public CosObject? GetInheritedPageAttribute(CosDictionary page, string key)
    {
        var d = page;
        for (int i = 0; i < 64 && d is not null; i++)
        {
            var v = d[key];
            if (v is not null) return Resolve(v);
            d = ResolveDict(d["Parent"]);
        }
        return null;
    }

    /// <summary>Pola formularza (liście z pełną nazwą), z dziedziczonym /FT.</summary>
    public IReadOnlyList<FieldInfo> GetFields()
    {
        var result = new List<FieldInfo>();
        var acro = ResolveDict(Catalog["AcroForm"]);
        if (acro is null) return result;
        var visited = new HashSet<int>();
        void Walk(CosObject? item, string parentName, string? parentFt)
        {
            if (item is CosReference r && !visited.Add(r.Number)) return;
            var d = ResolveDict(item);
            if (d is null) return;
            var partial = (Resolve(d["T"]) as CosString)?.Text;
            var name = partial is null ? parentName : parentName.Length == 0 ? partial : parentName + "." + partial;
            var ft = d.GetName("FT") ?? parentFt;
            var kids = Resolve(d["Kids"]) as CosArray;
            bool hasFieldKids = kids is not null && kids.Items.Any(k => ResolveDict(k) is { } kd && kd.ContainsKey("T"));
            if (hasFieldKids)
            {
                foreach (var k in kids!.Items) Walk(k, name, ft);
                return;
            }
            var widgets = new List<CosReference>();
            if (d.GetName("Subtype") == "Widget" && item is CosReference self) widgets.Add(self);
            else if (kids is not null) widgets.AddRange(kids.Items.OfType<CosReference>());
            result.Add(new FieldInfo(name, ft, item as CosReference, d, widgets));
        }
        if (Resolve(acro["Fields"]) is CosArray fields)
            foreach (var f in fields.Items) Walk(f, string.Empty, null);
        return result;
    }

    /// <summary>Indeks strony, na której jest widżet (/P lub przeszukanie /Annots).</summary>
    public int FindWidgetPage(CosReference widget, IReadOnlyList<CosReference> pages)
    {
        var w = ResolveDict(widget);
        if (w?["P"] is CosReference pr)
        {
            for (int i = 0; i < pages.Count; i++) if (pages[i].Number == pr.Number) return i;
        }
        for (int i = 0; i < pages.Count; i++)
        {
            var pd = ResolveDict(pages[i]);
            if (Resolve(pd?["Annots"]) is CosArray annots && annots.Items.OfType<CosReference>().Any(a => a.Number == widget.Number)) return i;
        }
        return -1;
    }

    public static PdfRect ToRect(CosObject? o)
    {
        if (o is not CosArray a || a.Count < 4) return default;
        double[] v = a.Items.Take(4).Select(i => (i as CosNumber)?.AsDouble ?? 0).ToArray();
        return new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }
}

/// <summary>Pole formularza odczytane z drzewa AcroForm.</summary>
internal sealed record FieldInfo(string FullName, string? FieldType, CosReference? Reference, CosDictionary Dictionary, IReadOnlyList<CosReference> Widgets);

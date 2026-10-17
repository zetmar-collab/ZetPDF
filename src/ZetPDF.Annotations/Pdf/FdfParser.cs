using System.Globalization;
using System.Text;

namespace ZetPDF.Annotations.Pdf;

/// <summary>Wartość obiektu FDF/PDF w małym parserze (FDF nie da się otworzyć przez PDFsharp – brak drzewa stron).</summary>
internal abstract record FdfValue;
internal sealed record FdfNull : FdfValue { public static readonly FdfNull Instance = new(); }
internal sealed record FdfBool(bool Value) : FdfValue;
internal sealed record FdfNumber(double Value) : FdfValue;
internal sealed record FdfName(string Value) : FdfValue;
internal sealed record FdfString(byte[] Bytes, bool Hex) : FdfValue
{
    /// <summary>Tekst: UTF-16BE z BOM, UTF-8 z BOM albo PDFDocEncoding (≈ Latin-1).</summary>
    public string Text
    {
        get
        {
            var b = Bytes;
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            return Encoding.Latin1.GetString(b);
        }
    }
}
internal sealed record FdfRef(int Number, int Generation) : FdfValue;
internal sealed record FdfArray : FdfValue
{
    public List<FdfValue> Items { get; } = new();
}
internal sealed record FdfDict : FdfValue
{
    public Dictionary<string, FdfValue> Items { get; } = new(StringComparer.Ordinal);
    public byte[]? Stream { get; set; }
    public FdfValue? this[string key] => Items.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Sparsowany plik FDF: obiekty pośrednie i trailer.</summary>
internal sealed class FdfDocument
{
    public Dictionary<int, FdfValue> Objects { get; } = new();
    public FdfDict Trailer { get; set; } = new();

    public FdfValue? Resolve(FdfValue? v, int depth = 0)
    {
        while (v is FdfRef r && depth++ < 64) v = Objects.TryGetValue(r.Number, out var o) ? o : null;
        return v;
    }

    public FdfDict? ResolveDict(FdfValue? v) => Resolve(v) as FdfDict;
    public FdfArray? ResolveArray(FdfValue? v) => Resolve(v) as FdfArray;

    public string Text(FdfValue? v) => Resolve(v) switch
    {
        FdfString s => s.Text,
        FdfName n => n.Value,
        FdfNumber num => num.Value.ToString(CultureInfo.InvariantCulture),
        FdfDict { Stream: { } bytes } => new FdfString(bytes, false).Text,
        _ => string.Empty,
    };

    /// <summary>Słownik /FDF z katalogu (trailer /Root → /FDF).</summary>
    public FdfDict? FdfRoot => ResolveDict(ResolveDict(Trailer["/Root"])?["/FDF"]);
}

/// <summary>Minimalny parser składni obiektów PDF dla plików FDF (słowniki, tablice, ciągi, strumienie z /Length).</summary>
internal sealed class FdfParser
{
    private readonly byte[] _b;
    private int _p;

    private FdfParser(byte[] data) => _b = data;

    public static bool LooksLikeFdf(byte[] data)
    {
        int i = 0;
        while (i < data.Length && i < 1024 && data[i] != '%') i++;
        return i + 4 < data.Length && data[i + 1] == 'F' && data[i + 2] == 'D' && data[i + 3] == 'F';
    }

    public static FdfDocument Parse(byte[] data)
    {
        var parser = new FdfParser(data);
        var doc = new FdfDocument();
        parser.ParseAll(doc);
        return doc;
    }

    private void ParseAll(FdfDocument doc)
    {
        while (true)
        {
            SkipWs();
            if (_p >= _b.Length) break;
            int save = _p;
            var tok = PeekToken();
            if (tok == "trailer")
            {
                ReadToken();
                if (ParseValue() is FdfDict t) doc.Trailer = t;
                continue;
            }
            if (tok.Length > 0 && char.IsDigit(tok[0]))
            {
                // "n g obj"
                var a = ReadToken();
                SkipWs();
                var g = ReadToken();
                SkipWs();
                var o = ReadToken();
                if (o == "obj" && int.TryParse(a, out int num) && int.TryParse(g, out _))
                {
                    var v = ParseValue();
                    SkipWs();
                    if (PeekToken() == "stream" && v is FdfDict sd)
                    {
                        ReadToken();
                        if (_p < _b.Length && _b[_p] == '\r') _p++;
                        if (_p < _b.Length && _b[_p] == '\n') _p++;
                        int len = sd["/Length"] is FdfNumber ln ? (int)ln.Value : -1;
                        int start = _p;
                        if (len < 0 || start + len > _b.Length)
                        {
                            int end = IndexOf("endstream", start);
                            len = Math.Max(0, (end < 0 ? _b.Length : end) - start);
                            while (len > 0 && (_b[start + len - 1] == '\n' || _b[start + len - 1] == '\r')) len--;
                        }
                        sd.Stream = _b.AsSpan(start, len).ToArray();
                        _p = start + len;
                        int es = IndexOf("endstream", _p);
                        _p = es < 0 ? _b.Length : es + 9;
                    }
                    doc.Objects[num] = v;
                    SkipWs();
                    if (PeekToken() == "endobj") ReadToken();
                    continue;
                }
                _p = save + 1;
                continue;
            }
            // inne słowa (xref, startxref, %%EOF) – pomijamy
            ReadToken();
            if (_p == save) _p++;
        }
    }

    private int IndexOf(string s, int from)
    {
        var pat = Encoding.ASCII.GetBytes(s);
        int idx = _b.AsSpan(Math.Min(from, _b.Length)).IndexOf(pat);
        return idx < 0 ? -1 : from + idx;
    }

    private static bool IsWs(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;
    private static bool IsDelim(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private void SkipWs()
    {
        while (_p < _b.Length)
        {
            if (IsWs(_b[_p])) { _p++; continue; }
            if (_b[_p] == '%')
            {
                while (_p < _b.Length && _b[_p] != '\n' && _b[_p] != '\r') _p++;
                continue;
            }
            break;
        }
    }

    private string PeekToken()
    {
        int save = _p;
        var t = ReadToken();
        _p = save;
        return t;
    }

    private string ReadToken()
    {
        SkipWs();
        int start = _p;
        while (_p < _b.Length && !IsWs(_b[_p]) && !IsDelim(_b[_p])) _p++;
        return Encoding.ASCII.GetString(_b, start, _p - start);
    }

    private FdfValue ParseValue()
    {
        SkipWs();
        if (_p >= _b.Length) return FdfNull.Instance;
        byte c = _b[_p];
        if (c == '<' && _p + 1 < _b.Length && _b[_p + 1] == '<') return ParseDict();
        if (c == '<') return ParseHex();
        if (c == '(') return ParseLiteral();
        if (c == '[') return ParseArray();
        if (c == '/') return ParseName();
        var tok = ReadToken();
        if (tok.Length == 0) { _p++; return FdfNull.Instance; }
        switch (tok)
        {
            case "true": return new FdfBool(true);
            case "false": return new FdfBool(false);
            case "null": return FdfNull.Instance;
        }
        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
        {
            // "n g R"?
            int save = _p;
            SkipWs();
            var g = ReadToken();
            SkipWs();
            if (int.TryParse(g, out int gen) && _p < _b.Length && _b[_p] == 'R' && (_p + 1 >= _b.Length || IsWs(_b[_p + 1]) || IsDelim(_b[_p + 1])))
            {
                _p++;
                return new FdfRef((int)num, gen);
            }
            _p = save;
            return new FdfNumber(num);
        }
        return new FdfName(tok);
    }

    private FdfDict ParseDict()
    {
        _p += 2;
        var d = new FdfDict();
        while (true)
        {
            SkipWs();
            if (_p >= _b.Length) break;
            if (_b[_p] == '>' && _p + 1 < _b.Length && _b[_p + 1] == '>') { _p += 2; break; }
            if (_b[_p] != '/') { _p++; continue; }
            var key = ((FdfName)ParseName()).Value;
            d.Items[key] = ParseValue();
        }
        return d;
    }

    private FdfArray ParseArray()
    {
        _p++;
        var a = new FdfArray();
        while (true)
        {
            SkipWs();
            if (_p >= _b.Length) break;
            if (_b[_p] == ']') { _p++; break; }
            a.Items.Add(ParseValue());
        }
        return a;
    }

    private FdfValue ParseName()
    {
        _p++;
        var sb = new StringBuilder("/");
        var raw = new List<byte>();
        while (_p < _b.Length && !IsWs(_b[_p]) && !IsDelim(_b[_p]))
        {
            if (_b[_p] == '#' && _p + 2 < _b.Length && byte.TryParse(Encoding.ASCII.GetString(_b, _p + 1, 2), NumberStyles.HexNumber, null, out byte hb))
            {
                raw.Add(hb);
                _p += 3;
            }
            else raw.Add(_b[_p++]);
        }
        sb.Append(Encoding.UTF8.GetString(raw.ToArray()));
        return new FdfName(sb.ToString());
    }

    private FdfString ParseHex()
    {
        _p++;
        var hex = new StringBuilder();
        while (_p < _b.Length && _b[_p] != '>')
        {
            char ch = (char)_b[_p++];
            if (Uri.IsHexDigit(ch)) hex.Append(ch);
        }
        _p++;
        if (hex.Length % 2 == 1) hex.Append('0');
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(hex.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new FdfString(bytes, true);
    }

    private FdfString ParseLiteral()
    {
        _p++;
        var bytes = new List<byte>();
        int depth = 1;
        while (_p < _b.Length)
        {
            byte c = _b[_p++];
            if (c == '\\' && _p < _b.Length)
            {
                byte e = _b[_p++];
                switch (e)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r': if (_p < _b.Length && _b[_p] == '\n') _p++; break;
                    case (byte)'\n': break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && _p < _b.Length && _b[_p] >= '0' && _b[_p] <= '7'; k++) v = v * 8 + (_b[_p++] - '0');
                            bytes.Add((byte)v);
                        }
                        else bytes.Add(e);
                        break;
                }
                continue;
            }
            if (c == '(') depth++;
            if (c == ')' && --depth == 0) break;
            bytes.Add(c);
        }
        return new FdfString(bytes.ToArray(), false);
    }
}

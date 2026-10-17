using System.Globalization;
using System.Text;

namespace ZetPDF.Security.Pdf;

// Minimalny model obiektów PDF (COS) używany przez zapis przyrostowy podpisów i weryfikację.
// PDFsharp w trybie Import spłaszcza drzewo stron (zmienia /Parent – sprawdzone), więc do zapisu
// przyrostowego potrzebny jest własny, wierny odczyt obiektów. Napisy i strumienie są trzymane
// w postaci ODSZYFROWANEJ; szyfrowanie stosuje zapis (CosWriter) wg numeru obiektu.

internal abstract class CosObject
{
    public virtual CosObject Clone() => this;
}

internal sealed class CosName : CosObject
{
    public CosName(string value) => Value = value;
    public string Value { get; }
    public override string ToString() => "/" + Value;
    public override bool Equals(object? obj) => obj is CosName n && n.Value == Value;
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
}

internal sealed class CosNumber : CosObject
{
    public CosNumber(string raw) => Raw = raw;
    public CosNumber(long value) => Raw = value.ToString(CultureInfo.InvariantCulture);
    public CosNumber(double value) => Raw = FormatReal(value);
    public string Raw { get; }
    public bool IsInteger => !Raw.Contains('.');
    public long AsLong => IsInteger && long.TryParse(Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l : (long)Math.Round(AsDouble);
    public int AsInt => (int)AsLong;
    public double AsDouble => double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    public override string ToString() => Raw;

    public static string FormatReal(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

internal sealed class CosString : CosObject
{
    public CosString(byte[] bytes, bool hex = false) { Bytes = bytes; Hex = hex; }
    public byte[] Bytes { get; }
    public bool Hex { get; }

    /// <summary>Tekst: UTF-16BE z BOM, UTF-8 z BOM (PDF 2.0) lub PDFDocEncoding (przybliżenie Latin-1).</summary>
    public string Text
    {
        get
        {
            var b = Bytes;
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            return PdfDocEncoding.Decode(b);
        }
    }

    /// <summary>Napis tekstowy: ASCII bez zmian, inaczej UTF-16BE z BOM (polskie znaki).</summary>
    public static CosString FromText(string text)
    {
        if (text.All(c => c < 128)) return new CosString(Encoding.ASCII.GetBytes(text));
        var body = Encoding.BigEndianUnicode.GetBytes(text);
        var b = new byte[body.Length + 2];
        b[0] = 0xFE; b[1] = 0xFF;
        body.CopyTo(b, 2);
        return new CosString(b);
    }

    public override string ToString() => Text;
}

internal sealed class CosBoolean : CosObject
{
    public static readonly CosBoolean True = new(true), False = new(false);
    private CosBoolean(bool v) => Value = v;
    public bool Value { get; }
    public override string ToString() => Value ? "true" : "false";
}

internal sealed class CosNull : CosObject
{
    public static readonly CosNull Instance = new();
    private CosNull() { }
    public override string ToString() => "null";
}

internal sealed class CosReference : CosObject
{
    public CosReference(int number, int generation) { Number = number; Generation = generation; }
    public int Number { get; }
    public int Generation { get; }
    public override string ToString() => $"{Number} {Generation} R";
    public override bool Equals(object? obj) => obj is CosReference r && r.Number == Number && r.Generation == Generation;
    public override int GetHashCode() => HashCode.Combine(Number, Generation);
}

/// <summary>Słowo kluczowe napotkane przez parser (obj, endobj, stream, R…) – tylko wewnętrznie.</summary>
internal sealed class CosKeyword : CosObject
{
    public CosKeyword(string value) => Value = value;
    public string Value { get; }
    public override string ToString() => Value;
}

internal sealed class CosArray : CosObject
{
    public CosArray() { }
    public CosArray(IEnumerable<CosObject> items) => Items.AddRange(items);
    public List<CosObject> Items { get; } = new();
    public int Count => Items.Count;
    public CosObject this[int i] => Items[i];
    public void Add(CosObject o) => Items.Add(o);
    public override CosObject Clone() => new CosArray(Items.Select(i => i.Clone()));

    public static CosArray OfNumbers(params double[] values) => new(values.Select(v => (CosObject)new CosNumber(v)));
}

internal sealed class CosDictionary : CosObject
{
    private readonly List<KeyValuePair<string, CosObject>> _entries = new();

    public IReadOnlyList<KeyValuePair<string, CosObject>> Entries => _entries;
    public IEnumerable<string> Keys => _entries.Select(e => e.Key);

    /// <summary>Wartość klucza (bez ukośnika) lub null.</summary>
    public CosObject? this[string key]
    {
        get
        {
            foreach (var e in _entries) if (e.Key == key) return e.Value;
            return null;
        }
        set
        {
            int i = _entries.FindIndex(e => e.Key == key);
            if (value is null)
            {
                if (i >= 0) _entries.RemoveAt(i);
                return;
            }
            if (i >= 0) _entries[i] = new(key, value);
            else _entries.Add(new(key, value));
        }
    }

    public bool ContainsKey(string key) => _entries.Exists(e => e.Key == key);

    public string? GetName(string key) => (this[key] as CosName)?.Value;

    public override CosObject Clone()
    {
        var d = new CosDictionary();
        foreach (var e in _entries) d._entries.Add(new(e.Key, e.Value.Clone()));
        return d;
    }
}

/// <summary>Strumień: słownik + dane po filtrach (np. Flate), ale PO odszyfrowaniu.</summary>
internal sealed class CosStream : CosObject
{
    public CosStream(CosDictionary dictionary, byte[] data) { Dictionary = dictionary; Data = data; }
    public CosDictionary Dictionary { get; }
    public byte[] Data { get; set; }
    public override CosObject Clone() => new CosStream((CosDictionary)Dictionary.Clone(), (byte[])Data.Clone());
}

/// <summary>Miejsce rezerwowane w zapisie (np. /Contents i /ByteRange podpisu) – wypełniane po zapisie.</summary>
internal sealed class CosPlaceholder : CosObject
{
    public CosPlaceholder(string id, int length, byte fill) { Id = id; Length = length; Fill = fill; }
    public string Id { get; }
    public int Length { get; }
    public byte Fill { get; }
}

/// <summary>PDFDocEncoding (tabela dla zakresu 0x80–0xA0; reszta jak Latin-1).</summary>
internal static class PdfDocEncoding
{
    private static readonly char[] High =
    {
        '•', '†', '‡', '…', '—', '–', 'ƒ', '⁄', '‹', '›', '−', '‰', '„', '“', '”', '‘',
        '’', '‚', '™', 'ﬁ', 'ﬂ', 'Ł', 'Œ', 'Š', 'Ÿ', 'Ž', 'ı', 'ł', 'œ', 'š', 'ž', '�',
        '€',
    };

    public static string Decode(byte[] b)
    {
        var sb = new StringBuilder(b.Length);
        foreach (var x in b) sb.Append(x >= 0x80 && x <= 0xA0 ? High[x - 0x80] : (char)x);
        return sb.ToString();
    }
}

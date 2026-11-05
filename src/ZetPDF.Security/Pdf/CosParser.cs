using System.Globalization;
using System.Text;

namespace ZetPDF.Security.Pdf;

/// <summary>Parser składni PDF (obiekty bezpośrednie i pośrednie) na tablicy bajtów.</summary>
internal sealed class CosParser
{
    private readonly byte[] _data;
    private readonly int _end;

    public CosParser(byte[] data, int position = 0, int end = -1)
    {
        _data = data;
        Position = position;
        _end = end < 0 ? data.Length : end;
    }

    public int Position { get; set; }
    public bool AtEnd { get { SkipWhitespace(); return Position >= _end; } }

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public void SkipWhitespace()
    {
        while (Position < _end)
        {
            byte b = _data[Position];
            if (IsWhitespace(b)) { Position++; continue; }
            if (b == '%')
            {
                while (Position < _end && _data[Position] != 10 && _data[Position] != 13) Position++;
                continue;
            }
            break;
        }
    }

    /// <summary>Czyta obiekt; liczby „n g R” łączy w referencję.</summary>
    public CosObject ReadObject()
    {
        var o = ReadPrimitive();
        if (o is CosNumber n1 && n1.IsInteger && n1.AsLong >= 0)
        {
            int save = Position;
            // Spekulacyjnie tylko liczba, nigdy tablica lub słownik: inaczej „1 [1 [1 [ …” czytałoby każdy poziom dwa razy.
            var o2 = NextIsDigit() ? TryReadPrimitiveNoThrow() : null;
            if (o2 is CosNumber n2 && n2.IsInteger && n2.AsLong >= 0)
            {
                SkipWhitespace();
                var o3 = Position < _end && _data[Position] == 'R' ? TryReadPrimitiveNoThrow() : null;
                if (o3 is CosKeyword { Value: "R" }) return new CosReference(n1.AsInt, n2.AsInt);
            }
            Position = save;
        }
        return o;
    }

    private bool NextIsDigit()
    {
        SkipWhitespace();
        return Position < _end && _data[Position] >= '0' && _data[Position] <= '9';
    }

    private int _depth;

    /// <summary>Limit zagnieżdżenia: parser jest rekurencyjny, a dane z pliku niezaufane.</summary>
    public const int MaxDepth = 256;

    private T Nested<T>(Func<T> read)
    {
        if (++_depth > MaxDepth) throw new FormatException("Nesting too deep.");
        try { return read(); }
        finally { _depth--; }
    }

    private CosObject? TryReadPrimitiveNoThrow()
    {
        try { return ReadPrimitive(); }
        catch (FormatException) { return null; }
    }

    private CosObject ReadPrimitive()
    {
        SkipWhitespace();
        if (Position >= _end) throw new FormatException("Unexpected end of data.");
        byte b = _data[Position];
        switch (b)
        {
            case (byte)'/': return ReadName();
            case (byte)'(': return ReadLiteralString();
            case (byte)'[': return Nested(ReadArray);
            case (byte)'<':
                if (Position + 1 < _end && _data[Position + 1] == '<') return Nested(ReadDictionaryOrStreamDict);
                return ReadHexString();
            case (byte)']':
            case (byte)'>':
                Position++;
                return new CosKeyword(((char)b).ToString());
        }
        if (b is (byte)'+' or (byte)'-' or (byte)'.' || (b >= '0' && b <= '9')) return ReadNumber();
        // słowo kluczowe
        int start = Position;
        while (Position < _end && !IsWhitespace(_data[Position]) && !IsDelimiter(_data[Position])) Position++;
        if (Position == start) { Position++; throw new FormatException($"Unexpected character '{(char)b}'."); }
        var kw = Encoding.ASCII.GetString(_data, start, Position - start);
        return kw switch
        {
            "true" => CosBoolean.True,
            "false" => CosBoolean.False,
            "null" => CosNull.Instance,
            _ => new CosKeyword(kw),
        };
    }

    private CosNumber ReadNumber()
    {
        int start = Position;
        Position++;
        while (Position < _end)
        {
            byte c = _data[Position];
            if ((c >= '0' && c <= '9') || c == '.') Position++;
            else break;
        }
        var raw = Encoding.ASCII.GetString(_data, start, Position - start);
        if (raw is "-" or "+" or ".") raw = "0";
        if (raw.StartsWith('+')) raw = raw[1..];
        if (raw.Contains("--", StringComparison.Ordinal)) raw = raw.Replace("--", "-", StringComparison.Ordinal);
        return new CosNumber(raw);
    }

    private CosName ReadName()
    {
        Position++; // '/'
        var bytes = new List<byte>();
        while (Position < _end)
        {
            byte c = _data[Position];
            if (IsWhitespace(c) || IsDelimiter(c)) break;
            if (c == '#' && Position + 2 < _end && IsHex(_data[Position + 1]) && IsHex(_data[Position + 2]))
            {
                bytes.Add((byte)(HexVal(_data[Position + 1]) * 16 + HexVal(_data[Position + 2])));
                Position += 3;
                continue;
            }
            bytes.Add(c);
            Position++;
        }
        return new CosName(Encoding.UTF8.GetString(bytes.ToArray()));
    }

    private CosString ReadLiteralString()
    {
        Position++; // '('
        var ms = new List<byte>();
        int depth = 1;
        while (Position < _end)
        {
            byte c = _data[Position++];
            if (c == '\\')
            {
                if (Position >= _end) break;
                byte e = _data[Position++];
                switch (e)
                {
                    case (byte)'n': ms.Add(10); break;
                    case (byte)'r': ms.Add(13); break;
                    case (byte)'t': ms.Add(9); break;
                    case (byte)'b': ms.Add(8); break;
                    case (byte)'f': ms.Add(12); break;
                    case (byte)'(': ms.Add((byte)'('); break;
                    case (byte)')': ms.Add((byte)')'); break;
                    case (byte)'\\': ms.Add((byte)'\\'); break;
                    case 13:
                        if (Position < _end && _data[Position] == 10) Position++;
                        break;
                    case 10: break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && Position < _end && _data[Position] >= '0' && _data[Position] <= '7'; k++)
                                v = v * 8 + (_data[Position++] - '0');
                            ms.Add((byte)v);
                        }
                        else ms.Add(e);
                        break;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) break;
            }
            ms.Add(c);
        }
        return new CosString(ms.ToArray());
    }

    private CosString ReadHexString()
    {
        Position++; // '<'
        var ms = new List<byte>();
        int hi = -1;
        while (Position < _end)
        {
            byte c = _data[Position++];
            if (c == '>') break;
            if (!IsHex(c)) continue;
            if (hi < 0) hi = HexVal(c);
            else { ms.Add((byte)(hi * 16 + HexVal(c))); hi = -1; }
        }
        if (hi >= 0) ms.Add((byte)(hi * 16));
        return new CosString(ms.ToArray(), hex: true);
    }

    private CosArray ReadArray()
    {
        Position++; // '['
        var arr = new CosArray();
        while (true)
        {
            SkipWhitespace();
            if (Position >= _end) throw new FormatException("Unterminated array.");
            if (_data[Position] == ']') { Position++; return arr; }
            var o = ReadObject();
            if (o is CosKeyword { Value: "endobj" or "stream" or "endstream" }) throw new FormatException("Unterminated array.");
            arr.Add(o);
        }
    }

    private CosDictionary ReadDictionaryOrStreamDict()
    {
        Position += 2; // '<<'
        var dict = new CosDictionary();
        while (true)
        {
            SkipWhitespace();
            if (Position >= _end) throw new FormatException("Unterminated dictionary.");
            if (_data[Position] == '>' && Position + 1 < _end && _data[Position + 1] == '>') { Position += 2; return dict; }
            var key = ReadPrimitive();
            if (key is not CosName name)
            {
                if (key is CosKeyword { Value: "endobj" or "stream" }) throw new FormatException("Unterminated dictionary.");
                continue; // śmieci – pomijamy
            }
            SkipWhitespace();
            if (Position < _end && _data[Position] == '>' && Position + 1 < _end && _data[Position + 1] == '>') { dict[name.Value] = CosNull.Instance; continue; }
            var value = ReadObject();
            if (value is CosKeyword kw)
            {
                if (kw.Value is "endobj" or "stream") throw new FormatException("Unterminated dictionary.");
                continue;
            }
            dict[name.Value] = value;
        }
    }

    /// <summary>Czy dalej jest słowo kluczowe (bez zużycia, jeśli nie).</summary>
    public bool TryReadKeyword(string keyword)
    {
        int save = Position;
        SkipWhitespace();
        int start = Position;
        int len = keyword.Length;
        if (start + len <= _end && Encoding.ASCII.GetString(_data, start, len) == keyword &&
            (start + len == _end || IsWhitespace(_data[start + len]) || IsDelimiter(_data[start + len])))
        {
            Position = start + len;
            return true;
        }
        Position = save;
        return false;
    }

    public long ReadInteger()
    {
        var o = ReadPrimitive();
        if (o is CosNumber n && n.IsInteger) return n.AsLong;
        throw new FormatException("Integer expected.");
    }

    /// <summary>Nagłówek obiektu pośredniego „n g obj”.</summary>
    public (int Number, int Generation) ReadObjectHeader()
    {
        int num = (int)ReadInteger();
        int gen = (int)ReadInteger();
        if (!TryReadKeyword("obj")) throw new FormatException("'obj' expected.");
        return (num, gen);
    }

    public static bool IsHex(byte c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    public static int HexVal(byte c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    public static string Latin1(byte[] data, int start, int length) => Encoding.Latin1.GetString(data, start, length);

    public static long ParseLong(string s) => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
}

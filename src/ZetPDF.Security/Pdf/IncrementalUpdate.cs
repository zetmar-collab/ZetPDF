using System.Globalization;
using System.Text;

namespace ZetPDF.Security.Pdf;

/// <summary>
/// Zapis przyrostowy (ISO 32000-1 7.5.6): do oryginalnych bajtów dopisywane są nowe i zmienione obiekty,
/// nowa sekcja xref (tablica lub strumień – jak w oryginale) i trailer z /Prev. Oryginalne bajty nie są
/// zmieniane, więc wcześniejsze podpisy pozostają ważne. W pliku zaszyfrowanym nowe napisy i strumienie
/// są szyfrowane kluczem pliku (wyjątek: /Contents słownika podpisu i miejsca rezerwowane).
/// </summary>
internal sealed class IncrementalUpdate
{
    private readonly CosDocument _source;
    private readonly SortedDictionary<int, (int Generation, CosObject Object)> _objects = new();
    private int _nextNumber;

    public IncrementalUpdate(CosDocument source)
    {
        _source = source;
        _nextNumber = Math.Max(1, source.Size);
    }

    /// <summary>Pozycje miejsc rezerwowanych w wyniku (id → (offset, długość)).</summary>
    public Dictionary<string, (int Offset, int Length)> Placeholders { get; } = new();

    /// <summary>Dodaje nowy obiekt pośredni, zwraca referencję.</summary>
    public CosReference Add(CosObject obj)
    {
        int num = _nextNumber++;
        _objects[num] = (0, obj);
        return new CosReference(num, 0);
    }

    /// <summary>Rezerwuje numer obiektu (treść ustawiana później przez <see cref="Set"/>).</summary>
    public CosReference Reserve()
    {
        int num = _nextNumber++;
        _objects[num] = (0, CosNull.Instance);
        return new CosReference(num, 0);
    }

    /// <summary>Zastępuje (lub ustawia zarezerwowany) obiekt pod tym samym numerem i generacją.</summary>
    public void Set(CosReference reference, CosObject obj) => _objects[reference.Number] = (reference.Generation, obj);

    public byte[] Write()
    {
        var ms = new MemoryStream();
        ms.Write(_source.Data);
        var last = _source.Data.Length > 0 ? _source.Data[^1] : (byte)10;
        if (last != 10 && last != 13) ms.WriteByte(10);

        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        foreach (var (num, (gen, obj)) in _objects)
        {
            offsets[num] = (ms.Position, gen);
            WriteAscii(ms, $"{num} {gen} obj\n");
            WriteObject(ms, obj, num, gen, encrypt: _source.Security is not null, signatureDict: false);
            WriteAscii(ms, "\nendobj\n");
        }

        var trailer = new CosDictionary();
        foreach (var key in new[] { "Root", "Info", "Encrypt", "ID" })
            if (_source.Trailer[key] is { } v) trailer[key] = v;
        trailer["Prev"] = new CosNumber(_source.StartXref);

        long xrefOffset;
        if (_source.UsesXrefStream)
        {
            int xrefNum = _nextNumber++;
            xrefOffset = ms.Position;
            offsets[xrefNum] = (xrefOffset, 0);
            var rows = new MemoryStream();
            var index = new CosArray();
            foreach (var group in Groups(offsets.Keys))
            {
                index.Add(new CosNumber(group.Start));
                index.Add(new CosNumber(group.Count));
                for (int n = group.Start; n < group.Start + group.Count; n++)
                {
                    var (off, gen) = offsets[n];
                    rows.WriteByte(1);
                    rows.WriteByte((byte)(off >> 24)); rows.WriteByte((byte)(off >> 16)); rows.WriteByte((byte)(off >> 8)); rows.WriteByte((byte)off);
                    rows.WriteByte((byte)(gen >> 8)); rows.WriteByte((byte)gen);
                }
            }
            var dict = new CosDictionary
            {
                ["Type"] = new CosName("XRef"),
                ["Size"] = new CosNumber(Math.Max(_nextNumber, _source.Size)),
                ["Index"] = index,
                ["W"] = CosArray.OfNumbers(1, 4, 2),
            };
            foreach (var e in trailer.Entries) dict[e.Key] = e.Value;
            WriteAscii(ms, $"{xrefNum} 0 obj\n");
            WriteObject(ms, new CosStream(dict, rows.ToArray()), xrefNum, 0, encrypt: false, signatureDict: false);
            WriteAscii(ms, "\nendobj\n");
        }
        else
        {
            xrefOffset = ms.Position;
            var sb = new StringBuilder("xref\n");
            foreach (var group in Groups(offsets.Keys))
            {
                sb.Append(CultureInfo.InvariantCulture, $"{group.Start} {group.Count}\n");
                for (int n = group.Start; n < group.Start + group.Count; n++)
                {
                    var (off, gen) = offsets[n];
                    sb.Append(CultureInfo.InvariantCulture, $"{off:D10} {gen:D5} n\r\n");
                }
            }
            WriteAscii(ms, sb.ToString());
            trailer["Size"] = new CosNumber(Math.Max(_nextNumber, _source.Size));
            WriteAscii(ms, "trailer\n");
            WriteObject(ms, trailer, 0, 0, encrypt: false, signatureDict: false);
            WriteAscii(ms, "\n");
        }
        WriteAscii(ms, $"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static IEnumerable<(int Start, int Count)> Groups(IEnumerable<int> sorted)
    {
        int start = -1, count = 0;
        foreach (var n in sorted)
        {
            if (start >= 0 && n == start + count) { count++; continue; }
            if (start >= 0) yield return (start, count);
            start = n;
            count = 1;
        }
        if (start >= 0) yield return (start, count);
    }

    private static void WriteAscii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));

    private void WriteObject(MemoryStream ms, CosObject obj, int num, int gen, bool encrypt, bool signatureDict)
    {
        switch (obj)
        {
            case CosNull: WriteAscii(ms, "null"); break;
            case CosBoolean b: WriteAscii(ms, b.Value ? "true" : "false"); break;
            case CosNumber n: WriteAscii(ms, n.Raw); break;
            case CosName name: WriteAscii(ms, EncodeName(name.Value)); break;
            case CosReference r: WriteAscii(ms, $"{r.Number} {r.Generation} R"); break;
            case CosString s:
                {
                    var bytes = encrypt ? _source.Security!.EncryptString(s.Bytes, num, gen) : s.Bytes;
                    WriteAscii(ms, "<" + Convert.ToHexString(bytes) + ">");
                    break;
                }
            case CosPlaceholder p:
                Placeholders[p.Id] = ((int)ms.Position, p.Length);
                for (int i = 0; i < p.Length; i++) ms.WriteByte(p.Fill);
                break;
            case CosArray a:
                ms.WriteByte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) ms.WriteByte((byte)' ');
                    WriteObject(ms, a[i], num, gen, encrypt, false);
                }
                ms.WriteByte((byte)']');
                break;
            case CosDictionary d:
                {
                    bool sig = signatureDict || d.GetName("Type") is "Sig" or "DocTimeStamp";
                    WriteAscii(ms, "<<");
                    foreach (var e in d.Entries)
                    {
                        WriteAscii(ms, " " + EncodeName(e.Key) + " ");
                        bool plain = sig && e.Key == "Contents";
                        WriteObject(ms, e.Value, num, gen, encrypt && !plain, false);
                    }
                    WriteAscii(ms, " >>");
                    break;
                }
            case CosStream st:
                {
                    var data = encrypt ? _source.Security!.EncryptStream(st.Data, num, gen) : st.Data;
                    var dict = (CosDictionary)st.Dictionary.Clone();
                    dict["Length"] = new CosNumber(data.Length);
                    WriteObject(ms, dict, num, gen, encrypt, false);
                    WriteAscii(ms, "\nstream\n");
                    ms.Write(data);
                    WriteAscii(ms, "\nendstream");
                    break;
                }
            default:
                throw new NotSupportedException(obj.GetType().Name);
        }
    }

    public static string EncodeName(string name)
    {
        var sb = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (b < 0x21 || b > 0x7E || b == '#' || CosParser.IsDelimiter(b)) sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else sb.Append((char)b);
        }
        return sb.ToString();
    }
}

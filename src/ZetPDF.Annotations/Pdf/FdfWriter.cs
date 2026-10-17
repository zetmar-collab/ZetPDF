using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Zapis pliku FDF 1.2. Obiekty PDFsharp serializowane rekurencyjnie (obiekty pośrednie i strumienie jako osobne obiekty),
/// z pominięciem kluczy wskazujących na strony/rodziców (/P, /Parent, /Popup), żeby nie ciągnąć całego dokumentu.
/// </summary>
internal sealed class FdfWriter
{
    private static readonly HashSet<string> SkipKeys = new(StringComparer.Ordinal) { "/P", "/Parent", "/Popup" };

    private readonly List<(int Number, byte[] Body)> _objects = new();
    private readonly Dictionary<PdfObject, int> _numbers = new(ReferenceEqualityComparer.Instance);
    private int _next = 2; // 1 = katalog

    /// <summary>Rezerwuje numer dla obiektu (np. adnotacji), żeby referencje /IRT wskazywały na niego.</summary>
    public int Reserve(PdfObject obj)
    {
        if (!_numbers.TryGetValue(obj, out int n))
        {
            n = _next++;
            _numbers[obj] = n;
        }
        return n;
    }

    /// <summary>Zapisuje obiekt pośredni (słownik ze strumieniem lub bez) i zwraca jego numer.</summary>
    public int WriteObject(PdfObject obj, Action<StringBuilder, PdfDictionary>? extraKeys = null)
    {
        int n = Reserve(obj);
        if (_objects.Any(o => o.Number == n)) return n;
        _objects.Add((n, Array.Empty<byte>())); // znacznik w trakcie (cykle)
        var body = new MemoryStream();
        var sb = new StringBuilder();
        if (obj is PdfDictionary d)
        {
            WriteDict(sb, d, extraKeys);
            Append(body, sb.ToString());
            if (d.Stream is { } s)
            {
                Append(body, "\nstream\n");
                body.Write(s.Value);
                Append(body, "\nendstream");
            }
        }
        else
        {
            WriteItem(sb, obj);
            Append(body, sb.ToString());
        }
        int idx = _objects.FindIndex(o => o.Number == n);
        _objects[idx] = (n, body.ToArray());
        return n;
    }

    private static void Append(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));

    private void WriteDict(StringBuilder sb, PdfDictionary d, Action<StringBuilder, PdfDictionary>? extraKeys)
    {
        sb.Append("<<");
        foreach (var key in d.Elements.Keys)
        {
            if (SkipKeys.Contains(key)) continue;
            if (d.Stream is not null && key == "/Length") continue;
            sb.Append(Name(key)).Append(' ');
            WriteItem(sb, d.Elements[key]);
            sb.Append(' ');
        }
        if (d.Stream is not null) sb.Append("/Length ").Append(d.Stream.Value.Length).Append(' ');
        extraKeys?.Invoke(sb, d);
        sb.Append(">>");
    }

    public void WriteItem(StringBuilder sb, PdfItem? item)
    {
        switch (item)
        {
            case null or PdfNull: sb.Append("null"); break;
            case PdfReference r:
                if (r.Value is PdfObject target) sb.Append(WriteObject(target)).Append(" 0 R");
                else sb.Append("null");
                break;
            case PdfName n: sb.Append(Name(n.Value)); break;
            case PdfString s: sb.Append(String(s.Value ?? string.Empty)); break;
            case PdfBoolean b: sb.Append(b.Value ? "true" : "false"); break;
            case PdfInteger i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfLongInteger li: sb.Append(li.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfReal re: sb.Append(PdfLow.Num(re.Value)); break;
            case PdfArray a:
                sb.Append('[');
                foreach (var e in a.Elements) { WriteItem(sb, e); sb.Append(' '); }
                sb.Append(']');
                break;
            case PdfDictionary d:
                if (d.Stream is not null || d.Reference is not null) sb.Append(WriteObject(d)).Append(" 0 R");
                else WriteDict(sb, d, null);
                break;
            default: sb.Append(item.ToString()); break;
        }
    }

    public static string Name(string name)
    {
        var sb = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(name.TrimStart('/')))
        {
            if (b < 33 || b > 126 || b is (byte)'#' or (byte)'/' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'%')
                sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>Ciąg: ASCII jako literał, pozostałe jako UTF-16BE z BOM (hex).</summary>
    public static string String(string s)
    {
        if (s.All(c => c >= 32 && c < 127 || c is '\n' or '\r' or '\t'))
        {
            var sb = new StringBuilder("(");
            foreach (char c in s)
                sb.Append(c switch { '(' => "\\(", ')' => "\\)", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
            return sb.Append(')').ToString();
        }
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(s));
        return "<" + string.Concat(bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) + ">";
    }

    /// <summary>Składa plik: katalog (obiekt 1) z /FDF podanym jako tekst słownika.</summary>
    public byte[] Build(string fdfDictBody)
    {
        var ms = new MemoryStream();
        Append(ms, "%FDF-1.2\n%âãÏÓ\n");
        Append(ms, $"1 0 obj\n<< /FDF {fdfDictBody} >>\nendobj\n");
        foreach (var (n, body) in _objects.OrderBy(o => o.Number))
        {
            Append(ms, $"{n} 0 obj\n");
            ms.Write(body);
            Append(ms, "\nendobj\n");
        }
        Append(ms, "trailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return ms.ToArray();
    }
}

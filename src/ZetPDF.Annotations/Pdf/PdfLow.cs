using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Niskopoziomowe pomocniki PDFsharp 6.2.4 (słowniki, tablice, daty, kolory). Wszystko w przestrzeni strony PDF
/// (punkty, Y w górę). Nie używamy PdfPage.Annotations (PDFsharp buforuje tam obiekty) – tylko /Annots przez Elements.
/// </summary>
internal static class PdfLow
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static PdfName Name(string name) => new(name.StartsWith('/') ? name : "/" + name);

    /// <summary>Ciąg tekstowy PDF: ASCII jako PDFDocEncoding, inne znaki jako UTF-16BE z BOM.</summary>
    public static PdfString Str(string s)
    {
        foreach (char c in s)
            if (c > 126 || c < 32 && c is not '\n' and not '\r' and not '\t')
                return new PdfString(s, PdfStringEncoding.Unicode);
        return new PdfString(s);
    }

    public static PdfReal Real(double v) => new(Math.Round(v, 4));

    public static PdfArray Arr(PdfDocument doc, params double[] values)
    {
        var a = new PdfArray(doc);
        foreach (var v in values) a.Elements.Add(Real(v));
        return a;
    }

    public static PdfArray RectArray(PdfDocument doc, PdfRect r) => Arr(doc, r.Left, r.Bottom, r.Right, r.Top);

    public static string Num(double v) => Math.Round(v, 4).ToString("0.####", Inv);

    /// <summary>Wartość elementu z rozwinięciem referencji.</summary>
    public static PdfItem? Get(PdfDictionary d, string key)
    {
        var item = d.Elements[key];
        return item is PdfReference r ? r.Value : item;
    }

    public static PdfItem? Deref(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static PdfDictionary? Dict(PdfDictionary d, string key) => Get(d, key) as PdfDictionary;
    public static PdfArray? ArrayOf(PdfDictionary d, string key) => Get(d, key) as PdfArray;

    public static string? NameOf(PdfDictionary d, string key) => Get(d, key) is PdfName n ? n.Value : null;

    public static string Text(PdfDictionary d, string key) => TextOf(Get(d, key));

    public static string TextOf(PdfItem? item) => Deref(item) switch
    {
        PdfString s => s.Value ?? string.Empty,
        PdfName n => n.Value.TrimStart('/'),
        PdfInteger i => i.Value.ToString(Inv),
        PdfReal r => r.Value.ToString(Inv),
        PdfDictionary { Stream: not null } sd => DecodeStreamText(sd),
        _ => string.Empty,
    };

    private static string DecodeStreamText(PdfDictionary sd)
    {
        var b = sd.Stream.UnfilteredValue ?? Array.Empty<byte>();
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return Encoding.Latin1.GetString(b);
    }

    public static double Number(PdfItem? item, double fallback = 0) => Deref(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        _ => fallback,
    };

    public static double Number(PdfDictionary d, string key, double fallback = 0) => Number(d.Elements[key], fallback);

    public static int Int(PdfDictionary d, string key, int fallback = 0) => (int)Number(d.Elements[key], fallback);

    public static double[] Numbers(PdfArray? a)
    {
        if (a is null) return System.Array.Empty<double>();
        var r = new double[a.Elements.Count];
        for (int i = 0; i < r.Length; i++) r[i] = Number(a.Elements[i]);
        return r;
    }

    public static PdfRect? Rect(PdfDictionary d, string key = "/Rect")
    {
        var n = Numbers(ArrayOf(d, key));
        if (n.Length < 4) return null;
        return PdfRect.FromPoints(new PdfPoint(n[0], n[1]), new PdfPoint(n[2], n[3]));
    }

    // ------------------------------------------------------------------ kolory

    public static PdfColor? Color(PdfDictionary d, string key)
    {
        var n = Numbers(ArrayOf(d, key));
        return ColorFrom(n);
    }

    public static PdfColor? ColorFrom(double[] n) => n.Length switch
    {
        1 => Gray(n[0]),
        3 => new PdfColor(255, B(n[0]), B(n[1]), B(n[2])),
        4 => new PdfColor(255, B((1 - n[0]) * (1 - n[3])), B((1 - n[1]) * (1 - n[3])), B((1 - n[2]) * (1 - n[3]))),
        _ => null,
    };

    private static PdfColor Gray(double g) => new(255, B(g), B(g), B(g));
    private static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    public static PdfArray ColorArray(PdfDocument doc, PdfColor c) => Arr(doc, c.R / 255.0, c.G / 255.0, c.B / 255.0);

    /// <summary>Operator koloru wypełnienia/obrysu do strumienia treści ("r g b rg").</summary>
    public static string Rgb(PdfColor c, bool stroke) =>
        $"{Num(c.R / 255.0)} {Num(c.G / 255.0)} {Num(c.B / 255.0)} {(stroke ? "RG" : "rg")}";

    public static string Hex(PdfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static PdfColor? ParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, Inv, out int v)) return null;
        return new PdfColor(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ------------------------------------------------------------------ daty

    public static string FormatDate(DateTimeOffset d)
    {
        var off = d.Offset;
        string sign = off < TimeSpan.Zero ? "-" : "+";
        off = off.Duration();
        return $"D:{d:yyyyMMddHHmmss}{sign}{off.Hours:00}'{off.Minutes:00}'";
    }

    public static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        var digits = new StringBuilder();
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i]) && digits.Length < 14) digits.Append(s[i++]);
        var ds = digits.ToString().PadRight(14, '0');
        if (ds.Length < 4) return null;
        if (!int.TryParse(ds[..4], NumberStyles.None, Inv, out int y)) return null;
        int Part(int start, int def) => int.TryParse(ds.AsSpan(start, 2), NumberStyles.None, Inv, out int v) && v > 0 ? v : def;
        int mo = Math.Clamp(Part(4, 1), 1, 12), dd = Math.Clamp(Part(6, 1), 1, 28 + 3);
        int hh = Math.Clamp(Part(8, 0), 0, 23), mi = Math.Clamp(Part(10, 0), 0, 59), ss = Math.Clamp(Part(12, 0), 0, 59);
        var offset = TimeSpan.Zero;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            int sign = s[i] == '-' ? -1 : 1;
            var rest = new string(s[(i + 1)..].Where(char.IsDigit).ToArray());
            int oh = rest.Length >= 2 ? int.Parse(rest[..2], Inv) : 0;
            int om = rest.Length >= 4 ? int.Parse(rest.Substring(2, 2), Inv) : 0;
            offset = sign * new TimeSpan(Math.Min(oh, 14), Math.Min(om, 59), 0);
        }
        try
        {
            dd = Math.Min(dd, DateTime.DaysInMonth(y, mo));
            return new DateTimeOffset(y, mo, dd, hh, mi, ss, offset);
        }
        catch (ArgumentException) { return null; }
    }

    // ------------------------------------------------------------------ strony i adnotacje

    /// <summary>Tablica /Annots strony (tworzy, jeśli brak; gdy była pośrednia – zostaje pośrednia).</summary>
    public static PdfArray EnsureAnnots(PdfPage page)
    {
        var item = page.Elements["/Annots"];
        if (Deref(item) is PdfArray a) return a;
        var arr = new PdfArray(page.Owner);
        page.Elements["/Annots"] = arr;
        return arr;
    }

    public static PdfArray? Annots(PdfPage page) => Deref(page.Elements["/Annots"]) as PdfArray;

    /// <summary>Słowniki adnotacji strony (z rozwinięciem referencji) wraz z indeksem w /Annots.</summary>
    public static IEnumerable<(PdfDictionary Annot, int Index)> AnnotDicts(PdfPage page)
    {
        var a = Annots(page);
        if (a is null) yield break;
        for (int i = 0; i < a.Elements.Count; i++)
            if (Deref(a.Elements[i]) is PdfDictionary d) yield return (d, i);
    }

    /// <summary>Dodaje obiekt pośredni i zwraca go (PdfDictionary musi mieć Owner = doc).</summary>
    public static T Indirect<T>(PdfDocument doc, T obj) where T : PdfObject
    {
        if (obj.Reference is null) doc.Internals.AddObject(obj);
        return obj;
    }

    /// <summary>Usuwa z tablicy element wskazujący (bezpośrednio lub referencją) na dany obiekt.</summary>
    public static bool RemoveFrom(PdfArray array, PdfObject target)
    {
        for (int i = array.Elements.Count - 1; i >= 0; i--)
        {
            var it = array.Elements[i];
            if (ReferenceEquals(it, target) || it is PdfReference r && ReferenceEquals(r.Value, target))
            {
                array.Elements.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public static int PageIndexOf(PdfDocument doc, PdfDictionary? pageDict)
    {
        if (pageDict is null) return -1;
        for (int i = 0; i < doc.PageCount; i++)
            if (ReferenceEquals(doc.Pages[i], pageDict) || doc.Pages[i].Reference is { } r && ReferenceEquals(r.Value, pageDict)) return i;
        return -1;
    }

    /// <summary>Nowy unikalny identyfikator adnotacji (/NM).</summary>
    public static string NewName() => "zet-" + Guid.NewGuid().ToString("N");

    public static PdfDictionary NewDict(PdfDocument doc, string? type = null)
    {
        var d = new PdfDictionary(doc);
        if (type is not null) d.Elements["/Type"] = Name(type);
        return d;
    }

    /// <summary>Strumień formy (XObject Form) z treścią w przestrzeni BBox.</summary>
    public static PdfDictionary FormXObject(PdfDocument doc, PdfRect bbox, string content, PdfDictionary? resources = null, PdfArray? matrix = null)
    {
        var f = NewDict(doc, "/XObject");
        f.Elements["/Subtype"] = Name("/Form");
        f.Elements["/FormType"] = new PdfInteger(1);
        f.Elements["/BBox"] = RectArray(doc, bbox);
        if (matrix is not null) f.Elements["/Matrix"] = matrix;
        f.Elements["/Resources"] = resources ?? NewDict(doc);
        f.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(f);
        return f;
    }

    /// <summary>Kopia głęboka obiektu bezpośredniego (do zmiany geometrii bez wpływu na inne obiekty).</summary>
    public static string Bool(bool b) => b ? "true" : "false";
}

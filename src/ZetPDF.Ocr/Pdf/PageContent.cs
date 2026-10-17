using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace ZetPDF.Ocr.Pdf;

/// <summary>
/// Operacje na strumieniach treści strony (PDFsharp 6.2.4): odczyt, zastąpienie, dopisanie warstwy
/// z gwarancją czystego stanu graficznego (owinięcie istniejącej treści w q/Q, jeśli zostawia zmieniony CTM).
/// </summary>
public static class PageContent
{
    /// <summary>Znacznik (komentarz w pierwszej linii) strumieni dopisanych przez ZetPDF – pozwala je odnaleźć i zastąpić.</summary>
    public const string MarkerPrefix = "%ZetPDF:";

    /// <summary>Wszystkie strumienie treści strony (słowniki), w kolejności.</summary>
    public static IReadOnlyList<PdfDictionary> Streams(PdfPage page)
    {
        var list = new List<PdfDictionary>();
        var item = page.Elements["/Contents"];
        if (item is PdfReference r) item = r.Value;
        if (item is PdfArray arr)
        {
            foreach (var e in arr.Elements)
            {
                var v = e is PdfReference er ? er.Value : e;
                if (v is PdfDictionary d && d.Stream is not null) list.Add(d);
            }
        }
        else if (item is PdfDictionary d && d.Stream is not null) list.Add(d);
        return list;
    }

    /// <summary>Zdekodowane bajty strumienia (bez zmiany obiektu).</summary>
    public static byte[] Decoded(PdfDictionary stream)
    {
        var s = stream.Stream;
        if (s is null) return Array.Empty<byte>();
        if (stream.Elements["/Filter"] is null) return s.Value;
        return s.UnfilteredValue;
    }

    /// <summary>Połączona, zdekodowana treść strony.</summary>
    public static byte[] ReadAll(PdfPage page)
    {
        using var ms = new MemoryStream();
        foreach (var s in Streams(page))
        {
            ms.Write(Decoded(s));
            ms.WriteByte((byte)'\n');
        }
        return ms.ToArray();
    }

    /// <summary>Sparsowana treść strony (wszystkie strumienie razem).</summary>
    public static CSequence Parse(PdfPage page) => ContentReader.ReadContent(ReadAll(page));

    /// <summary>Zastępuje całą treść strony jednym strumieniem.</summary>
    public static void Replace(PdfPage page, byte[] content)
    {
        var doc = page.Owner;
        var dict = new PdfDictionary(doc);
        dict.CreateStream(content);
        doc.Internals.AddObject(dict);
        page.Elements["/Contents"] = dict.Reference!;
    }

    /// <summary>
    /// Dopisuje strumień na końcu treści strony. Jeśli istniejąca treść nie jest zrównoważona (q/Q) lub zmienia CTM/obcięcie
    /// na poziomie 0, najpierw owija ją w q … Q, żeby nowa warstwa miała współrzędne przestrzeni użytkownika strony.
    /// </summary>
    public static PdfDictionary Append(PdfPage page, byte[] content, string? marker = null)
    {
        EnsureCleanState(page);
        var doc = page.Owner;
        var payload = marker is null ? content : Encoding.ASCII.GetBytes(MarkerPrefix + marker + "\n").Concat(content).ToArray();
        var dict = new PdfDictionary(doc);
        dict.CreateStream(payload);
        doc.Internals.AddObject(dict);
        AddStream(page, dict, atStart: false);
        return dict;
    }

    /// <summary>Usuwa strumienie dopisane przez ZetPDF z danym znacznikiem. Zwraca liczbę usuniętych.</summary>
    public static int RemoveMarked(PdfPage page, string marker)
    {
        var tag = Encoding.ASCII.GetBytes(MarkerPrefix + marker);
        var keep = new List<PdfDictionary>();
        int removed = 0;
        foreach (var s in Streams(page))
        {
            var data = Decoded(s);
            if (data.Length >= tag.Length && data.AsSpan(0, tag.Length).SequenceEqual(tag)) removed++;
            else keep.Add(s);
        }
        if (removed == 0) return 0;
        var arr = new PdfArray(page.Owner);
        foreach (var s in keep) arr.Elements.Add(s.Reference is not null ? s.Reference : s);
        page.Elements["/Contents"] = arr;
        return removed;
    }

    /// <summary>Czy treść zostawia stan graficzny niezmieniony (bilans q/Q = 0, brak cm/W na poziomie 0).</summary>
    public static bool IsStateClean(PdfPage page)
    {
        CSequence seq;
        try { seq = Parse(page); }
        catch (Exception) { return false; }
        int depth = 0;
        foreach (var item in seq)
        {
            if (item is not COperator op) continue;
            switch (op.OpCode.Name)
            {
                case "q": depth++; break;
                case "Q": depth--; if (depth < 0) return false; break;
                case "cm":
                case "W":
                case "W*":
                    if (depth == 0) return false;
                    break;
            }
        }
        return depth == 0;
    }

    /// <summary>Owija istniejącą treść w q … Q, jeśli to potrzebne.</summary>
    public static void EnsureCleanState(PdfPage page)
    {
        if (Streams(page).Count == 0 || IsStateClean(page)) return;
        var doc = page.Owner;
        var open = new PdfDictionary(doc);
        open.CreateStream(Encoding.ASCII.GetBytes("q\n"));
        doc.Internals.AddObject(open);
        var close = new PdfDictionary(doc);
        close.CreateStream(Encoding.ASCII.GetBytes("\nQ\n"));
        doc.Internals.AddObject(close);
        AddStream(page, open, atStart: true);
        AddStream(page, close, atStart: false);
    }

    private static void AddStream(PdfPage page, PdfDictionary dict, bool atStart)
    {
        var arr = new PdfArray(page.Owner);
        var existing = Streams(page);
        if (atStart) arr.Elements.Add(dict.Reference!);
        foreach (var s in existing) arr.Elements.Add(s.Reference is not null ? s.Reference : s);
        if (!atStart) arr.Elements.Add(dict.Reference!);
        page.Elements["/Contents"] = arr;
    }

    // ------------------------------------------------------------------ formatowanie liczb i napisów

    public static string Num(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
        if (Math.Abs(v) < 1e-7) return "0";
        return Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture);
    }

    public static string Hex(string bytesAsChars)
    {
        var sb = new StringBuilder(bytesAsChars.Length * 2 + 2);
        sb.Append('<');
        foreach (var c in bytesAsChars) sb.Append(((int)c & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
        sb.Append('>');
        return sb.ToString();
    }

    public static string Matrix(double a, double b, double c, double d, double e, double f) =>
        $"{Num(a)} {Num(b)} {Num(c)} {Num(d)} {Num(e)} {Num(f)}";
}

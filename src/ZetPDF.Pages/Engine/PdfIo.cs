using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Pages.Engine;

/// <summary>
/// Wspólne pomocniki silnika stron (PDFsharp 6.2.4 MIT): otwieranie, zapis, rozwiązywanie referencji,
/// ramki stron z uwzględnieniem /Rotate, dopisywanie strumieni treści, bezpieczny zapis plików.
/// </summary>
internal static class PdfIo
{
    public static PdfDocument OpenModify(byte[] pdf)
    {
        try { return PdfSharpEdit.OpenForModify(pdf); }
        catch (PdfReaderException ex) { throw new PdfCorruptException(ex.Message, ex); }
    }

    public static PdfDocument OpenImport(byte[] pdf)
    {
        try { return PdfSharpEdit.OpenForImport(pdf); }
        catch (PdfReaderException ex) { throw new PdfCorruptException(ex.Message, ex); }
    }

    public static byte[] Save(PdfDocument doc) => PdfSharpEdit.Save(doc);

    /// <summary>Nowy pusty dokument (bez stron) z inicjalizacją czcionek.</summary>
    public static PdfDocument NewDocument()
    {
        PdfSharpEdit.EnsureInitialized();
        return new PdfDocument();
    }

    // ------------------------------------------------------------------ obiekty

    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static PdfDictionary? GetDict(PdfDictionary owner, string key) => Resolve(owner.Elements[key]) as PdfDictionary;

    public static PdfArray? GetArray(PdfDictionary owner, string key) => Resolve(owner.Elements[key]) as PdfArray;

    /// <summary>Liczby z tablicy lub PdfRectangle (PDFsharp zamienia /Rect, /BBox na PdfRectangle).</summary>
    public static double[]? GetNumbers(PdfDictionary owner, string key) => Resolve(owner.Elements[key]) switch
    {
        PdfArray a => a.Elements.Select(e => GetNumber(e) ?? 0).ToArray(),
        PdfRectangle r => new[] { r.X1, r.Y1, r.X2, r.Y2 },
        _ => null,
    };

    public static string? GetName(PdfDictionary owner, string key) => Resolve(owner.Elements[key]) is PdfName n ? n.Value : null;

    public static double? GetNumber(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        PdfIntegerObject io => io.Value,
        PdfRealObject ro => ro.Value,
        _ => null,
    };

    /// <summary>Tekst z PdfString (także Unicode) lub null.</summary>
    public static string? GetText(PdfItem? item) => Resolve(item) switch
    {
        PdfString s => s.Value,
        PdfStringObject so => so.Value,
        PdfName n => n.Value.TrimStart('/'),
        _ => null,
    };

    /// <summary>Słownik zasobów strony (tworzy, jeśli brak).</summary>
    public static PdfDictionary Resources(PdfPage page) => page.Resources;

    /// <summary>Podsłownik zasobów (np. /XObject), tworzony w miejscu, jeśli brak.</summary>
    public static PdfDictionary SubResources(PdfDictionary resources, string key)
    {
        if (Resolve(resources.Elements[key]) is PdfDictionary d) return d;
        var created = new PdfDictionary();
        resources.Elements[key] = created;
        return created;
    }

    /// <summary>Unikalna nazwa zasobu w podsłowniku (np. "/ZWm1").</summary>
    public static string UniqueName(PdfDictionary sub, string prefix)
    {
        for (int i = 1; ; i++)
        {
            var name = "/" + prefix + i.ToString(CultureInfo.InvariantCulture);
            if (!sub.Elements.ContainsKey(name)) return name;
        }
    }

    /// <summary>Strumień jako obiekt pośredni dokumentu.</summary>
    public static PdfDictionary NewStream(PdfDocument doc, byte[] data, params (string Key, PdfItem Value)[] entries)
    {
        var d = new PdfDictionary(doc);
        foreach (var (k, v) in entries) d.Elements[k] = v;
        d.CreateStream(data);
        doc.Internals.AddObject(d);
        return d;
    }

    public static PdfArray NumberArray(PdfDocument? doc, params double[] values)
    {
        var a = doc is null ? new PdfArray() : new PdfArray(doc);
        foreach (var v in values) a.Elements.Add(new PdfReal(Math.Round(v, 4)));
        return a;
    }

    public static string Num(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    public static string Matrix(PdfMatrix m) => $"{Num(m.A)} {Num(m.B)} {Num(m.C)} {Num(m.D)} {Num(m.E)} {Num(m.F)}";

    public static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    // ------------------------------------------------------------------ ramki stron

    /// <summary>Normalizuje kąt do 0/90/180/270.</summary>
    public static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

    public static int GetRotation(PdfPage page)
    {
        var v = GetNumber(page.Elements["/Rotate"]);
        return v is null ? 0 : NormalizeRotation((int)v.Value);
    }

    public static PdfRect ToRect(PdfRectangle r) =>
        new(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));

    public static PdfRectangle ToPdfRectangle(PdfRect r) => new(new XPoint(r.Left, r.Bottom), new XPoint(r.Right, r.Top));

    public static PdfRect MediaBox(PdfPage page)
    {
        var r = ToRect(page.MediaBoxReadOnly);
        return r.IsEmpty ? new PdfRect(0, 0, PdfSize.A4.Width, PdfSize.A4.Height) : r;
    }

    /// <summary>Widoczna ramka strony: CropBox przycięty do MediaBox (lub MediaBox).</summary>
    public static PdfRect VisibleBox(PdfPage page)
    {
        var media = MediaBox(page);
        if (!page.HasCropBox) return media;
        var crop = ToRect(page.CropBoxReadOnly);
        var inter = new PdfRect(Math.Max(crop.Left, media.Left), Math.Max(crop.Bottom, media.Bottom), Math.Min(crop.Right, media.Right), Math.Min(crop.Top, media.Top));
        return inter.IsEmpty ? media : inter;
    }

    /// <summary>
    /// Ramka strony tak, jak widzi ją użytkownik (po /Rotate): rozmiar wyświetlany i macierz
    /// "układ wyświetlany (początek lewy-dolny, Y w górę) → przestrzeń użytkownika strony".
    /// </summary>
    public static DisplayFrame Frame(PdfPage page) => DisplayFrame.Create(VisibleBox(page), GetRotation(page));

    // ------------------------------------------------------------------ strumienie treści

    public const string MarkKey = "/ZetPDFMark";
    public const string OptionsKey = "/ZetPDFOptions";
    public const string WrapMark = "/Wrap";

    /// <summary>Dopisuje strumień treści na końcu strony (z owinięciem dotychczasowej treści w q/Q).</summary>
    public static PdfDictionary AppendContent(PdfPage page, byte[] data, string? mark = null)
    {
        EnsureWrapped(page);
        var c = page.Contents.AppendContent();
        c.CreateStream(data);
        if (mark is not null) c.Elements[MarkKey] = new PdfName(mark);
        return c;
    }

    /// <summary>Wstawia strumień treści na początku strony (pod istniejącą treścią).</summary>
    public static PdfDictionary PrependContent(PdfPage page, byte[] data, string? mark = null)
    {
        var c = page.Contents.PrependContent();
        c.CreateStream(data);
        if (mark is not null) c.Elements[MarkKey] = new PdfName(mark);
        return c;
    }

    /// <summary>
    /// Owija oryginalną treść strony w q … Q (osobne strumienie oznaczone /ZetPDFMark /Wrap), żeby dopisana
    /// treść nie dziedziczyła stanu graficznego (np. niezamkniętego cm). Idempotentne.
    /// </summary>
    public static void EnsureWrapped(PdfPage page)
    {
        var contents = page.Contents;
        var items = ContentDicts(page).ToList();
        if (items.Count == 0) return;
        bool hasOpen = items.Any(d => GetName(d, MarkKey) == WrapMark && IsSingleOperator(d, "q"));
        if (hasOpen) return;
        // q na samym początku (przed ewentualnymi tłami/znakami pod treścią – one i tak są samodzielne q..Q)
        var open = contents.PrependContent();
        open.CreateStream(Latin1("q\n"));
        open.Elements[MarkKey] = new PdfName(WrapMark);
        var close = contents.AppendContent();
        close.CreateStream(Latin1("\nQ\n"));
        close.Elements[MarkKey] = new PdfName(WrapMark);
    }

    private static bool IsSingleOperator(PdfDictionary d, string op)
    {
        try { return Encoding.Latin1.GetString(d.Stream?.UnfilteredValue ?? Array.Empty<byte>()).Trim() == op; }
        catch (Exception) { return false; }
    }

    /// <summary>Słowniki strumieni treści strony w kolejności.</summary>
    public static IEnumerable<PdfDictionary> ContentDicts(PdfPage page)
    {
        var item = Resolve(page.Elements["/Contents"]);
        if (item is PdfDictionary single) { yield return single; yield break; }
        if (item is PdfArray arr)
            foreach (var e in arr.Elements)
                if (Resolve(e) is PdfDictionary d) yield return d;
    }

    /// <summary>Połączone (rozpakowane) bajty treści strony.</summary>
    public static byte[] ReadContent(PdfPage page)
    {
        using var ms = new MemoryStream();
        foreach (var d in ContentDicts(page))
        {
            var data = StreamData(d);
            ms.Write(data);
            ms.WriteByte((byte)'\n');
        }
        return ms.ToArray();
    }

    /// <summary>Rozpakowane dane strumienia (puste, gdy filtr nieobsługiwany).</summary>
    public static byte[] StreamData(PdfDictionary d)
    {
        if (d.Stream is null) return Array.Empty<byte>();
        try { return d.Stream.UnfilteredValue ?? Array.Empty<byte>(); }
        catch (Exception) { return Array.Empty<byte>(); }
    }

    /// <summary>Zastępuje dane strumienia (bez filtra; kompresja przy zapisie wg opcji dokumentu).</summary>
    public static void SetStreamData(PdfDictionary d, byte[] data)
    {
        d.Elements.Remove("/Filter");
        d.Elements.Remove("/DecodeParms");
        if (d.Stream is null) d.CreateStream(data);
        else d.Stream.Value = data;
        d.Elements.SetInteger("/Length", data.Length);
    }

    /// <summary>Indeks strony po obiekcie (referencji) lub -1.</summary>
    public static Dictionary<PdfObjectID, int> PageIndexMap(PdfDocument doc)
    {
        var map = new Dictionary<PdfObjectID, int>();
        for (int i = 0; i < doc.PageCount; i++)
            if (doc.Pages[i].Reference is { } r) map[r.ObjectID] = i;
        return map;
    }

    // ------------------------------------------------------------------ pliki

    /// <summary>Zapis przez plik tymczasowy w tym samym folderze, potem podmiana (File.Replace) lub przeniesienie.</summary>
    public static void WriteFileSafe(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllBytes(tmp, data);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Ścieżka nieistniejącego pliku (dodaje " (2)", " (3)"… przy kolizji).</summary>
    public static string UniquePath(string path, ISet<string>? taken = null)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidate = path;
        for (int i = 2; File.Exists(candidate) || (taken?.Contains(candidate) ?? false); i++)
            candidate = Path.Combine(dir, $"{name} ({i}){ext}");
        taken?.Add(candidate);
        return candidate;
    }

    /// <summary>Usuwa znaki niedozwolone w nazwie pliku.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }
}

/// <summary>Ramka strony w układzie wyświetlanym (po /Rotate) z macierzą do przestrzeni użytkownika.</summary>
internal readonly record struct DisplayFrame(double Width, double Height, PdfMatrix ToUser, PdfRect UserBox, int Rotation)
{
    public static DisplayFrame Create(PdfRect box, int rotation)
    {
        rotation = PdfIo.NormalizeRotation(rotation);
        var m = rotation switch
        {
            90 => new PdfMatrix(0, 1, -1, 0, box.Right, box.Bottom),
            180 => new PdfMatrix(-1, 0, 0, -1, box.Right, box.Top),
            270 => new PdfMatrix(0, -1, 1, 0, box.Left, box.Top),
            _ => new PdfMatrix(1, 0, 0, 1, box.Left, box.Bottom),
        };
        bool swap = rotation is 90 or 270;
        return new DisplayFrame(swap ? box.Height : box.Width, swap ? box.Width : box.Height, m, box, rotation);
    }

    /// <summary>Prostokąt w układzie wyświetlanym → prostokąt w przestrzeni użytkownika.</summary>
    public PdfRect DisplayToUser(PdfRect r)
    {
        var a = ToUser.Transform(new PdfPoint(r.Left, r.Bottom));
        var b = ToUser.Transform(new PdfPoint(r.Right, r.Top));
        return PdfRect.FromPoints(a, b);
    }
}

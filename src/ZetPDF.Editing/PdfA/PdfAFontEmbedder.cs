using System.Text;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Pdf;
using ZetPDF.Editing.Pdf;
using ZetPDF.Editing.Text;
using FontStyle = System.Windows.FontStyle;

namespace ZetPDF.Editing.PdfA;

/// <summary>
/// Osadzanie czcionek nieosadzonych z systemu, wyłącznie gdy układ tekstu się nie zmieni: czcionka prosta z kodowaniem
/// WinAnsi, rodzina zgodna metrycznie (Helvetica → Arial, Times → Times New Roman, Courier → Courier New) albo ta sama
/// rodzina zainstalowana w systemie, szerokości z pliku zgodne z /Widths, licencja czcionki pozwala osadzać.
/// Osadzany jest cały plik czcionki, bo podzbiór wymagałby przebudowy tablicy cmap. Inne przypadki: rasteryzacja strony za zgodą.
/// </summary>
internal static class PdfAFontEmbedder
{
    private const int WidthTolerance = 5; // w tysięcznych jednostki em
    private const double FirstCode = 32, LastCode = 255;

    public sealed record Candidate(string Family, bool Bold, bool Italic, GlyphTypeface Typeface, int[] Widths)
    {
        public string PdfName => Family.Replace(" ", string.Empty) + (Bold || Italic ? "-" : string.Empty) + (Bold ? "Bold" : string.Empty) + (Italic ? "Italic" : string.Empty);
    }

    public static string BaseName(PdfDictionary font) => FontNames.StripSubset(font.Elements.GetName("/BaseFont").TrimStart('/'));

    /// <summary>Czy słownik wskazuje osadzony program czcionki (Type3 ma glify w pliku, Type0 – przez czcionkę potomną).</summary>
    public static bool IsEmbedded(PdfDictionary font)
    {
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type3") return true;
        if (subtype != "/Type0") return HasFontFile(font);
        // Tylko jeden poziom i tylko czcionka CID: spreparowany Type0 może wskazywać sam siebie.
        return PdfObjects.Resolve(font.Elements["/DescendantFonts"]) is PdfArray { Elements.Count: > 0 } a
            && PdfObjects.Resolve(a.Elements[0]) is PdfDictionary child
            && child.Elements.GetName("/Subtype") is "/CIDFontType0" or "/CIDFontType2"
            && HasFontFile(child);
    }

    private static bool HasFontFile(PdfDictionary font) =>
        PdfObjects.Resolve(font.Elements["/FontDescriptor"]) is PdfDictionary d &&
        (d.Elements.ContainsKey("/FontFile") || d.Elements.ContainsKey("/FontFile2") || d.Elements.ContainsKey("/FontFile3"));

    /// <summary>Rodzina zastępcza zgodna metrycznie albo o tej samej nazwie; null, gdy brak.</summary>
    internal static (string Family, bool Metric)? MapFamily(string baseName)
    {
        var n = baseName.Replace(" ", string.Empty);
        if (n.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase)) return ("Arial", true);
        if (n.StartsWith("ArialNarrow", StringComparison.OrdinalIgnoreCase)) return ("Arial Narrow", true);
        if (n.StartsWith("Arial", StringComparison.OrdinalIgnoreCase)) return ("Arial", true);
        if (n.StartsWith("TimesNewRoman", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Times-", StringComparison.OrdinalIgnoreCase) || n.Equals("Times", StringComparison.OrdinalIgnoreCase)) return ("Times New Roman", true);
        if (n.StartsWith("Courier", StringComparison.OrdinalIgnoreCase)) return ("Courier New", true);
        foreach (var family in new[] { "Calibri", "Cambria", "Verdana", "Tahoma", "Georgia", "Consolas", "Segoe UI", "Trebuchet MS", "Garamond", "Book Antiqua", "Century Gothic" })
            if (n.StartsWith(family.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase)) return (family, false);
        return null;
    }

    /// <summary>Sprawdza warunki osadzenia; bez zmian w dokumencie.</summary>
    public static Candidate? TryPrepare(PdfDictionary font)
    {
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype is not ("/Type1" or "/MMType1" or "/TrueType")) return null;
        if (PdfObjects.Resolve(font.Elements["/Encoding"]) is not PdfName { Value: "/WinAnsiEncoding" }) return null;
        var name = BaseName(font);
        if (MapFamily(name) is not { } map) return null;
        bool bold = FontNames.IsBold(name), italic = FontNames.IsItalic(name);
        var typeface = new Typeface(new FontFamily(map.Family), italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphs)) return null;
        // WPF podstawia inną rodzinę, gdy żądanej nie ma; sprawdzamy nazwę z pliku czcionki.
        if (!glyphs.Win32FamilyNames.Values.Any(v => v.Equals(map.Family, StringComparison.OrdinalIgnoreCase)) && !glyphs.FamilyNames.Values.Any(v => v.Equals(map.Family, StringComparison.OrdinalIgnoreCase))) return null;
        if (glyphs.EmbeddingRights == FontEmbeddingRight.RestrictedLicense || glyphs.EmbeddingRights.ToString().Contains("BitmapsOnly", StringComparison.Ordinal)) return null;
        var widths = FontWidths(glyphs);
        if (!WidthsAgree(font, widths, requireWidths: !map.Metric)) return null;
        return new Candidate(map.Family, bold, italic, glyphs, widths);
    }

    private static int[] FontWidths(GlyphTypeface g)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp = Encoding.GetEncoding(1252);
        var widths = new int[(int)(LastCode - FirstCode) + 1];
        for (int code = (int)FirstCode; code <= LastCode; code++)
        {
            var ch = cp.GetString(new[] { (byte)code })[0];
            widths[code - (int)FirstCode] = g.CharacterToGlyphMap.TryGetValue(ch, out var glyph) ? (int)Math.Round(g.AdvanceWidths[glyph] * 1000) : 0;
        }
        return widths;
    }

    /// <summary>Szerokości zapisane w PDF muszą odpowiadać czcionce; inaczej tekst by się przesunął.</summary>
    private static bool WidthsAgree(PdfDictionary font, int[] widths, bool requireWidths)
    {
        if (PdfObjects.Resolve(font.Elements["/Widths"]) is not PdfArray given) return !requireWidths;
        int first = font.Elements.GetInteger("/FirstChar");
        for (int i = 0; i < given.Elements.Count; i++)
        {
            int code = first + i;
            if (code < FirstCode || code > LastCode) continue;
            double w = PdfObjects.Number(given.Elements[i]), expected = widths[code - (int)FirstCode];
            if (w > 0 && expected > 0 && Math.Abs(w - expected) > WidthTolerance) return false;
        }
        return true;
    }

    /// <summary>Zamienia słownik czcionki na prostą TrueType z osadzonym plikiem. Pliki współdzielone w obrębie dokumentu.</summary>
    public static void Embed(PdfDocument doc, PdfDictionary font, Candidate c, Dictionary<string, PdfDictionary> descriptors)
    {
        if (!descriptors.TryGetValue(c.PdfName, out var descriptor))
        {
            using var stream = c.Typeface.GetFontStream();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var data = ms.ToArray();
            var file = new PdfDictionary(doc);
            file.CreateStream(data);
            file.Elements.SetInteger("/Length1", data.Length);
            file.Stream.Zip();
            doc.Internals.AddObject(file);

            int flags = 32 | (c.Italic ? 64 : 0) | (c.Family == "Courier New" ? 1 : 0) | (c.Family is "Times New Roman" or "Cambria" or "Georgia" or "Garamond" or "Book Antiqua" ? 2 : 0);
            descriptor = new PdfDictionary(doc);
            descriptor.Elements["/Type"] = new PdfName("/FontDescriptor");
            descriptor.Elements["/FontName"] = new PdfName("/" + c.PdfName);
            descriptor.Elements.SetInteger("/Flags", flags);
            var box = ReadBoundingBox(data);
            descriptor.Elements["/FontBBox"] = new PdfArray(doc, box.Select(v => (PdfItem)new PdfInteger(v)).ToArray());
            descriptor.Elements.SetInteger("/ItalicAngle", c.Italic ? -12 : 0);
            descriptor.Elements.SetInteger("/Ascent", (int)Math.Round(c.Typeface.Baseline * 1000));
            descriptor.Elements.SetInteger("/Descent", (int)Math.Round((c.Typeface.Baseline - c.Typeface.Height) * 1000));
            descriptor.Elements.SetInteger("/CapHeight", (int)Math.Round(c.Typeface.CapsHeight * 1000));
            descriptor.Elements.SetInteger("/StemV", c.Bold ? 140 : 80);
            descriptor.Elements["/FontFile2"] = file.Reference!;
            doc.Internals.AddObject(descriptor);
            descriptors[c.PdfName] = descriptor;
        }
        font.Elements["/Subtype"] = new PdfName("/TrueType");
        font.Elements["/BaseFont"] = new PdfName("/" + c.PdfName);
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        font.Elements.SetInteger("/FirstChar", (int)FirstCode);
        font.Elements.SetInteger("/LastChar", (int)LastCode);
        font.Elements["/Widths"] = new PdfArray(doc, c.Widths.Select(w => (PdfItem)new PdfInteger(w)).ToArray());
        font.Elements["/FontDescriptor"] = descriptor.Reference!;
    }

    /// <summary>Prostokąt z tablicy head w tysięcznych em; przy nietypowym pliku wartości ogólne.</summary>
    private static int[] ReadBoundingBox(byte[] d)
    {
        int U16(int p) => (d[p] << 8) | d[p + 1];
        int S16(int p) => (short)U16(p);
        long U32(int p) => ((long)d[p] << 24) | ((long)d[p + 1] << 16) | ((long)d[p + 2] << 8) | d[p + 3];
        if (d.Length > 12)
        {
            int tables = U16(4);
            for (int i = 0; i < tables && 12 + i * 16 + 16 <= d.Length; i++)
            {
                int e = 12 + i * 16;
                if (d[e] != 'h' || d[e + 1] != 'e' || d[e + 2] != 'a' || d[e + 3] != 'd') continue;
                long at = U32(e + 8);
                if (at + 54 > d.Length) break;
                int p = (int)at, units = Math.Max(1, U16(p + 18));
                return new[] { S16(p + 36), S16(p + 38), S16(p + 40), S16(p + 42) }.Select(v => (int)Math.Round(v * 1000.0 / units)).ToArray();
            }
        }
        return new[] { -1000, -500, 2000, 1500 };
    }
}

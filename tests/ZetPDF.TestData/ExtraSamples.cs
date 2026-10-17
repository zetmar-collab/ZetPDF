using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Documents;

namespace ZetPDF.TestData;

/// <summary>
/// Dodatkowe próbki testera (czarna skrzynka). Generowane kodem, bez plików z sieci i bez prywatnych danych.
/// </summary>
public static class ExtraSamples
{
    public const string SecretPhrase = "TAJNE-HASLO-7731";
    public const string XmpSecret = "Sekret-XMP-4471";
    public const string JsMarker = "zetpdfJsMarker";
    public const string AttachmentMarker = "ZAWARTOSC-ZALACZNIKA-9911";

    private static XFont Font(double size) => new("Arial", size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    /// <summary>
    /// Strona z tekstem zapisanym DOSŁOWNIE w strumieniu treści (Helvetica, WinAnsi, operator Tj) – fraza jest widoczna
    /// w surowych bajtach po dekompresji, więc da się sprawdzić, że redakcja usuwa ją ze strumienia, a nie tylko zakrywa.
    /// Tytuł dokumentu też zawiera frazę.
    /// </summary>
    public static byte[] WithLiteralText(string[] lines, string? title = null, bool compress = true)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        doc.Options.CompressContentStreams = compress;
        if (title is not null) doc.Info.Title = title;
        var page = doc.AddPage();
        var font = new PdfDictionary(doc);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(font);
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F1"] = font.Reference!;
        var res = new PdfDictionary(doc);
        res.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = res;

        var sb = new StringBuilder();
        double y = 760;
        foreach (var line in lines)
        {
            var escaped = line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            sb.Append("BT /F1 14 Tf 56 ").Append(y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" Td (").Append(escaped).Append(") Tj ET\n");
            y -= 28;
        }
        var content = new PdfDictionary(doc);
        content.CreateStream(Encoding.Latin1.GetBytes(sb.ToString()));
        if (compress)
        {
            var data = content.Stream.Value;
            content.Stream.Value = new PdfSharp.Pdf.Filters.FlateDecode().Encode(data, PdfFlateEncodeMode.BestCompression);
            content.Elements["/Filter"] = new PdfName("/FlateDecode");
        }
        doc.Internals.AddObject(content);
        page.Elements["/Contents"] = content.Reference!;
        return PdfSharpEdit.Save(doc);
    }

    /// <summary>
    /// Pod oczyszczanie: /Info (tytuł, autor, temat, słowa kluczowe), strumień XMP z sekretem, JavaScript w OpenAction,
    /// w drzewie /Names, w akcji strony /AA i w łączu; załącznik w EmbeddedFiles i adnotacja FileAttachment; komentarz.
    /// </summary>
    public static byte[] WithEverythingHidden()
    {
        return PdfSharpEdit.Modify(SamplePdfFactory.WithMetadataAttachmentAndJavaScript(), doc =>
        {
            doc.Info.Author = "Autor Tajny";
            // XMP
            var xmp = $"<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description>{XmpSecret}</dc:description></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
            var meta = new PdfDictionary(doc);
            meta.Elements["/Type"] = new PdfName("/Metadata");
            meta.Elements["/Subtype"] = new PdfName("/XML");
            meta.CreateStream(Encoding.UTF8.GetBytes(xmp));
            doc.Internals.AddObject(meta);
            doc.Internals.Catalog.Elements["/Metadata"] = meta.Reference!;

            PdfDictionary Js(string code)
            {
                var js = new PdfDictionary(doc);
                js.Elements["/S"] = new PdfName("/JavaScript");
                js.Elements["/JS"] = new PdfString(code);
                doc.Internals.AddObject(js);
                return js;
            }

            var page = doc.Pages[0];
            var aa = new PdfDictionary(doc);
            aa.Elements["/O"] = Js($"var a='{JsMarker}-page';");
            page.Elements["/AA"] = aa;

            var annots = page.Elements.GetArray("/Annots") ?? new PdfArray(doc);
            var link = new PdfDictionary(doc);
            link.Elements["/Type"] = new PdfName("/Annot");
            link.Elements["/Subtype"] = new PdfName("/Link");
            link.Elements["/Rect"] = new PdfRectangle(new XRect(100, 100, 80, 20));
            link.Elements["/A"] = Js($"var b='{JsMarker}-link';");
            doc.Internals.AddObject(link);
            annots.Elements.Add(link.Reference!);

            // adnotacja FileAttachment z osadzonym plikiem
            var ef = new PdfDictionary(doc);
            ef.Elements["/Type"] = new PdfName("/EmbeddedFile");
            ef.CreateStream(Encoding.UTF8.GetBytes(AttachmentMarker));
            doc.Internals.AddObject(ef);
            var efd = new PdfDictionary(doc);
            efd.Elements["/F"] = ef.Reference!;
            var spec = new PdfDictionary(doc);
            spec.Elements["/Type"] = new PdfName("/Filespec");
            spec.Elements["/F"] = new PdfString("adnotacja.txt");
            spec.Elements["/EF"] = efd;
            doc.Internals.AddObject(spec);
            var fa = new PdfDictionary(doc);
            fa.Elements["/Type"] = new PdfName("/Annot");
            fa.Elements["/Subtype"] = new PdfName("/FileAttachment");
            fa.Elements["/Rect"] = new PdfRectangle(new XRect(300, 300, 20, 20));
            fa.Elements["/FS"] = spec.Reference!;
            fa.Elements["/Contents"] = new PdfString("załącznik w adnotacji");
            doc.Internals.AddObject(fa);
            annots.Elements.Add(fa.Reference!);

            var note = new PdfDictionary(doc);
            note.Elements["/Type"] = new PdfName("/Annot");
            note.Elements["/Subtype"] = new PdfName("/Text");
            note.Elements["/Rect"] = new PdfRectangle(new XRect(400, 500, 20, 20));
            note.Elements["/Contents"] = new PdfString("komentarz do usunięcia", PdfStringEncoding.Unicode);
            doc.Internals.AddObject(note);
            annots.Elements.Add(note.Reference!);
            page.Elements["/Annots"] = annots;
        });
    }

    /// <summary>Strony z pełnym prostokątem koloru: strona <paramref name="redPage"/> (0-based) czerwona, pozostałe niebieskie.</summary>
    public static byte[] ColoredPages(int pages, int redPage)
    {
        PdfSharpEdit.EnsureInitialized();
        using var doc = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var p = doc.AddPage();
            using var g = XGraphics.FromPdfPage(p);
            g.DrawRectangle(i == redPage ? XBrushes.Red : XBrushes.Blue, 0, 0, p.Width.Point, p.Height.Point);
            g.DrawString($"Strona {i + 1}", Font(30), XBrushes.White, new XPoint(60, 100));
        }
        return PdfSharpEdit.Save(doc);
    }

    /// <summary>Tekst z adresem URL i e-mailem (do łączy); <paramref name="path"/> – ścieżka adresu (np. z polskimi znakami).</summary>
    public static byte[] WithUrls(string path = "sciezka") =>
        SamplePdfFactory.Text(1, $"Zobacz https://example.com/{path} oraz kontakt@example.com");

    /// <summary>PNG (gradient) w pamięci.</summary>
    public static byte[] Png(int w = 120, int h = 80)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                px[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[o + 1] = 40;
                px[o + 2] = (byte)(y * 255 / Math.Max(1, h - 1));
            }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, px, w * 3);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Różne uszkodzenia: obcięty z śmieciami, sam nagłówek, losowe bajty z nagłówkiem, pusty, nie-PDF, obcięty w połowie xref.</summary>
    public static IReadOnlyList<(string Name, byte[] Data)> Broken()
    {
        var ok = SamplePdfFactory.Text(2);
        var rnd = new Random(1234);
        var garbage = new byte[4096];
        rnd.NextBytes(garbage);
        var withHeader = Encoding.ASCII.GetBytes("%PDF-1.7\n").Concat(garbage).ToArray();
        var tail = ok.AsSpan(0, ok.Length - 40).ToArray();
        // zniszczony słownik xref/trailer: zamiana "xref" na śmieci
        var noXref = (byte[])ok.Clone();
        var s = Encoding.Latin1.GetString(noXref);
        int ix = s.LastIndexOf("startxref", StringComparison.Ordinal);
        if (ix > 0) for (int i = ix; i < noXref.Length; i++) noXref[i] = (byte)'#';
        return new[]
        {
            ("obcięty ze śmieciami", SamplePdfFactory.Corrupted()),
            ("sam nagłówek", Encoding.ASCII.GetBytes("%PDF-1.7\n")),
            ("losowe bajty z nagłówkiem", withHeader),
            ("pusty", Array.Empty<byte>()),
            ("nie PDF", SamplePdfFactory.NotAPdf()),
            ("bez końcówki", tail),
            ("zniszczony startxref", noXref),
        };
    }
}

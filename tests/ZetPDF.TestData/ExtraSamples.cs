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


    /// <summary>
    /// PDF 1.5 jak z nowoczesnych programów: słowniki w strumieniu obiektów (/ObjStm), tabela xref jako strumień
    /// (/XRef), treść Flate. Tekst dosłowny (Helvetica): "Strumienie obiektow" i <see cref="SecretPhrase"/>.
    /// </summary>
    public static byte[] WithObjectStreams()
    {
        static byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            return ms.ToArray();
        }
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var header = new StringBuilder();
        var body = new StringBuilder();
        for (int i = 0; i < objs.Length; i++)
        {
            header.Append(i + 1).Append(' ').Append(body.Length).Append(' ');
            body.Append(objs[i]).Append('\n');
        }
        var objStmData = Encoding.ASCII.GetBytes(header.ToString() + body);
        int first = header.Length;
        var content = Encoding.ASCII.GetBytes($"BT /F1 16 Tf 56 760 Td (Strumienie obiektow) Tj 0 -30 Td ({SecretPhrase}) Tj ET\n");

        var ms = new MemoryStream();
        void W(string t) { var b = Encoding.ASCII.GetBytes(t); ms.Write(b); }
        void WB(byte[] b) => ms.Write(b);
        W("%PDF-1.5\n%âãÏÓ\n".Replace("âãÏÓ", "AAAA"));
        var offsets = new long[8];
        var c = Deflate(content);
        offsets[5] = ms.Position;
        W($"5 0 obj\n<< /Length {c.Length} /Filter /FlateDecode >>\nstream\n"); WB(c); W("\nendstream\nendobj\n");
        var o = Deflate(objStmData);
        offsets[6] = ms.Position;
        W($"6 0 obj\n<< /Type /ObjStm /N {objs.Length} /First {first} /Length {o.Length} /Filter /FlateDecode >>\nstream\n"); WB(o); W("\nendstream\nendobj\n");
        offsets[7] = ms.Position;
        // xref: /W [1 4 2]
        var x = new MemoryStream();
        void Entry(int type, long f2, int f3)
        {
            x.WriteByte((byte)type);
            x.WriteByte((byte)(f2 >> 24)); x.WriteByte((byte)(f2 >> 16)); x.WriteByte((byte)(f2 >> 8)); x.WriteByte((byte)f2);
            x.WriteByte((byte)(f3 >> 8)); x.WriteByte((byte)f3);
        }
        Entry(0, 0, 65535);
        for (int i = 0; i < 4; i++) Entry(2, 6, i);
        Entry(1, offsets[5], 0);
        Entry(1, offsets[6], 0);
        Entry(1, offsets[7], 0);
        var xd = x.ToArray();
        W($"7 0 obj\n<< /Type /XRef /Size 8 /W [1 4 2] /Index [0 8] /Root 1 0 R /Length {xd.Length} >>\nstream\n"); WB(xd); W("\nendstream\nendobj\n");
        W($"startxref\n{offsets[7]}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Plik z aktualizacją przyrostową: druga rewizja zmienia tytuł (/Info) na „Rewizja 2 – źdźbło”.</summary>
    public static byte[] WithIncrementalUpdate()
    {
        var baseBytes = SamplePdfFactory.Text(1);
        var text = Encoding.Latin1.GetString(baseBytes);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(text[(sx + 9)..].Trim().Split('\n', '\r')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        // największy numer obiektu i katalog z bieżącego pliku
        int size = PdfSharpEdit.Read(baseBytes, d => d.Internals.GetAllObjects().Max(o => o.Reference?.ObjectNumber ?? 0)) + 1;
        int root = PdfSharpEdit.Read(baseBytes, d => d.Internals.Catalog.Reference!.ObjectNumber);
        var ms = new MemoryStream();
        ms.Write(baseBytes);
        void W(string t) { var b = Encoding.Latin1.GetBytes(t); ms.Write(b); }
        W("\n");
        long infoOffset = ms.Position;
        // tytuł jako UTF-16BE z BOM (polskie znaki)
        var title = "Rewizja 2 – źdźbło";
        var hex = "FEFF" + string.Concat(Encoding.BigEndianUnicode.GetBytes(title).Select(b => b.ToString("X2")));
        W($"{size} 0 obj\n<< /Title <{hex}> /Producer (ZetPDF test) >>\nendobj\n");
        long xref = ms.Position;
        W($"xref\n0 1\n0000000000 65535 f \n{size} 1\n{infoOffset:D10} 00000 n \n");
        W($"trailer\n<< /Size {size + 1} /Root {root} 0 R /Info {size} 0 R /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

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

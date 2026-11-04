using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;

namespace ZetPDF.Editing.PdfA;

/// <summary>Pakiet XMP z pdfaid (część 2, poziom B) i podstawowymi właściwościami z /Info; bez własnych schematów.</summary>
internal static class PdfAMetadata
{
    public const string PdfaIdNamespace = "http://www.aiim.org/pdfa/ns/id/";

    public static byte[] Build(string title, string author, string subject, string keywords, string creator, string producer, DateTime created, DateTime modified)
    {
        // Pierwszy znak pakietu to znacznik kolejności bajtów, zapisany kodem, żeby był widoczny w źródle.
        var bom = ((char)0xFEFF).ToString();
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"").Append(bom).Append("\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"").Append(PdfaIdNamespace).Append("\">\n<pdfaid:part>2</pdfaid:part>\n<pdfaid:conformance>B</pdfaid:conformance>\n</rdf:Description>\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n<dc:format>application/pdf</dc:format>\n");
        if (title.Length > 0) sb.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Esc(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        if (author.Length > 0) sb.Append("<dc:creator><rdf:Seq><rdf:li>").Append(Esc(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        if (subject.Length > 0) sb.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Esc(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        sb.Append("</rdf:Description>\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        if (creator.Length > 0) sb.Append("<xmp:CreatorTool>").Append(Esc(creator)).Append("</xmp:CreatorTool>\n");
        sb.Append("<xmp:CreateDate>").Append(Date(created)).Append("</xmp:CreateDate>\n<xmp:ModifyDate>").Append(Date(modified)).Append("</xmp:ModifyDate>\n</rdf:Description>\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        if (keywords.Length > 0) sb.Append("<pdf:Keywords>").Append(Esc(keywords)).Append("</pdf:Keywords>\n");
        sb.Append("<pdf:Producer>").Append(Esc(producer)).Append("</pdf:Producer>\n</rdf:Description>\n");
        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static string Esc(string s) => SecurityElement.Escape(Conversion.XlsxWriter.CleanText(s)) ?? string.Empty;

    private static string Date(DateTime d) => (d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Local) : d).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// PDFsharp 6.2.4 przy każdym zapisie zastępuje /Metadata własnym pakietem (a jego tryb PDF/A oznacza plik jako 1A), więc pakietu
    /// nie da się ustawić przed zapisem. Dopisujemy go do gotowego pliku jako aktualizację przyrostową tego samego obiektu: wartości
    /// pochodzą z zapisanego /Info, więc XMP się z nim zgadza. Strumień bez /Filter, jak wymaga PDF/A.
    /// </summary>
    public static byte[] WriteToSavedFile(byte[] pdf)
    {
        var (number, xmp) = Core.Documents.PdfSharpEdit.Read(pdf, doc =>
        {
            if (doc.Internals.Catalog.Elements["/Metadata"] is not PdfSharp.Pdf.Advanced.PdfReference r) throw new InvalidOperationException("No metadata object.");
            var info = doc.Info;
            return (r.ObjectNumber, Build(info.Title ?? string.Empty, info.Author ?? string.Empty, info.Subject ?? string.Empty, info.Keywords ?? string.Empty, info.Creator ?? string.Empty, info.Producer ?? string.Empty, info.CreationDate, info.ModificationDate));
        });
        var text = Encoding.Latin1.GetString(pdf);
        int startx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var prev = Regex.Match(text[startx..], @"startxref\s+(\d+)");
        int trailerAt = text.LastIndexOf("trailer", startx, StringComparison.Ordinal);
        if (!prev.Success || trailerAt < 0) throw new InvalidOperationException("Cross-reference table not found.");
        var trailer = text[trailerAt..startx];
        string Entry(string pattern) => Regex.Match(trailer, pattern).Value;

        using var ms = new MemoryStream();
        ms.Write(pdf);
        void Ascii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        if (pdf[^1] != 10) Ascii("\n");
        long objectAt = ms.Position;
        Ascii(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n<</Type/Metadata/Subtype/XML/Length {xmp.Length}>>\nstream\n"));
        ms.Write(xmp);
        Ascii("\nendstream\nendobj\n");
        long xrefAt = ms.Position;
        Ascii(string.Create(CultureInfo.InvariantCulture, $"xref\n0 1\n0000000000 65535 f \n{number} 1\n{objectAt:D10} 00000 n \ntrailer\n<<{Entry(@"/Size\s+\d+")}{Entry(@"/Root\s+\d+\s+\d+\s+R")}{Entry(@"/Info\s+\d+\s+\d+\s+R")}{Entry(@"/ID\s*\[[^\]]*\]")}/Prev {prev.Groups[1].Value}>>\nstartxref\n{xrefAt}\n%%EOF\n"));
        return ms.ToArray();
    }
}

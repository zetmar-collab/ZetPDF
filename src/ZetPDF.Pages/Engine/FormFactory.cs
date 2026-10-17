using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ZetPDF.Pages.Engine;

/// <summary>
/// Tworzy obiekty XObject typu Form rysowane przez XGraphics PDFsharp (osadzanie czcionek TrueType z Unicode/ToUnicode –
/// polskie znaki wyodrębniają się jako tekst). PDFsharp 6.2 nie udostępnia publicznie obiektu PDF formularza XForm,
/// więc formularz jest raz rysowany na tymczasowej stronie, skąd odczytujemy referencję; strona jest usuwana
/// (jej treść nie trafia do pliku – PDFsharp przy zapisie pomija obiekty nieosiągalne).
/// </summary>
internal static class FormFactory
{
    public static XFont Font(string family, double size, bool bold = false, bool italic = false)
    {
        var style = (bold, italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };
        var opts = new XPdfFontOptions(PdfFontEncoding.Unicode);
        try { return new XFont(string.IsNullOrWhiteSpace(family) ? "Arial" : family, size, style, opts); }
        catch (Exception) { return new XFont("Arial", size, style, opts); }
    }

    /// <summary>Kontekst do mierzenia tekstu (bez dokumentu).</summary>
    public static XGraphics MeasureContext() => XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);

    /// <summary>Formularz w/h punktów; rysowanie w układzie XGraphics (początek lewy-górny, Y w dół).</summary>
    public static PdfDictionary Create(PdfDocument doc, double width, double height, Action<XGraphics> draw)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var form = new XForm(doc, new XRect(0, 0, width, height));
        using (var g = XGraphics.FromForm(form)) draw(g);
        form.DrawingFinished();
        var temp = doc.AddPage();
        try
        {
            using (var g = XGraphics.FromPdfPage(temp)) g.DrawImage(form, 0, 0, width, height);
            var xobjects = PdfIo.GetDict(temp.Resources, "/XObject") ?? throw new InvalidOperationException("Form XObject was not created.");
            foreach (var key in xobjects.Elements.Keys)
                if (xobjects.Elements[key] is PdfReference r && r.Value is PdfDictionary d) return d;
            throw new InvalidOperationException("Form XObject was not created.");
        }
        finally
        {
            doc.Pages.Remove(temp);
        }
    }

    /// <summary>Formularz z ręcznie zbudowaną treścią i zasobami.</summary>
    public static PdfDictionary CreateRaw(PdfDocument doc, double width, double height, string content, PdfDictionary? resources)
    {
        var d = new PdfDictionary(doc);
        d.Elements["/Type"] = new PdfName("/XObject");
        d.Elements["/Subtype"] = new PdfName("/Form");
        d.Elements["/BBox"] = PdfIo.NumberArray(null, 0, 0, width, height);
        d.Elements["/Resources"] = resources ?? new PdfDictionary();
        d.CreateStream(PdfIo.Latin1(content));
        doc.Internals.AddObject(d);
        return d;
    }

    /// <summary>Obraz z pliku jako XObject /Image (PDFsharp koduje JPEG/PNG z przezroczystością).</summary>
    public static (PdfDictionary Image, double WidthPt, double HeightPt) Image(PdfDocument doc, string path)
    {
        var x = XImage.FromFile(path);
        var img = new PdfImage(doc, x);
        if (img.Reference is null) doc.Internals.AddObject(img);
        double w = x.PointWidth > 0 ? x.PointWidth : x.PixelWidth;
        double h = x.PointHeight > 0 ? x.PointHeight : x.PixelHeight;
        return (img, w, h);
    }

    /// <summary>ExtGState z kryciem (wypełnienie i obrys).</summary>
    public static PdfDictionary Opacity(PdfDocument doc, double opacity)
    {
        var gs = new PdfDictionary(doc);
        gs.Elements["/Type"] = new PdfName("/ExtGState");
        gs.Elements["/ca"] = new PdfReal(Math.Round(Math.Clamp(opacity, 0, 1), 3));
        gs.Elements["/CA"] = new PdfReal(Math.Round(Math.Clamp(opacity, 0, 1), 3));
        doc.Internals.AddObject(gs);
        return gs;
    }
}

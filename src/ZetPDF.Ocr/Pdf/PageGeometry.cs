using System.Windows;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Ocr.Pdf;

/// <summary>Geometria strony PDFsharp zgodna z <see cref="PageInfo"/> z PDFium (CropBox, /Rotate).</summary>
public static class PageGeometry
{
    /// <summary>CropBox (lub MediaBox) w przestrzeni użytkownika strony.</summary>
    public static PdfRect CropBox(PdfPage page)
    {
        var r = ReadBox(page, "/CropBox") ?? ReadBox(page, "/MediaBox") ?? new PdfRect(0, 0, 595.276, 841.89);
        var m = ReadBox(page, "/MediaBox");
        if (m is { } media)
        {
            // CropBox jest przycinany do MediaBox (jak w PDFium).
            var c = new PdfRect(Math.Max(r.Left, media.Left), Math.Max(r.Bottom, media.Bottom), Math.Min(r.Right, media.Right), Math.Min(r.Top, media.Top));
            if (!c.IsEmpty) r = c;
        }
        return r;
    }

    /// <summary>Obrót strony 0/90/180/270.</summary>
    public static int Rotation(PdfPage page)
    {
        var rot = page.Elements.GetInteger("/Rotate");
        rot %= 360;
        if (rot < 0) rot += 360;
        return rot / 90 * 90;
    }

    /// <summary>PageTransform dla strony w skali 1 (punkty widoku = punkty strony po obrocie).</summary>
    public static PageTransform Transform(PdfPage page, double scale = 1)
    {
        var box = CropBox(page);
        return new PageTransform(new PdfSize(box.Width, box.Height), new PdfPoint(box.Left, box.Bottom), Rotation(page), scale, new Point(0, 0));
    }

    private static PdfRect? ReadBox(PdfPage page, string key)
    {
        var item = page.Elements[key];
        if (item is PdfSharp.Pdf.Advanced.PdfReference reference) item = reference.Value;
        if (item is not PdfArray a || a.Elements.Count < 4) return null;
        double v(int i) => a.Elements[i] switch
        {
            PdfInteger n => n.Value,
            PdfReal d => d.Value,
            PdfLongInteger l => l.Value,
            _ => 0,
        };
        double x1 = v(0), y1 = v(1), x2 = v(2), y2 = v(3);
        return new PdfRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}

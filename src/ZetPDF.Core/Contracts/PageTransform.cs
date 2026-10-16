using System.Windows;

namespace ZetPDF.Core.Contracts;

/// <summary>
/// Przeliczanie współrzędnych strona PDF ↔ widok WPF (DIP) dla jednej strony w widoku.
/// Wzorcowa implementacja dla wszystkich modułów (renderer-viewer musi być z nią zgodny – test
/// porównujący z FPDF_PageToDevice w ZetPDF.Viewer.Tests).
///
/// Przestrzeń strony: punkty, Y w górę, początek = lewy dolny róg przestrzeni użytkownika
/// (CropOrigin to lewy dolny róg CropBox). Widok: DIP, Y w dół, ViewOrigin = lewy górny róg
/// narysowanej strony. Obrót łączny = (/Rotate strony + obrót widoku) mod 360, zgodnie z ruchem wskazówek zegara.
/// Scale = DIP na punkt. WPF DIP = 1/96 cala, punkt PDF = 1/72 cala, więc przy zoomie 100% (rozmiar fizyczny)
/// Scale = 96/72 ≈ 1.333; ogólnie Scale = zoom · 96 / 72. Piksele bitmapy = DIP · (DPI ekranu / 96).
/// </summary>
public sealed class PageTransform
{
    public PageTransform(PdfSize pageSize, PdfPoint cropOrigin, int totalRotation, double scale, Point viewOrigin)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        PageSize = pageSize;
        CropOrigin = cropOrigin;
        Rotation = ((totalRotation % 360) + 360) % 360;
        if (Rotation % 90 != 0) throw new ArgumentOutOfRangeException(nameof(totalRotation), "Only multiples of 90° are supported.");
        Scale = scale;
        ViewOrigin = viewOrigin;
    }

    /// <summary>Rozmiar CropBox bez obrotu (punkty).</summary>
    public PdfSize PageSize { get; }
    public PdfPoint CropOrigin { get; }
    /// <summary>Obrót łączny 0/90/180/270 (zgodnie z ruchem wskazówek zegara).</summary>
    public int Rotation { get; }
    /// <summary>DIP na punkt.</summary>
    public double Scale { get; }
    /// <summary>Lewy górny róg narysowanej strony w układzie widoku.</summary>
    public Point ViewOrigin { get; }

    /// <summary>Rozmiar narysowanej strony w DIP.</summary>
    public Size ViewSize => Rotation % 180 == 0
        ? new Size(PageSize.Width * Scale, PageSize.Height * Scale)
        : new Size(PageSize.Height * Scale, PageSize.Width * Scale);

    /// <summary>Prostokąt narysowanej strony w układzie widoku.</summary>
    public Rect ViewBounds => new(ViewOrigin, ViewSize);

    public Point ToView(PdfPoint p)
    {
        double w = PageSize.Width, h = PageSize.Height;
        double u = p.X - CropOrigin.X;          // od lewej krawędzi, punkty
        double v = h - (p.Y - CropOrigin.Y);    // od górnej krawędzi, punkty
        double x, y;
        switch (Rotation)
        {
            case 0: x = u; y = v; break;
            case 90: x = h - v; y = u; break;
            case 180: x = w - u; y = h - v; break;
            default: x = v; y = w - u; break; // 270
        }
        return new Point(ViewOrigin.X + x * Scale, ViewOrigin.Y + y * Scale);
    }

    public PdfPoint ToPage(Point viewPoint)
    {
        double w = PageSize.Width, h = PageSize.Height;
        double x = (viewPoint.X - ViewOrigin.X) / Scale;
        double y = (viewPoint.Y - ViewOrigin.Y) / Scale;
        double u, v;
        switch (Rotation)
        {
            case 0: u = x; v = y; break;
            case 90: u = y; v = h - x; break;
            case 180: u = w - x; v = h - y; break;
            default: u = w - y; v = x; break; // 270
        }
        return new PdfPoint(u + CropOrigin.X, h - v + CropOrigin.Y);
    }

    /// <summary>Prostokąt strony → prostokąt widoku (osiowo wyrównany).</summary>
    public Rect ToView(PdfRect r)
    {
        var a = ToView(new PdfPoint(r.Left, r.Bottom));
        var b = ToView(new PdfPoint(r.Right, r.Top));
        return new Rect(a, b);
    }

    /// <summary>Prostokąt widoku → prostokąt strony.</summary>
    public PdfRect ToPage(Rect r) => PdfRect.FromPoints(ToPage(r.TopLeft), ToPage(r.BottomRight));

    /// <summary>Długość w punktach → DIP (grubości linii itp.).</summary>
    public double ToViewLength(double points) => points * Scale;
    public double ToPageLength(double dip) => dip / Scale;
}

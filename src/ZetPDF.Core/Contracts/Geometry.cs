namespace ZetPDF.Core.Contracts;

// Układ współrzędnych PDF: punkty (1/72 cala), początek w lewym DOLNYM rogu strony (przestrzeń użytkownika
// strony, przed uwzględnieniem /Rotate), oś Y w górę. Wszystkie kontrakty używają tych jednostek,
// chyba że opis mówi inaczej (np. piksele bitmapy, współrzędne widoku WPF).

/// <summary>Punkt w przestrzeni strony PDF (punkty, Y w górę).</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>Prostokąt w przestrzeni strony PDF (punkty, Y w górę). Left ≤ Right, Bottom ≤ Top.</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Tworzy prostokąt z dwóch dowolnych narożników (normalizuje kolejność).</summary>
    public static PdfRect FromPoints(PdfPoint a, PdfPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(PdfPoint p) => p.X >= Left && p.X <= Right && p.Y >= Bottom && p.Y <= Top;

    public bool IntersectsWith(PdfRect o) => o.Left < Right && o.Right > Left && o.Bottom < Top && o.Top > Bottom;

    public PdfRect Union(PdfRect o) =>
        new(Math.Min(Left, o.Left), Math.Min(Bottom, o.Bottom), Math.Max(Right, o.Right), Math.Max(Top, o.Top));

    public PdfRect Inflate(double d) => new(Left - d, Bottom - d, Right + d, Top + d);
}

/// <summary>
/// Czworokąt (np. QuadPoints adnotacji adiustacji lub obrys obróconego znaku).
/// Kolejność jak w PDF QuadPoints w praktyce czytników: lewy-górny, prawy-górny, lewy-dolny, prawy-dolny.
/// </summary>
public readonly record struct PdfQuad(PdfPoint UpperLeft, PdfPoint UpperRight, PdfPoint LowerLeft, PdfPoint LowerRight)
{
    public static PdfQuad FromRect(PdfRect r) =>
        new(new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom));

    public PdfRect Bounds => new(
        Math.Min(Math.Min(UpperLeft.X, UpperRight.X), Math.Min(LowerLeft.X, LowerRight.X)),
        Math.Min(Math.Min(UpperLeft.Y, UpperRight.Y), Math.Min(LowerLeft.Y, LowerRight.Y)),
        Math.Max(Math.Max(UpperLeft.X, UpperRight.X), Math.Max(LowerLeft.X, LowerRight.X)),
        Math.Max(Math.Max(UpperLeft.Y, UpperRight.Y), Math.Max(LowerLeft.Y, LowerRight.Y)));
}

/// <summary>Macierz przekształcenia PDF [a b c d e f] (x' = a·x + c·y + e, y' = b·x + d·y + f).</summary>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);

    public PdfPoint Transform(PdfPoint p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>Złożenie: najpierw this, potem <paramref name="next"/>.</summary>
    public PdfMatrix Then(PdfMatrix next) => new(
        A * next.A + B * next.C, A * next.B + B * next.D,
        C * next.A + D * next.C, C * next.B + D * next.D,
        E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);

    public static PdfMatrix Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);
    public static PdfMatrix Scaling(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static PdfMatrix Rotation(double degreesCounterClockwise)
    {
        var r = degreesCounterClockwise * Math.PI / 180.0;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        return new(cos, sin, -sin, cos, 0, 0);
    }
}

/// <summary>Rozmiar strony w punktach.</summary>
public readonly record struct PdfSize(double Width, double Height)
{
    public static PdfSize A4 => new(595.276, 841.89);
    public static PdfSize Letter => new(612, 792);
    public static PdfSize A3 => new(841.89, 1190.551);
    public static PdfSize A5 => new(419.528, 595.276);
    public static PdfSize Legal => new(612, 1008);
}

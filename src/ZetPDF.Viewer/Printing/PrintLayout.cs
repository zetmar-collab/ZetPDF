using System.Windows;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.Printing;

/// <summary>Umieszczenie jednej strony na arkuszu: prostokąt docelowy (DIP arkusza) i czy obrócić o 90°.</summary>
public readonly record struct CellPlacement(int PageIndex, Rect Destination, Rect Cell, bool Rotate90);

/// <summary>
/// Czysta matematyka druku (testy jednostkowe): podział stron na arkusze (N stron na arkuszu: 1, 2, 4, 6, 9),
/// siatka komórek, skalowanie (dopasuj / rzeczywisty rozmiar / własny %) i automatyczna orientacja
/// (obrót strony o 90°, gdy lepiej wypełnia komórkę). Jednostki arkusza: DIP (1/96 cala); strony: punkty (1/72 cala).
/// </summary>
public static class PrintLayout
{
    public const double DipPerPoint = 96.0 / 72.0;

    public static readonly int[] SupportedPagesPerSheet = { 1, 2, 4, 6, 9 };

    public static IReadOnlyList<IReadOnlyList<int>> Sheets(IReadOnlyList<int> pages, int perSheet)
    {
        perSheet = Normalize(perSheet);
        var sheets = new List<IReadOnlyList<int>>();
        for (int i = 0; i < pages.Count; i += perSheet) sheets.Add(pages.Skip(i).Take(perSheet).ToArray());
        return sheets;
    }

    /// <summary>Strony arkuszy dla opcji druku; w broszurze kolejność składki, <see cref="BookletImposition.Blank"/> to pusta komórka.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> SheetsFor(IReadOnlyList<int> pages, PrintOptions options) =>
        options.Booklet ? BookletImposition.Map(pages) : Sheets(pages, options.PagesPerSheet);

    /// <summary>Papier poziomy dla broszury.</summary>
    public static (Size Paper, Rect Printable) ForBooklet(Size paper, Rect printable) =>
        paper.Width >= paper.Height ? (paper, printable) : (new Size(paper.Height, paper.Width), new Rect(printable.Y, printable.X, printable.Height, printable.Width));

    public static int Normalize(int perSheet) => SupportedPagesPerSheet.Contains(perSheet) ? perSheet : 1;

    /// <summary>Siatka (kolumny, wiersze) dla N stron na arkuszu – dopasowana do orientacji obszaru druku.</summary>
    public static (int Cols, int Rows) Grid(int perSheet, Size printable)
    {
        bool landscape = printable.Width > printable.Height;
        return Normalize(perSheet) switch
        {
            2 => landscape ? (2, 1) : (1, 2),
            4 => (2, 2),
            6 => landscape ? (3, 2) : (2, 3),
            9 => (3, 3),
            _ => (1, 1),
        };
    }

    /// <summary>Komórki arkusza (wiersz po wierszu, od lewej) w obszarze druku.</summary>
    public static IReadOnlyList<Rect> Cells(int perSheet, Rect printable, double gap = 8)
    {
        var (cols, rows) = Grid(perSheet, printable.Size);
        if (cols * rows == 1) return new[] { printable };
        double w = (printable.Width - (cols - 1) * gap) / cols, h = (printable.Height - (rows - 1) * gap) / rows;
        var cells = new List<Rect>(cols * rows);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                cells.Add(new Rect(printable.X + c * (w + gap), printable.Y + r * (h + gap), Math.Max(1, w), Math.Max(1, h)));
        return cells;
    }

    /// <summary>
    /// Prostokąt strony w komórce. N &gt; 1 zawsze dopasowuje. Rzeczywisty rozmiar / własny % – wyśrodkowane
    /// (część wychodząca poza komórkę zostanie przycięta).
    /// </summary>
    public static CellPlacement Place(int pageIndex, Size pagePoints, Rect cell, PrintScaling scaling, double customPercent, bool autoRotate, bool forceFit)
    {
        bool rotate = false;
        if (autoRotate)
        {
            bool pageLandscape = pagePoints.Width > pagePoints.Height * 1.02;
            bool cellLandscape = cell.Width > cell.Height * 1.02;
            bool pagePortrait = pagePoints.Height > pagePoints.Width * 1.02;
            bool cellPortrait = cell.Height > cell.Width * 1.02;
            rotate = (pageLandscape && cellPortrait) || (pagePortrait && cellLandscape);
        }
        double pw = (rotate ? pagePoints.Height : pagePoints.Width) * DipPerPoint;
        double ph = (rotate ? pagePoints.Width : pagePoints.Height) * DipPerPoint;
        double scale = (forceFit || scaling == PrintScaling.FitToPaper)
            ? Math.Min(cell.Width / pw, cell.Height / ph)
            : scaling == PrintScaling.Custom ? Math.Clamp(customPercent, 1, 1000) / 100.0 : 1.0;
        double w = pw * scale, h = ph * scale;
        var dest = new Rect(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h);
        return new CellPlacement(pageIndex, dest, cell, rotate);
    }

    /// <summary>Rozmieszczenie stron jednego arkusza.</summary>
    public static IReadOnlyList<CellPlacement> LayoutSheet(IReadOnlyList<int> sheetPages, Func<int, Size> pageSizePoints, Rect printable, PrintOptions options)
    {
        int n = options.Booklet ? 2 : Normalize(options.PagesPerSheet);
        var cells = Cells(n, printable);
        bool autoRotate = options.AutoOrientation && !options.Booklet;
        var result = new List<CellPlacement>(sheetPages.Count);
        for (int i = 0; i < sheetPages.Count && i < cells.Count; i++)
            if (sheetPages[i] != BookletImposition.Blank)
                result.Add(Place(sheetPages[i], pageSizePoints(sheetPages[i]), cells[i], options.Scaling, options.CustomScalePercent, autoRotate, forceFit: n > 1));
        return result;
    }

    /// <summary>Rozdzielczość renderu dla komórki (piksele na punkt), ograniczona do maxDpi.</summary>
    public static double PixelScale(CellPlacement placement, Size pagePoints, double printerDpi, double maxDpi = 300)
    {
        double dpi = Math.Min(printerDpi <= 0 ? 300 : printerDpi, maxDpi);
        double widthPts = placement.Rotate90 ? pagePoints.Height : pagePoints.Width;
        double destInches = placement.Destination.Width / 96.0;
        return Math.Max(0.05, destInches * dpi / widthPts);
    }
}

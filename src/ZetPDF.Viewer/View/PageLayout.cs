using System.Windows;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.View;

/// <summary>
/// Czysta matematyka układu stron (bez WPF-owych kontrolek – testowana jednostkowo).
/// Współrzędne „treści” w DIP: (0,0) = lewy górny róg całego przewijanego obszaru.
/// Skala DIP na punkt = zoom · 96 / 72 (zgodnie z <see cref="PageTransform"/>).
/// </summary>
public sealed class PageLayout
{
    public const double Margin = 12;
    public const double Gap = 10;
    public const double DipPerPointAt100 = 96.0 / 72.0;

    private readonly Rect[] _rects;

    private PageLayout(Rect[] rects, IReadOnlyList<int> placed, Size extent, double scale)
    {
        _rects = rects;
        PlacedPages = placed;
        Extent = extent;
        Scale = scale;
    }

    /// <summary>Strony obecne w układzie (w trybach „jedna strona”/„dwie strony” – tylko bieżąca rozkładówka).</summary>
    public IReadOnlyList<int> PlacedPages { get; }

    /// <summary>Rozmiar całej treści (DIP).</summary>
    public Size Extent { get; }

    /// <summary>DIP na punkt.</summary>
    public double Scale { get; }

    /// <summary>Prostokąt strony w układzie treści lub <see cref="Rect.Empty"/>, gdy strona nie jest umieszczona.</summary>
    public Rect GetPageRect(int pageIndex) => (uint)pageIndex < (uint)_rects.Length ? _rects[pageIndex] : Rect.Empty;

    public bool IsPlaced(int pageIndex) => !GetPageRect(pageIndex).IsEmpty;

    /// <summary>Rozmiar wyświetlany strony w punktach (po /Rotate i obrocie widoku).</summary>
    public static Size DisplaySizePoints(PageInfo page, int viewRotation)
    {
        int total = ((page.Rotation + viewRotation) % 360 + 360) % 360;
        return total % 180 == 0 ? new Size(page.Size.Width, page.Size.Height) : new Size(page.Size.Height, page.Size.Width);
    }

    public static bool IsTwoUp(PageLayoutMode mode) => mode is PageLayoutMode.TwoPages or PageLayoutMode.TwoPagesContinuous;
    public static bool IsContinuous(PageLayoutMode mode) => mode is PageLayoutMode.Continuous or PageLayoutMode.TwoPagesContinuous;

    /// <summary>Pierwsza strona rozkładówki zawierającej daną stronę (pary 0-1, 2-3, …).</summary>
    public static int SpreadStart(int pageIndex, PageLayoutMode mode) => IsTwoUp(mode) ? pageIndex - pageIndex % 2 : pageIndex;

    /// <summary>Buduje układ. Strony węższe od wiersza są centrowane; cały układ centrowany, gdy węższy od okna.</summary>
    public static PageLayout Build(IReadOnlyList<PageInfo> pages, PageLayoutMode mode, int viewRotation, double zoom, int currentPage, Size viewport)
    {
        double scale = zoom * DipPerPointAt100;
        var rects = new Rect[pages.Count];
        for (int i = 0; i < rects.Length; i++) rects[i] = Rect.Empty;
        var placed = new List<int>();
        if (pages.Count == 0) return new PageLayout(rects, placed, new Size(0, 0), scale);

        currentPage = Math.Clamp(currentPage, 0, pages.Count - 1);
        int perRow = IsTwoUp(mode) ? 2 : 1;
        int first, last;
        if (IsContinuous(mode)) { first = 0; last = pages.Count - 1; }
        else { first = SpreadStart(currentPage, mode); last = Math.Min(pages.Count - 1, first + perRow - 1); }

        // wiersze
        var rows = new List<(int Start, int Count, double Width, double Height)>();
        for (int i = first; i <= last; i += perRow)
        {
            int count = Math.Min(perRow, last - i + 1);
            double w = 0, h = 0;
            for (int k = 0; k < count; k++)
            {
                var s = DisplaySizePoints(pages[i + k], viewRotation);
                w += s.Width * scale;
                h = Math.Max(h, s.Height * scale);
            }
            w += (count - 1) * Gap;
            rows.Add((i, count, w, h));
        }
        double maxRow = rows.Max(r => r.Width);
        double contentWidth = Math.Max(maxRow + 2 * Margin, viewport.Width);
        double y = Margin;
        foreach (var row in rows)
        {
            double x = (contentWidth - row.Width) / 2;
            for (int k = 0; k < row.Count; k++)
            {
                int idx = row.Start + k;
                var s = DisplaySizePoints(pages[idx], viewRotation);
                double w = s.Width * scale, h = s.Height * scale;
                rects[idx] = new Rect(x, y + (row.Height - h) / 2, w, h);
                placed.Add(idx);
                x += w + Gap;
            }
            y += row.Height + Gap;
        }
        double contentHeight = y - Gap + Margin;
        if (!IsContinuous(mode)) contentHeight = Math.Max(contentHeight, viewport.Height);
        return new PageLayout(rects, placed, new Size(contentWidth, contentHeight), scale);
    }

    /// <summary>Powiększenie dla trybów dopasowania (dla Custom zwraca <paramref name="currentZoom"/>).</summary>
    public static double ComputeZoom(ZoomMode zoomMode, IReadOnlyList<PageInfo> pages, PageLayoutMode mode, int viewRotation, int currentPage, Size viewport, double currentZoom)
    {
        if (pages.Count == 0 || viewport.Width <= 0 || viewport.Height <= 0) return currentZoom;
        switch (zoomMode)
        {
            case ZoomMode.ActualSize: return 1.0;
            case ZoomMode.Custom: return currentZoom;
        }
        int perRow = IsTwoUp(mode) ? 2 : 1;
        currentPage = Math.Clamp(currentPage, 0, pages.Count - 1);

        // szerokość/wysokość wiersza (punkty) – dla dopasowania szerokości bierzemy najszerszy wiersz dokumentu
        double RowWidth(int start) { double w = 0; for (int k = 0; k < perRow && start + k < pages.Count; k++) w += DisplaySizePoints(pages[start + k], viewRotation).Width; return w; }
        double RowHeight(int start) { double h = 0; for (int k = 0; k < perRow && start + k < pages.Count; k++) h = Math.Max(h, DisplaySizePoints(pages[start + k], viewRotation).Height); return h; }
        int RowCount(int start) => Math.Min(perRow, pages.Count - start);

        double maxRowWidth = 0;
        int widestStart = 0;
        for (int i = 0; i < pages.Count; i += perRow)
        {
            double w = RowWidth(i);
            if (w > maxRowWidth) { maxRowWidth = w; widestStart = i; }
        }
        double margin = zoomMode == ZoomMode.FitVisible ? 2 : Margin;
        double scrollbar = IsContinuous(mode) ? 18 : 0; // pionowy pasek przewijania pojawia się w trybach ciągłych
        double availW = Math.Max(40, viewport.Width - 2 * margin - (RowCount(widestStart) - 1) * Gap - scrollbar);
        double fitWidth = availW / (maxRowWidth * DipPerPointAt100);
        if (zoomMode is ZoomMode.FitWidth or ZoomMode.FitVisible) return Clamp(fitWidth);

        // FitPage – cała bieżąca rozkładówka mieści się w oknie
        int spread = SpreadStart(currentPage, mode);
        double rowW = RowWidth(spread), rowH = RowHeight(spread);
        double fw = Math.Max(40, viewport.Width - 2 * Margin - (RowCount(spread) - 1) * Gap) / (rowW * DipPerPointAt100);
        double fh = Math.Max(40, viewport.Height - 2 * Margin) / (rowH * DipPerPointAt100);
        return Clamp(Math.Min(fw, fh));
    }

    public const double MinZoom = 0.05;
    public const double MaxZoom = 64.0;

    /// <summary>„125%”, „125”, „1,25” → 1.25 (procenty; okno Powiększenie do).</summary>
    public static bool TryParsePercent(string? text, out double zoom)
    {
        zoom = 0;
        var t = (text ?? string.Empty).Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v <= 0) return false;
        zoom = Clamp(v / 100.0);
        return true;
    }

    public static double Clamp(double zoom) => double.IsNaN(zoom) ? 1 : Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>Standardowe stopnie powiększenia (Ctrl+/Ctrl−, lista pola procentowego).</summary>
    public static IReadOnlyList<double> ZoomSteps { get; } = new[] { 0.0833, 0.125, 0.25, 0.3333, 0.5, 0.6667, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0, 24.0, 32.0, 64.0 };

    public static double NextZoomStep(double zoom, bool up)
    {
        if (up)
        {
            foreach (var s in ZoomSteps) if (s > zoom * 1.001) return s;
            return MaxZoom;
        }
        for (int i = ZoomSteps.Count - 1; i >= 0; i--) if (ZoomSteps[i] < zoom * 0.999) return ZoomSteps[i];
        return MinZoom;
    }
}

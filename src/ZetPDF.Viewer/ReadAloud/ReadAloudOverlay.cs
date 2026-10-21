using System.Windows;
using System.Windows.Media;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.ReadAloud;

/// <summary>Nakładka podświetlająca aktualnie czytane słowo (pędzel motywu „bieżące trafienie wyszukiwania”).</summary>
internal sealed class ReadAloudOverlay : IPageOverlay
{
    private readonly ReadAloudService _service;

    public ReadAloudOverlay(ReadAloudService service) => _service = service;

    public int ZIndex => 850;
    public bool IsVisible => _service.Highlight is not null;

    public void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        if (_service.Highlight is not { } h || h.Page != pageIndex) return;
        var app = Application.Current;
        var fill = app?.TryFindResource(ThemeKeys.ViewerSearchHitCurrent) as Brush;
        var outline = app?.TryFindResource(ThemeKeys.ViewerOverlayOutline) as Brush;
        if (fill is null && outline is null) return;
        var pen = outline is null ? null : new Pen(outline, 1);
        foreach (var r in h.Rects) dc.DrawRectangle(fill, pen, transform.ToView(r.Inflate(1)));
    }
}

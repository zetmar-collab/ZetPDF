using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Security.UI;

/// <summary>
/// Narzędzie „Podpisz cyfrowo”: przeciągnięcie rysuje prostokąt widocznego podpisu, kliknięcie w puste pole
/// podpisu wybiera to pole. Esc anuluje. Po wyborze woła <see cref="Placed"/> i wraca do narzędzia domyślnego.
/// </summary>
internal sealed class SignatureTool : IViewerTool, IPageOverlay
{
    private const double MinSize = 12;
    private readonly IToolService _tools;
    private int _page = -1;
    private PdfPoint _a, _b;
    private bool _pressed;

    public SignatureTool(IToolService tools) => _tools = tools;

    /// <summary>Puste pola podpisu bieżącego dokumentu (podświetlane i klikalne).</summary>
    public IReadOnlyList<(string Name, int Page, PdfRect Rect)> EmptyFields { get; set; } = Array.Empty<(string, int, PdfRect)>();

    /// <summary>(strona, prostokąt, nazwa pola lub null).</summary>
    public event Action<int, PdfRect, string?>? Placed;

    public string Id => CommandIds.Sign.PlaceSignature;
    public Cursor Cursor => Cursors.Cross;
    public IPageOverlay? Overlay => this;
    public int ZIndex => 900;
    public bool IsVisible => true;

    public void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        var outline = Application.Current?.TryFindResource(ThemeKeys.ViewerOverlayOutline) as Brush;
        var fieldFill = Application.Current?.TryFindResource(ThemeKeys.ViewerFormFieldHighlight) as Brush;
        var selection = Application.Current?.TryFindResource(ThemeKeys.ViewerTextSelection) as Brush;
        var pen = outline is null ? null : new Pen(outline, 1) { DashStyle = DashStyles.Dash };
        foreach (var f in EmptyFields)
            if (f.Page == pageIndex) dc.DrawRectangle(fieldFill, pen, transform.ToView(f.Rect));
        if (_pressed && pageIndex == _page) dc.DrawRectangle(selection, pen, transform.ToView(PdfRect.FromPoints(_a, _b)));
    }

    public void OnActivated(IDocumentView view) => view.InvalidateOverlays();

    public void OnDeactivated(IDocumentView view)
    {
        _pressed = false;
        view.InvalidateOverlays();
    }

    public bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _page = e.PageIndex;
        _a = _b = e.PagePoint;
        _pressed = true;
        view.InvalidateOverlays();
        return true;
    }

    public bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _b = Clamp(view, e);
        view.InvalidateOverlays();
        return true;
    }

    public bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _pressed = false;
        _b = Clamp(view, e);
        var rect = PdfRect.FromPoints(_a, _b);
        view.InvalidateOverlays();
        if (rect.Width < MinSize || rect.Height < MinSize)
        {
            var hit = EmptyFields.FirstOrDefault(f => f.Page == _page && f.Rect.Contains(_a));
            if (hit.Name is null) return true; // za mały prostokąt – czekamy dalej
            Finish(view, hit.Page, hit.Rect, hit.Name);
            return true;
        }
        Finish(view, _page, rect, null);
        return true;
    }

    private void Finish(IDocumentView view, int page, PdfRect rect, string? field)
    {
        _tools.ActivateDefault();
        // po zakończeniu obsługi zdarzenia myszy (okno modalne nie może przejąć przechwycenia myszy w trakcie)
        view.Element.Dispatcher.BeginInvoke(() => Placed?.Invoke(page, rect, field));
    }

    public bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return false;
        _pressed = false;
        _tools.ActivateDefault();
        return true;
    }

    private PdfPoint Clamp(IDocumentView view, PagePointerEventArgs e)
    {
        var t = view.GetPageTransform(_page);
        var p = e.PageIndex == _page || t is null ? e.PagePoint : t.ToPage(e.ViewPoint);
        if (t is null) return p;
        double x = Math.Clamp(p.X, t.CropOrigin.X, t.CropOrigin.X + t.PageSize.Width);
        double y = Math.Clamp(p.Y, t.CropOrigin.Y, t.CropOrigin.Y + t.PageSize.Height);
        return new PdfPoint(x, y);
    }
}

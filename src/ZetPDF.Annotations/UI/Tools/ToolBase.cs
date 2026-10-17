using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Tools;

/// <summary>Nakładka rysowana delegatem (podgląd tworzonego komentarza, zaznaczenie, wyróżnienie pól).</summary>
internal sealed class DelegateOverlay : IPageOverlay
{
    private readonly Action<DrawingContext, int, PageTransform> _render;
    private readonly Func<bool> _visible;

    public DelegateOverlay(int zIndex, Action<DrawingContext, int, PageTransform> render, Func<bool>? visible = null)
    {
        ZIndex = zIndex;
        _render = render;
        _visible = visible ?? (() => true);
    }

    public int ZIndex { get; }
    public bool IsVisible => _visible();
    public void Render(DrawingContext dc, int pageIndex, PageTransform transform) => _render(dc, pageIndex, transform);
}

/// <summary>Wspólna baza narzędzi komentarzy: stan wciśnięcia, nakładka podglądu, styl z ustawień.</summary>
internal abstract class ToolBase : IViewerTool
{
    protected ToolBase(AnnotContext ctx, string id)
    {
        Ctx = ctx;
        Id = id;
        Overlay = new DelegateOverlay(900, RenderPreview);
    }

    protected AnnotContext Ctx { get; }
    public string Id { get; }
    public virtual Cursor Cursor => Cursors.Cross;
    public IPageOverlay? Overlay { get; }

    protected ToolStyle Style => Ctx.Settings.StyleFor(Id);

    public virtual void OnActivated(IDocumentView view) => view.InvalidateOverlays();
    public virtual void OnDeactivated(IDocumentView view)
    {
        Cancel();
        view.InvalidateOverlays();
    }

    public abstract bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e);
    public virtual bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e) => false;
    public virtual bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e) => false;

    public virtual bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            view.InvalidateOverlays();
            if (Ctx.Tools is { } t) t.ActivateDefault();
            return true;
        }
        return false;
    }

    protected virtual void Cancel() { }
    protected virtual void RenderPreview(DrawingContext dc, int page, PageTransform t) { }

    protected static Pen OutlinePen(double thickness = 1)
    {
        var p = new Pen(Ui.Resource(ThemeKeys.ViewerOverlayOutline) ?? System.Windows.SystemColors.HighlightBrush, thickness) { DashStyle = DashStyles.Dash };
        p.Freeze();
        return p;
    }

    protected Pen ContentPen(double widthDip) =>
        new(Ui.ContentBrush(Style.Color, Math.Max(0.4, Style.Opacity)), Math.Max(1, widthDip)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    protected static IPdfDocumentSession? SessionOf(IDocumentView view) => view.Session;
}

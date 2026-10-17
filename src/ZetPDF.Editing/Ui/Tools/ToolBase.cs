using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Editing.Ui.Tools;

/// <summary>Wspólna baza narzędzi modułu: nakładka narzędzia (ZIndex 900), rysowanie pędzlami motywu.</summary>
internal abstract class ToolBase : IViewerTool, IPageOverlay
{
    protected ToolBase(EditingContext ctx, string id)
    {
        Ctx = ctx;
        Id = id;
    }

    protected EditingContext Ctx { get; }
    public string Id { get; }
    public virtual Cursor Cursor => Cursors.Cross;
    public IPageOverlay? Overlay => this;
    public virtual int ZIndex => 900;
    public virtual bool IsVisible => true;

    public virtual void OnActivated(IDocumentView view) => view.InvalidateOverlays();
    public virtual void OnDeactivated(IDocumentView view) => view.InvalidateOverlays();
    public virtual bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e) => false;
    public virtual bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e) => false;
    public virtual bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e) => false;
    public virtual bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Ctx.Tools?.ActivateDefault(); return true; }
        return false;
    }

    public abstract void Render(DrawingContext dc, int pageIndex, PageTransform transform);

    protected static Pen OutlinePen(double thickness = 1, bool dashed = false)
    {
        var pen = new Pen(Ui.ThemeBrush(ThemeKeys.ViewerOverlayOutline) ?? Brushes.Transparent, thickness);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        return pen;
    }

    protected static Brush? SelectionFill => Ui.ThemeBrush(ThemeKeys.ViewerTextSelection);
    protected static Brush? HandleBrush => Ui.ThemeBrush(ThemeKeys.ViewerOverlayHandle);

    /// <summary>Wykonanie asynchroniczne z obsługą błędów (zdarzenia myszy są synchroniczne).</summary>
    protected async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ctx.Dialogs?.ShowError(ex); }
    }
}

/// <summary>Narzędzie "przeciągnij prostokąt na stronie" (redakcja obszaru, OCR obszaru, kształt, nowe łącze).</summary>
internal class RectangleTool : ToolBase
{
    private readonly Func<IDocumentView, int, PdfRect, Task> _onRect;
    private readonly bool _allowClick;
    protected int Page = -1;
    protected PdfPoint A, B;
    protected bool Pressed;

    public RectangleTool(EditingContext ctx, string id, Func<IDocumentView, int, PdfRect, Task> onRect, bool allowClick = false) : base(ctx, id)
    {
        _onRect = onRect;
        _allowClick = allowClick;
    }

    public override bool IsVisible => Pressed;

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        Page = e.PageIndex;
        A = B = e.PagePoint;
        Pressed = true;
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (!Pressed) return false;
        var t = view.GetPageTransform(Page);
        if (t is not null)
        {
            var p = t.ToPage(e.ViewPoint);
            B = new PdfPoint(Math.Clamp(p.X, t.CropOrigin.X, t.CropOrigin.X + t.PageSize.Width), Math.Clamp(p.Y, t.CropOrigin.Y, t.CropOrigin.Y + t.PageSize.Height));
        }
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!Pressed) return false;
        Pressed = false;
        view.InvalidateOverlays();
        var rect = PdfRect.FromPoints(A, B);
        if (rect.Width > 2 && rect.Height > 2 || _allowClick)
        {
            int page = Page;
            Run(() => _onRect(view, page, rect));
        }
        return true;
    }

    public override bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Pressed)
        {
            Pressed = false;
            view.InvalidateOverlays();
            return true;
        }
        return base.OnKeyDown(view, e);
    }

    public override void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        if (!Pressed || pageIndex != Page) return;
        dc.DrawRectangle(SelectionFill, OutlinePen(1, dashed: true), transform.ToView(PdfRect.FromPoints(A, B)));
    }
}

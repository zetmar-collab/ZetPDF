using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Tools;

internal enum DragShape { Rectangle, Ellipse, Line, Arrow }

/// <summary>
/// Narzędzie „przeciągnij”: prostokąt, owal, linia, strzałka, wyróżnienie obszaru, pola formularza. Podgląd na nakładce,
/// polecenie wykonywane dopiero po puszczeniu myszy. Kliknięcie bez przeciągania tworzy obiekt o domyślnym rozmiarze.
/// </summary>
internal sealed class DragTool : ToolBase
{
    private readonly DragShape _shape;
    private readonly Func<IPdfDocumentSession, int, PdfPoint, PdfPoint, Task> _commit;
    private readonly Size _defaultSize;
    private bool _pressed;
    private int _page = -1;
    private PdfPoint _start, _end;

    public DragTool(AnnotContext ctx, string id, DragShape shape, Func<IPdfDocumentSession, int, PdfPoint, PdfPoint, Task> commit, Size? defaultSize = null)
        : base(ctx, id)
    {
        _shape = shape;
        _commit = commit;
        _defaultSize = defaultSize ?? new Size(80, 50);
    }

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _pressed = true;
        _page = e.PageIndex;
        _start = _end = e.PagePoint;
        return true;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        if (e.PageIndex == _page) _end = e.PagePoint;
        else if (view.GetPageTransform(_page) is { } t) _end = t.ToPage(e.ViewPoint);
        if (e.Modifiers.HasFlag(ModifierKeys.Shift)) _end = Constrain(_start, _end);
        view.InvalidateOverlays();
        return true;
    }

    private PdfPoint Constrain(PdfPoint a, PdfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (_shape is DragShape.Line or DragShape.Arrow)
        {
            double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), len = Math.Sqrt(dx * dx + dy * dy);
            return new PdfPoint(a.X + Math.Cos(ang) * len, a.Y + Math.Sin(ang) * len);
        }
        double m = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new PdfPoint(a.X + Math.Sign(dx == 0 ? 1 : dx) * m, a.Y + Math.Sign(dy == 0 ? 1 : dy) * m);
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _pressed = false;
        var start = _start;
        var end = _end;
        if (Math.Abs(end.X - start.X) < 3 && Math.Abs(end.Y - start.Y) < 3)
            end = _shape is DragShape.Line or DragShape.Arrow
                ? new PdfPoint(start.X + _defaultSize.Width, start.Y)
                : new PdfPoint(start.X + _defaultSize.Width, start.Y - _defaultSize.Height);
        view.InvalidateOverlays();
        int page = _page;
        _page = -1;
        _ = _commit(view.Session, page, start, end);
        return true;
    }

    protected override void Cancel()
    {
        _pressed = false;
        _page = -1;
    }

    protected override void RenderPreview(DrawingContext dc, int page, PageTransform t)
    {
        if (!_pressed || page != _page) return;
        var a = t.ToView(_start);
        var b = t.ToView(_end);
        var pen = ContentPen(t.ToViewLength(Style.LineWidth));
        switch (_shape)
        {
            case DragShape.Rectangle:
                dc.DrawRectangle(Style.Fill is { } f ? Ui.ContentBrush(f, Style.Opacity) : null, pen, new Rect(a, b));
                dc.DrawRectangle(null, OutlinePen(), new Rect(a, b));
                break;
            case DragShape.Ellipse:
                var r = new Rect(a, b);
                dc.DrawEllipse(Style.Fill is { } ef ? Ui.ContentBrush(ef, Style.Opacity) : null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            default:
                dc.DrawLine(pen, a, b);
                break;
        }
    }
}

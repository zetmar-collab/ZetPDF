using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Tools;

/// <summary>Wielokąt / linia łamana: kliknięcia dodają wierzchołki, dwuklik lub Enter kończy, Esc anuluje.</summary>
internal sealed class PolyTool : ToolBase
{
    private readonly bool _closed;
    private readonly List<PdfPoint> _points = new();
    private int _page = -1;
    private PdfPoint? _hover;

    public PolyTool(AnnotContext ctx, string id, bool closed) : base(ctx, id) => _closed = closed;

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        if (_page >= 0 && e.PageIndex != _page) return true;
        _page = e.PageIndex;
        if (e.ClickCount >= 2)
        {
            Finish(view);
            return true;
        }
        _points.Add(e.PagePoint);
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (_page < 0) return false;
        _hover = e.PageIndex == _page ? e.PagePoint : null;
        view.InvalidateOverlays();
        return false;
    }

    public override bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _points.Count >= 2) { Finish(view); return true; }
        if (e.Key == Key.Back && _points.Count > 0) { _points.RemoveAt(_points.Count - 1); view.InvalidateOverlays(); return true; }
        return base.OnKeyDown(view, e);
    }

    private void Finish(IDocumentView view)
    {
        var pts = Dedup(_points);
        int page = _page;
        Cancel();
        view.InvalidateOverlays();
        if (pts.Count < 2) return;
        var s = Style;
        _ = Ctx.AddAsync(view.Session, new AnnotationSpec
        {
            Kind = _closed ? AnnotationKind.Polygon : AnnotationKind.Polyline,
            PageIndex = page,
            Points = pts,
            Color = s.Color,
            FillColor = _closed ? s.Fill : null,
            Opacity = s.Opacity,
            LineWidth = s.LineWidth,
        });
    }

    private static List<PdfPoint> Dedup(List<PdfPoint> pts)
    {
        var r = new List<PdfPoint>();
        foreach (var p in pts)
            if (r.Count == 0 || Math.Abs(r[^1].X - p.X) + Math.Abs(r[^1].Y - p.Y) > 0.5) r.Add(p);
        return r;
    }

    protected override void Cancel()
    {
        _points.Clear();
        _page = -1;
        _hover = null;
    }

    protected override void RenderPreview(DrawingContext dc, int page, PageTransform t)
    {
        if (page != _page || _points.Count == 0) return;
        var pen = ContentPen(t.ToViewLength(Style.LineWidth));
        var pts = _points.Select(t.ToView).ToList();
        if (_hover is { } h) pts.Add(t.ToView(h));
        for (int i = 1; i < pts.Count; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
        if (_closed && pts.Count > 2) dc.DrawLine(OutlinePen(), pts[^1], pts[0]);
        var handle = Ui.Resource(Core.Theming.ThemeKeys.ViewerOverlayHandle);
        foreach (var p in pts.Take(_points.Count)) dc.DrawRectangle(handle, null, new Rect(p.X - 3, p.Y - 3, 6, 6));
    }
}

/// <summary>Ołówek (Ink): każde pociągnięcie myszą/piórem to jedna adnotacja; nacisk pióra pogrubia podgląd.</summary>
internal sealed class InkTool : ToolBase
{
    private readonly List<PdfPoint> _stroke = new();
    private int _page = -1;
    private readonly AnnotationKind _kind;

    public InkTool(AnnotContext ctx, string id, AnnotationKind kind = AnnotationKind.Ink) : base(ctx, id) => _kind = kind;

    public override Cursor Cursor => Cursors.Pen;

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _page = e.PageIndex;
        _stroke.Clear();
        _stroke.Add(e.PagePoint);
        return true;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (_page < 0) return false;
        var p = e.PageIndex == _page ? e.PagePoint : view.GetPageTransform(_page)?.ToPage(e.ViewPoint) ?? _stroke[^1];
        if (Math.Abs(_stroke[^1].X - p.X) + Math.Abs(_stroke[^1].Y - p.Y) > 0.8) _stroke.Add(p);
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (_page < 0) return false;
        var pts = Simplify(_stroke, 0.4);
        int page = _page;
        Cancel();
        view.InvalidateOverlays();
        var s = Style;
        _ = Ctx.AddAsync(view.Session, new AnnotationSpec
        {
            Kind = _kind,
            PageIndex = page,
            InkStrokes = new[] { pts },
            Color = s.Color,
            Opacity = s.Opacity,
            LineWidth = s.LineWidth,
        });
        return true;
    }

    protected override void Cancel()
    {
        _stroke.Clear();
        _page = -1;
    }

    protected override void RenderPreview(DrawingContext dc, int page, PageTransform t)
    {
        if (page != _page || _stroke.Count == 0) return;
        var pen = ContentPen(t.ToViewLength(Style.LineWidth));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(t.ToView(_stroke[0]), false, false);
            g.PolyLineTo(_stroke.Skip(1).Select(t.ToView).ToList(), true, true);
        }
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>Douglas–Peucker (mniej punktów w InkList).</summary>
    public static List<PdfPoint> Simplify(IReadOnlyList<PdfPoint> pts, double tolerance)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double max = 0; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegDist(pts[i], pts[a], pts[b]);
                if (d > max) { max = d; idx = i; }
            }
            if (idx >= 0 && max > tolerance) { keep[idx] = true; stack.Push((a, idx)); stack.Push((idx, b)); }
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    private static double SegDist(PdfPoint p, PdfPoint a, PdfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        if (len < 1e-12) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }
}

/// <summary>Narzędzie „kliknij, aby umieścić” (notatka, stempel, znaczniki Wypełnij i podpisz, podpis, załącznik).</summary>
internal sealed class ClickTool : ToolBase
{
    private readonly Func<IPdfDocumentSession, int, PdfPoint, Task> _place;
    private readonly Func<Size>? _previewSize;
    private int _hoverPage = -1;
    private PdfPoint _hover;

    public ClickTool(AnnotContext ctx, string id, Func<IPdfDocumentSession, int, PdfPoint, Task> place, Func<Size>? previewSize = null) : base(ctx, id)
    {
        _place = place;
        _previewSize = previewSize;
    }

    public override Cursor Cursor => Cursors.Cross;

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _ = _place(view.Session, e.PageIndex, e.PagePoint);
        return true;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (_previewSize is null) return false;
        _hoverPage = e.PageIndex;
        _hover = e.PagePoint;
        view.InvalidateOverlays();
        return false;
    }

    protected override void RenderPreview(DrawingContext dc, int page, PageTransform t)
    {
        if (_previewSize is null || page != _hoverPage) return;
        var s = _previewSize();
        dc.DrawRectangle(null, OutlinePen(), t.ToView(new PdfRect(_hover.X, _hover.Y - s.Height, _hover.X + s.Width, _hover.Y)));
    }
}

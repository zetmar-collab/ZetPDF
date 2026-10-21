using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Annotations.Measure;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Tools;

/// <summary>
/// Narzędzia pomiarów na nakładce widoku. Odległość: przeciągnij (lub kliknij początek i koniec), Shift – co 45°.
/// Obwód i pole: kliknięcia dodają wierzchołki, dwuklik lub Enter kończy, Backspace cofa wierzchołek, Esc anuluje.
/// Podgląd pokazuje bieżącą wartość w skali strony; wynik – adnotacja pomiarowa przez polecenie z Cofnij/Ponów.
/// </summary>
internal sealed class MeasureTool : ToolBase
{
    private readonly MeasureType _type;
    private readonly List<PdfPoint> _points = new();
    private int _page = -1;
    private PdfPoint? _hover;
    private bool _dragging;

    public MeasureTool(AnnotContext ctx, string id, MeasureType type) : base(ctx, id) => _type = type;

    /// <summary>Gotowy pomiar (testy, polecenia) – domyślnie dodaje adnotację.</summary>
    public Func<IPdfDocumentSession, MeasureSpec, Task>? Commit { get; set; }

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _view = view;
        if (_page >= 0 && e.PageIndex != _page) return true;
        _page = e.PageIndex;
        _ = Ctx.GetScaleAsync(view.Session, _page);
        if (_type == MeasureType.Distance)
        {
            if (_points.Count == 0) { _points.Add(e.PagePoint); _dragging = true; _hover = e.PagePoint; }
            else { _points.Add(Constrain(e)); Finish(view); }
            view.InvalidateOverlays();
            return true;
        }
        if (e.ClickCount >= 2) { Finish(view); return true; }
        _points.Add(e.PagePoint);
        view.InvalidateOverlays();
        return true;
    }

    private PdfPoint Constrain(PagePointerEventArgs e)
    {
        var p = e.PageIndex == _page ? e.PagePoint : ViewToPage(e) ?? e.PagePoint;
        if (!e.Modifiers.HasFlag(ModifierKeys.Shift) || _points.Count == 0) return p;
        var a = _points[^1];
        double dx = p.X - a.X, dy = p.Y - a.Y;
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), len = Math.Sqrt(dx * dx + dy * dy);
        return new PdfPoint(a.X + Math.Cos(ang) * len, a.Y + Math.Sin(ang) * len);
    }

    private IDocumentView? _view;
    private PdfPoint? ViewToPage(PagePointerEventArgs e) => _view?.GetPageTransform(_page)?.ToPage(e.ViewPoint);

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        _view = view;
        if (_page < 0) return false;
        _hover = Constrain(e);
        view.InvalidateOverlays();
        return _dragging;
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_dragging || _type != MeasureType.Distance) return false;
        _dragging = false;
        var end = Constrain(e);
        // kliknięcie bez przeciągania: czekamy na drugie kliknięcie (koniec odcinka)
        if (_points.Count == 1 && MeasureGeometry.Length(_points[0], end) > 3)
        {
            _points.Add(end);
            Finish(view);
        }
        return true;
    }

    public override bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        int min = _type == MeasureType.Area ? 3 : 2;
        if (e.Key == Key.Enter && _points.Count >= min) { Finish(view); return true; }
        if (e.Key == Key.Back && _points.Count > 0) { _points.RemoveAt(_points.Count - 1); view.InvalidateOverlays(); return true; }
        return base.OnKeyDown(view, e);
    }

    private void Finish(IDocumentView view)
    {
        var pts = Dedup(_points);
        int page = _page;
        Cancel();
        view.InvalidateOverlays();
        int min = _type == MeasureType.Area ? 3 : 2;
        if (pts.Count < min || page < 0) return;
        var s = Style;
        var spec = new MeasureSpec { Type = _type, PageIndex = page, Points = pts, Color = s.Color, Opacity = s.Opacity, LineWidth = s.LineWidth };
        if (Commit is { } c) { _ = c(view.Session, spec); return; }
        _ = Ctx.RunAsync(view.Session, "Annot.Undo.Measure", async (pdf, ct) => (await Ctx.Measures.AddAsync(pdf, spec with { Author = Ctx.Author }, ct)).Pdf, pages: new[] { page });
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
        _dragging = false;
    }

    protected override void RenderPreview(DrawingContext dc, int page, PageTransform t)
    {
        if (page != _page || _points.Count == 0 || _view is null && Ctx.ActiveView is null) return;
        var session = (_view ?? Ctx.ActiveView)!.Session;
        var pts = _points.ToList();
        if (_hover is { } h) pts.Add(h);
        var pen = ContentPen(t.ToViewLength(Style.LineWidth));
        var vp = pts.Select(t.ToView).ToList();
        for (int i = 1; i < vp.Count; i++) dc.DrawLine(pen, vp[i - 1], vp[i]);
        if (_type == MeasureType.Area && vp.Count > 2) dc.DrawLine(OutlinePen(), vp[^1], vp[0]);
        var handle = Ui.Resource(ThemeKeys.ViewerOverlayHandle);
        foreach (var p in vp) dc.DrawRectangle(handle, null, new Rect(p.X - 3, p.Y - 3, 6, 6));

        // bieżąca wartość w skali strony
        var format = MeasureFormat.From(Ctx.ScaleFor(session, page), Ctx.Measures.Culture);
        var label = format.Label(_type, format.Value(_type, pts));
        var text = new FormattedText(label, Ctx.Loc.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
            Ui.Resource(ThemeKeys.TooltipText) ?? SystemColors.InfoTextBrush, 1.0);
        var at = vp[^1];
        var box = new Rect(at.X + 12, at.Y + 12, text.Width + 8, text.Height + 4);
        dc.DrawRectangle(Ui.Resource(ThemeKeys.TooltipBackground) ?? SystemColors.InfoBrush, OutlinePen(), box);
        dc.DrawText(text, new Point(box.X + 4, box.Y + 2));
    }
}

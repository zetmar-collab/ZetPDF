using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Ui.Tools;

/// <summary>
/// Edytuj obiekt (O): zaznaczanie obrazów, formularzy, kształtów i bloków tekstu; przeciąganie = przesunięcie,
/// uchwyty narożne = skalowanie (Shift – z zachowaniem proporcji), [ / ] = obrót o 90°, strzałki = przesunięcie o 1 pt (Shift 10 pt),
/// Delete = usunięcie, menu kontekstowe (prawy przycisk) – obrót, wymiana obrazu, usunięcie. Polecenie wykonywane po puszczeniu myszy.
/// </summary>
internal sealed class EditObjectTool : ToolBase
{
    private const double HandleSize = 7;
    private readonly PageDataCache<IReadOnlyList<PageObjectInfo>> _objects;
    private PageObjectInfo? _selected;
    private long _selectedVersion;
    private enum Mode { None, Move, Scale }
    private Mode _mode;
    private PdfPoint _start, _anchor;
    private int _corner;
    private PdfMatrix _preview = PdfMatrix.Identity;

    public EditObjectTool(EditingContext ctx) : base(ctx, CommandIds.Edit.EditObject)
    {
        var svc = ctx.Get<IObjectEditService>();
        _objects = new PageDataCache<IReadOnlyList<PageObjectInfo>>((pdf, page) => svc.GetObjectsAsync(pdf, page));
    }

    public override Cursor Cursor => Cursors.Arrow;
    public override int ZIndex => 300;

    public override void OnDeactivated(IDocumentView view)
    {
        _selected = null;
        _mode = Mode.None;
        base.OnDeactivated(view);
    }

    private PageObjectInfo? Selected(IDocumentView view) =>
        _selected is not null && _selectedVersion == view.Session.Current.Version ? _selected : null;

    private static PdfPoint[] Corners(PdfRect r) => new[] { new PdfPoint(r.Left, r.Bottom), new PdfPoint(r.Right, r.Bottom), new PdfPoint(r.Right, r.Top), new PdfPoint(r.Left, r.Top) };

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.PageIndex < 0) return false;
        var t = view.GetPageTransform(e.PageIndex);
        var sel = Selected(view);
        if (e.Button == MouseButton.Left && sel is not null && sel.PageIndex == e.PageIndex && t is not null)
        {
            var corners = Corners(sel.Bounds);
            for (int i = 0; i < 4; i++)
            {
                var c = t.ToView(corners[i]);
                if (Math.Abs(c.X - e.ViewPoint.X) <= HandleSize && Math.Abs(c.Y - e.ViewPoint.Y) <= HandleSize)
                {
                    _mode = Mode.Scale;
                    _corner = i;
                    _anchor = corners[(i + 2) % 4];
                    _start = e.PagePoint;
                    return true;
                }
            }
        }
        var objs = _objects.TryGet(view, e.PageIndex);
        if (objs is null) return true;
        var hit = objs.Where(o => o.Bounds.Inflate(2).Contains(e.PagePoint)).OrderBy(o => o.Bounds.Width * o.Bounds.Height).FirstOrDefault();
        if (hit is null)
        {
            _selected = null;
            view.InvalidateOverlays();
            return e.Button == MouseButton.Left;
        }
        if (sel is null || sel.Id != hit.Id || sel.PageIndex != hit.PageIndex)
        {
            _selected = hit;
            _selectedVersion = view.Session.Current.Version;
        }
        if (e.Button == MouseButton.Right)
        {
            ShowMenu(view);
            return true;
        }
        if (e.Button == MouseButton.Left)
        {
            _mode = Mode.Move;
            _start = e.PagePoint;
            _preview = PdfMatrix.Identity;
        }
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        var sel = Selected(view);
        if (_mode == Mode.None || sel is null) return false;
        var t = view.GetPageTransform(sel.PageIndex);
        if (t is null) return true;
        var p = t.ToPage(e.ViewPoint);
        if (_mode == Mode.Move) _preview = PdfMatrix.Translation(p.X - _start.X, p.Y - _start.Y);
        else
        {
            var corner = Corners(sel.Bounds)[_corner];
            double ox = corner.X - _anchor.X, oy = corner.Y - _anchor.Y;
            double sx = Math.Abs(ox) < 1e-6 ? 1 : (p.X - _anchor.X) / ox;
            double sy = Math.Abs(oy) < 1e-6 ? 1 : (p.Y - _anchor.Y) / oy;
            sx = Math.Max(0.02, sx); sy = Math.Max(0.02, sy);
            if ((e.Modifiers & ModifierKeys.Shift) != 0) sx = sy = Math.Max(sx, sy);
            _preview = PdfMatrix.Translation(-_anchor.X, -_anchor.Y).Then(PdfMatrix.Scaling(sx, sy)).Then(PdfMatrix.Translation(_anchor.X, _anchor.Y));
        }
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        var sel = Selected(view);
        if (_mode == Mode.None || sel is null) { _mode = Mode.None; return false; }
        _mode = Mode.None;
        var m = _preview;
        _preview = PdfMatrix.Identity;
        bool changed = Math.Abs(m.E) > 0.5 || Math.Abs(m.F) > 0.5 || Math.Abs(m.A - 1) > 0.005 || Math.Abs(m.D - 1) > 0.005;
        view.InvalidateOverlays();
        if (changed) Run(() => TransformAsync(view, sel, m));
        return true;
    }

    public override bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        var sel = Selected(view);
        if (sel is null) return base.OnKeyDown(view, e);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete: Run(() => DeleteAsync(view, sel)); return true;
            case Key.OemOpenBrackets: Run(() => RotateAsync(view, sel, 90)); return true;
            case Key.OemCloseBrackets: Run(() => RotateAsync(view, sel, -90)); return true;
            case Key.Left: Run(() => TransformAsync(view, sel, PdfMatrix.Translation(-step, 0))); return true;
            case Key.Right: Run(() => TransformAsync(view, sel, PdfMatrix.Translation(step, 0))); return true;
            case Key.Up: Run(() => TransformAsync(view, sel, PdfMatrix.Translation(0, step))); return true;
            case Key.Down: Run(() => TransformAsync(view, sel, PdfMatrix.Translation(0, -step))); return true;
            case Key.Escape: _selected = null; view.InvalidateOverlays(); return true;
        }
        return false;
    }

    private void ShowMenu(IDocumentView view)
    {
        var sel = Selected(view);
        if (sel is null) return;
        var menu = new ContextMenu { PlacementTarget = view.Element, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        MenuItem Item(string key, Func<Task> action)
        {
            var mi = new MenuItem().Loc(HeaderedItemsControl.HeaderProperty, Ctx.Loc, key);
            mi.Click += (_, _) => Run(action);
            return mi;
        }
        menu.Items.Add(Item("Edit.Object.RotateLeft", () => RotateAsync(view, sel, 90)));
        menu.Items.Add(Item("Edit.Object.RotateRight", () => RotateAsync(view, sel, -90)));
        if (sel.Kind == PageObjectKind.Image) menu.Items.Add(Item("Edit.Object.ReplaceImage", () => ReplaceImageAsync(view, sel)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Edit.Object.Delete", () => DeleteAsync(view, sel)));
        menu.IsOpen = true;
    }

    private async Task TransformAsync(IDocumentView view, PageObjectInfo obj, PdfMatrix m)
    {
        var svc = Ctx.Get<IObjectEditService>();
        if (await Ctx.ExecuteAsync(view.Session, "Edit.Undo.TransformObject", (pdf, _, ct) => svc.TransformObjectAsync(pdf, obj.PageIndex, obj.Id, m, ct),
                DocumentChangeKind.PageContent, pages: new[] { obj.PageIndex }))
            await Reselect(view, obj, Transform(obj.Bounds, m));
    }

    private Task RotateAsync(IDocumentView view, PageObjectInfo obj, double degrees)
    {
        double cx = (obj.Bounds.Left + obj.Bounds.Right) / 2, cy = (obj.Bounds.Bottom + obj.Bounds.Top) / 2;
        var m = PdfMatrix.Translation(-cx, -cy).Then(PdfMatrix.Rotation(degrees)).Then(PdfMatrix.Translation(cx, cy));
        return TransformAsync(view, obj, m);
    }

    private async Task DeleteAsync(IDocumentView view, PageObjectInfo obj)
    {
        var svc = Ctx.Get<IObjectEditService>();
        _selected = null;
        await Ctx.ExecuteAsync(view.Session, "Edit.Undo.DeleteObject", (pdf, _, ct) => svc.DeleteObjectAsync(pdf, obj.PageIndex, obj.Id, ct),
            DocumentChangeKind.PageContent, pages: new[] { obj.PageIndex });
    }

    private async Task ReplaceImageAsync(IDocumentView view, PageObjectInfo obj)
    {
        var files = Ctx.Dialogs?.OpenFiles(Ctx.Loc["Edit.Object.ReplaceImage"], new[] { EditingCommands.ImageFilter(Ctx.Loc) });
        if (files is not { Count: > 0 }) return;
        var data = await File.ReadAllBytesAsync(files[0]);
        var svc = Ctx.Get<IObjectEditService>();
        if (await Ctx.ExecuteAsync(view.Session, "Edit.Undo.ReplaceImage", (pdf, _, ct) => svc.ReplaceImageAsync(pdf, obj.PageIndex, obj.Id, data, ct),
                DocumentChangeKind.PageContent, pages: new[] { obj.PageIndex }))
            await Reselect(view, obj, obj.Bounds);
    }

    /// <summary>Po zmianie zaznacza obiekt tego samego rodzaju najbliższy oczekiwanemu położeniu (identyfikatory zmieniają się z treścią).</summary>
    private async Task Reselect(IDocumentView view, PageObjectInfo old, PdfRect expected)
    {
        var objs = await _objects.GetAsync(view, old.PageIndex);
        _selected = objs.Where(o => o.Kind == old.Kind)
            .OrderBy(o => Math.Abs(o.Bounds.Left - expected.Left) + Math.Abs(o.Bounds.Top - expected.Top) + Math.Abs(o.Bounds.Width - expected.Width)).FirstOrDefault();
        _selectedVersion = view.Session.Current.Version;
        view.InvalidateOverlays();
    }

    private static PdfRect Transform(PdfRect r, PdfMatrix m)
    {
        var pts = Corners(r).Select(m.Transform).ToList();
        return new PdfRect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    public override void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        var view = Ctx.View;
        if (view is null) return;
        var objs = _objects.TryGet(view, pageIndex);
        if (objs is not null)
        {
            var faint = OutlinePen(0.5, dashed: true);
            foreach (var o in objs.Where(o => o.Kind != PageObjectKind.Text)) dc.DrawRectangle(null, faint, transform.ToView(o.Bounds));
        }
        var sel = Selected(view);
        if (sel is null || sel.PageIndex != pageIndex) return;
        var bounds = _mode == Mode.None ? sel.Bounds : Transform(sel.Bounds, _preview);
        var r = transform.ToView(bounds);
        dc.DrawRectangle(_mode == Mode.None ? null : SelectionFill, OutlinePen(1.5), r);
        var hb = HandleBrush;
        foreach (var c in Corners(bounds))
        {
            var p = transform.ToView(c);
            dc.DrawRectangle(hb, OutlinePen(1), new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Tools;

/// <summary>
/// Wybierz adnotację (R): kliknięcie zaznacza komentarz lub pole formularza, przeciąganie przesuwa, uchwyty zmieniają
/// rozmiar (polecenie wykonywane po puszczeniu myszy), Delete usuwa, dwuklik/Enter otwiera właściwości,
/// strzałki przesuwają o 1 pt (z Shift o 10 pt).
/// </summary>
internal sealed class SelectAnnotationTool : ToolBase
{
    private enum Mode { None, Move, Resize }

    private Mode _mode;
    private int _handle = -1;
    private PdfPoint _start;
    private PdfRect _preview;
    private bool _changed;

    public SelectAnnotationTool(AnnotContext ctx) : base(ctx, CommandIds.Tool.SelectAnnotation) { }

    public override Cursor Cursor => Cursors.Arrow;

    /// <summary>Akcje zależne od innych części modułu (okna właściwości, usuwanie).</summary>
    public Func<SelectedItem, Task>? OpenProperties { get; set; }
    /// <summary>Dwuklik: edycja w miejscu (pole tekstowe) albo właściwości.</summary>
    public Func<IDocumentView, SelectedItem, Task>? EditInPlace { get; set; }
    public Func<SelectedItem, Task>? DeleteSelected { get; set; }

    public override void OnDeactivated(IDocumentView view)
    {
        base.OnDeactivated(view);
        _mode = Mode.None;
    }

    private static PdfPoint[] Handles(PdfRect r) => new[]
    {
        new PdfPoint(r.Left, r.Top), new PdfPoint((r.Left + r.Right) / 2, r.Top), new PdfPoint(r.Right, r.Top),
        new PdfPoint(r.Right, (r.Bottom + r.Top) / 2), new PdfPoint(r.Right, r.Bottom), new PdfPoint((r.Left + r.Right) / 2, r.Bottom),
        new PdfPoint(r.Left, r.Bottom), new PdfPoint(r.Left, (r.Bottom + r.Top) / 2),
    };

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        var sel = Ctx.Selection;
        var t = view.GetPageTransform(e.PageIndex);
        if (sel is not null && ReferenceEquals(sel.Session, view.Session) && sel.PageIndex == e.PageIndex && t is not null)
        {
            if (e.ClickCount >= 2 && sel.Rect.Inflate(2).Contains(e.PagePoint))
            {
                _ = EditInPlace is { } edit ? edit(view, sel) : OpenProperties?.Invoke(sel);
                return true;
            }
            var hs = Handles(sel.Rect);
            double tol = t.ToPageLength(6);
            for (int i = 0; i < hs.Length; i++)
                if (Math.Abs(hs[i].X - e.PagePoint.X) <= tol && Math.Abs(hs[i].Y - e.PagePoint.Y) <= tol)
                {
                    Begin(Mode.Resize, i, e.PagePoint, sel.Rect);
                    return true;
                }
            if (sel.Rect.Inflate(tol / 2).Contains(e.PagePoint))
            {
                Begin(Mode.Move, -1, e.PagePoint, sel.Rect);
                return true;
            }
        }
        var hit = HitTest(view.Session, e.PageIndex, e.PagePoint, t is null ? 2 : t.ToPageLength(3));
        Ctx.Selection = hit;
        if (hit is not null)
        {
            if (e.ClickCount >= 2) { _ = EditInPlace is { } edit ? edit(view, hit) : OpenProperties?.Invoke(hit); return true; }
            Begin(Mode.Move, -1, e.PagePoint, hit.Rect);
        }
        return true;
    }

    private void Begin(Mode mode, int handle, PdfPoint start, PdfRect rect)
    {
        _mode = mode;
        _handle = handle;
        _start = start;
        _preview = rect;
        _changed = false;
    }

    /// <summary>Najwyżej leżący komentarz albo widżet pod punktem.</summary>
    public SelectedItem? HitTest(IPdfDocumentSession session, int page, PdfPoint p, double tol)
    {
        var widgets = Ctx.CachedWidgets(session);
        if (widgets is not null)
            for (int i = widgets.Count - 1; i >= 0; i--)
            {
                var w = widgets[i];
                if (w.Page == page && w.Rect.Inflate(tol).Contains(p)) return new SelectedItem(session, null, w.Name, page, w.Rect, w.OnState);
            }
        var list = Ctx.CachedAnnotations(session);
        if (list is null) return null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var a = list[i];
            if (a.Id.PageIndex != page || a.InReplyTo is not null || a.IsHidden || a.Kind is AnnotationKind.Link or AnnotationKind.Widget or AnnotationKind.Popup) continue;
            if (a.Rect.Inflate(tol).Contains(p)) return new SelectedItem(session, a.Id, null, page, a.Rect);
        }
        return null;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (_mode == Mode.None || Ctx.Selection is not { } sel) return false;
        var t = view.GetPageTransform(sel.PageIndex);
        var p = e.PageIndex == sel.PageIndex ? e.PagePoint : t?.ToPage(e.ViewPoint) ?? e.PagePoint;
        double dx = p.X - _start.X, dy = p.Y - _start.Y;
        if (!_changed && Math.Abs(dx) + Math.Abs(dy) < (t?.ToPageLength(3) ?? 2)) return true;
        _changed = true;
        var r = sel.Rect;
        if (_mode == Mode.Move) _preview = new PdfRect(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy);
        else
        {
            double l = r.Left, b = r.Bottom, rt = r.Right, tp = r.Top;
            switch (_handle)
            {
                case 0: l += dx; tp += dy; break;
                case 1: tp += dy; break;
                case 2: rt += dx; tp += dy; break;
                case 3: rt += dx; break;
                case 4: rt += dx; b += dy; break;
                case 5: b += dy; break;
                case 6: l += dx; b += dy; break;
                case 7: l += dx; break;
            }
            var n = PdfRect.FromPoints(new(l, b), new(rt, tp));
            if (n.Width >= 4 && n.Height >= 4) _preview = n;
        }
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (_mode == Mode.None) return false;
        _mode = Mode.None;
        view.InvalidateOverlays();
        if (_changed && Ctx.Selection is { } sel) _ = CommitRect(sel, _preview);
        return true;
    }

    public override bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (Ctx.Selection is not { } sel || !ReferenceEquals(sel.Session, view.Session)) return base.OnKeyDown(view, e);
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete:
                _ = DeleteSelected?.Invoke(sel);
                return true;
            case Key.Enter:
                _ = OpenProperties?.Invoke(sel);
                return true;
            case Key.Escape:
                Ctx.Selection = null;
                return true;
            case Key.Left: _ = CommitRect(sel, Shift(sel.Rect, -step, 0)); return true;
            case Key.Right: _ = CommitRect(sel, Shift(sel.Rect, step, 0)); return true;
            case Key.Up: _ = CommitRect(sel, Shift(sel.Rect, 0, step)); return true;
            case Key.Down: _ = CommitRect(sel, Shift(sel.Rect, 0, -step)); return true;
        }
        return base.OnKeyDown(view, e);
    }

    private static PdfRect Shift(PdfRect r, double dx, double dy) => new(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy);

    private async Task CommitRect(SelectedItem sel, PdfRect rect)
    {
        bool ok;
        if (sel.Annotation is { } id)
        {
            ok = await Ctx.RunAsync(sel.Session, "Annot.Undo.Move", (pdf, ct) => Ctx.Annotations.UpdateAsync(pdf, id, new AnnotationUpdate { Rect = rect }, ct), pages: new[] { sel.PageIndex });
        }
        else if (sel.FieldName is { } name)
        {
            ok = await Ctx.RunAsync(sel.Session, "Annot.Undo.FieldEdit", (pdf, ct) => Ctx.Forms.MoveWidgetAsync(pdf, name, sel.OnState, rect, ct),
                DocumentChangeKind.FormFields, DocumentPermissions.ModifyContent, new[] { sel.PageIndex });
        }
        else return;
        if (ok) Ctx.Selection = sel with { Rect = rect };
    }

    /// <summary>Zaznaczenie i uchwyty (nakładka modułu, widoczna także po wyborze z panelu).</summary>
    public static void RenderSelection(AnnotContext ctx, SelectAnnotationTool tool, DrawingContext dc, int page, PageTransform t, IDocumentView? view)
    {
        if (ctx.Selection is not { } sel || sel.PageIndex != page || view is null || !ReferenceEquals(sel.Session, view.Session)) return;
        var rect = tool._mode != Mode.None && tool._changed ? tool._preview : sel.Rect;
        var outline = Ui.Resource(ThemeKeys.ViewerOverlayOutline) ?? SystemColors.HighlightBrush;
        var pen = new Pen(outline, 1) { DashStyle = DashStyles.Dash };
        var vr = t.ToView(rect);
        dc.DrawRectangle(null, pen, vr);
        if (ctx.Tools?.IsActive(CommandIds.Tool.SelectAnnotation) != true) return;
        var handle = Ui.Resource(ThemeKeys.ViewerOverlayHandle) ?? SystemColors.HighlightBrush;
        var hpen = new Pen(outline, 1);
        foreach (var h in Handles(rect))
        {
            var p = t.ToView(h);
            dc.DrawRectangle(handle, hpen, new Rect(p.X - 3.5, p.Y - 3.5, 7, 7));
        }
    }
}

/// <summary>Spec pola z informacji o istniejącym polu (zmiana położenia, okno właściwości).</summary>
internal static class FieldSpecs
{
    public static FormFieldSpec FromInfo(FormFieldInfo f, PdfRect rect, string? exportValue) => new()
    {
        Kind = f.Kind,
        Name = f.FullName,
        PageIndex = f.PageIndex,
        Rect = rect,
        DefaultValue = f.DefaultValue,
        Options = f.Options,
        ExportValue = exportValue,
        Tooltip = f.Tooltip,
        Required = f.Required,
        ReadOnly = f.ReadOnly,
        Multiline = f.Multiline,
    };
}

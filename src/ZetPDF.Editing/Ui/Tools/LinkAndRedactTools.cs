using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Editing.Text;
using ZetPDF.Editing.Ui.Dialogs;

namespace ZetPDF.Editing.Ui.Tools;

/// <summary>Łącze (L): przeciągnięcie = nowe łącze (URL lub strona), kliknięcie w istniejące = edycja/usunięcie.</summary>
internal sealed class LinkTool : RectangleTool
{
    private readonly PageDataCache<IReadOnlyList<PdfLinkInfo>> _links;

    public LinkTool(EditingContext ctx) : base(ctx, CommandIds.Edit.Link, (_, _, _) => Task.CompletedTask)
    {
        var svc = ctx.Get<ILinkService>();
        _links = new PageDataCache<IReadOnlyList<PdfLinkInfo>>((pdf, page) => svc.GetLinksAsync(pdf, page));
    }

    public override bool IsVisible => true;

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!Pressed) return false;
        Pressed = false;
        view.InvalidateOverlays();
        var rect = PdfRect.FromPoints(A, B);
        int page = Page;
        if (rect.Width > 3 && rect.Height > 3) Run(() => CreateAsync(view, page, rect));
        else
        {
            var links = _links.TryGet(view, page);
            int index = links is null ? -1 : links.ToList().FindLastIndex(l => l.Rect.Contains(A));
            if (index >= 0) Run(() => EditAsync(view, page, index, links![index]));
        }
        return true;
    }

    private async Task CreateAsync(IDocumentView view, int page, PdfRect rect)
    {
        var dlg = new LinkDialog(Ctx.Loc, view.PageCount, null, canDelete: false);
        if (Ctx.ShowDialog(dlg) != true) return;
        var target = dlg.Target;
        var svc = Ctx.Get<ILinkService>();
        await Ctx.ExecuteAsync(view.Session, "Edit.Undo.AddLink", (pdf, _, ct) => svc.AddLinkAsync(pdf, page, rect, target, ct), DocumentChangeKind.Annotations, DocumentPermissions.Annotate, pages: new[] { page });
    }

    private async Task EditAsync(IDocumentView view, int page, int index, PdfLinkInfo link)
    {
        var dlg = new LinkDialog(Ctx.Loc, view.PageCount, link, canDelete: true);
        if (Ctx.ShowDialog(dlg) != true) return;
        var svc = Ctx.Get<ILinkService>();
        if (dlg.DeleteRequested)
            await Ctx.ExecuteAsync(view.Session, "Edit.Undo.DeleteLink", (pdf, _, ct) => svc.DeleteLinkAsync(pdf, page, index, ct), DocumentChangeKind.Annotations, DocumentPermissions.Annotate, pages: new[] { page });
        else
        {
            var target = dlg.Target;
            await Ctx.ExecuteAsync(view.Session, "Edit.Undo.EditLink", (pdf, _, ct) => svc.UpdateLinkAsync(pdf, page, index, link.Rect, target, ct), DocumentChangeKind.Annotations, DocumentPermissions.Annotate, pages: new[] { page });
        }
    }

    public override void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        var view = Ctx.View;
        if (view is not null && _links.TryGet(view, pageIndex) is { } links)
        {
            var pen = OutlinePen(1);
            foreach (var l in links) dc.DrawRectangle(null, pen, transform.ToView(l.Rect));
        }
        base.Render(dc, pageIndex, transform);
    }
}

/// <summary>Zaznacz tekst do redakcji: przeciągnięcie od znaku do znaku (lub bieżące zaznaczenie tekstu przy aktywacji).</summary>
internal sealed class RedactTextTool : ToolBase
{
    private readonly Func<IDocumentView, IReadOnlyList<RedactionMark>, Task> _mark;
    private int _page = -1;
    private PdfPoint _a, _b;
    private bool _pressed;
    private PageText? _text;

    public RedactTextTool(EditingContext ctx, Func<IDocumentView, IReadOnlyList<RedactionMark>, Task> mark) : base(ctx, CommandIds.Redact.MarkText) => _mark = mark;

    public override Cursor Cursor => Cursors.IBeam;
    public override bool IsVisible => _pressed;

    public override void OnActivated(IDocumentView view)
    {
        base.OnActivated(view);
        if (view.TextSelection is { Spans.Count: > 0 } sel)
        {
            var marks = sel.Spans.Where(s => s.Quads.Count > 0).Select(s => new RedactionMark(s.PageIndex, s.Quads)).ToList();
            view.ClearTextSelection();
            if (marks.Count > 0) Run(() => _mark(view, marks));
        }
    }

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _page = e.PageIndex;
        _a = _b = e.PagePoint;
        _pressed = true;
        _text = null;
        var page = _page;
        _ = view.GetPageTextAsync(page).ContinueWith(t => { if (t.IsCompletedSuccessfully && _page == page) { _text = t.Result; view.InvalidateOverlays(); } },
            TaskScheduler.FromCurrentSynchronizationContext());
        return true;
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        if (view.GetPageTransform(_page) is { } t) _b = t.ToPage(e.ViewPoint);
        view.InvalidateOverlays();
        return true;
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _pressed = false;
        view.InvalidateOverlays();
        int page = _page;
        var (a, b) = (_a, _b);
        Run(async () =>
        {
            var text = _text ?? await view.GetPageTextAsync(page);
            var rects = Range(text, a, b);
            if (rects.Count > 0) await _mark(view, new[] { new RedactionMark(page, rects.Select(PdfQuad.FromRect).ToList()) });
        });
        return true;
    }

    private static List<PdfRect> Range(PageText text, PdfPoint a, PdfPoint b)
    {
        int i = PageTextTools.NearestChar(text, a), j = PageTextTools.NearestChar(text, b);
        if (i < 0 || j < 0) return new();
        if (i > j) (i, j) = (j, i);
        return PageTextTools.RangeRects(text, i, j - i + 1);
    }

    public override void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        if (!_pressed || pageIndex != _page || _text is null) return;
        var fill = Ui.ThemeBrush(ThemeKeys.ViewerRedactionMark);
        foreach (var r in Range(_text, _a, _b)) dc.DrawRectangle(fill, null, transform.ToView(r));
    }
}

/// <summary>Nakładka podglądu oznaczeń redakcji (ZIndex 400) – rysowana zawsze, gdy dokument ma adnotacje /Redact.</summary>
internal sealed class RedactionOverlay : IPageOverlay
{
    private readonly IDocumentView _view;
    private readonly PageDataCache<Dictionary<int, List<PdfRect>>> _marks = new((pdf, _) => Task.FromResult(RedactionService.ReadMarks(pdf)));

    public RedactionOverlay(IDocumentView view) => _view = view;

    public int ZIndex => 400;
    public bool IsVisible => true;

    public bool HasMarks => _marks.TryGet(_view, -1) is { Count: > 0 };

    public void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        var marks = _marks.TryGet(_view, -1);
        if (marks is null || !marks.TryGetValue(pageIndex, out var rects)) return;
        var fill = Ui.ThemeBrush(ThemeKeys.ViewerRedactionMark);
        var pen = new Pen(Ui.ThemeBrush(ThemeKeys.StatusError) ?? Brushes.Transparent, 1);
        foreach (var r in rects) dc.DrawRectangle(fill, pen, transform.ToView(r));
    }
}

using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Tools;

/// <summary>
/// Adiustacja tekstu (Wyróżnij Shift+H, Podkreśl Ctrl+U, Przekreśl Shift+S, Podkreślenie falowane Shift+U).
/// Gdy jest zaznaczony tekst (narzędzie Zaznacz tekst) – adnotacja powstaje od razu z jego czworokątów; w przeciwnym
/// razie przeciągnięcie zaznacza znaki strony (prostokąty znaków z PDFium przez <see cref="IDocumentView.GetPageTextAsync"/>).
/// </summary>
internal sealed class MarkupTool : ToolBase
{
    private readonly AnnotationKind _kind;
    private PageText? _text;
    private int _page = -1;
    private int _anchor = -1, _focus = -1;
    private bool _pressed;

    public MarkupTool(AnnotContext ctx, string id, AnnotationKind kind) : base(ctx, id) => _kind = kind;

    public override Cursor Cursor => Cursors.IBeam;

    public override void OnActivated(IDocumentView view)
    {
        base.OnActivated(view);
        if (view.TextSelection is { Spans.Count: > 0 } sel)
        {
            view.ClearTextSelection();
            _ = ApplySelectionAsync(view.Session, sel);
        }
    }

    /// <summary>Tworzy adnotacje (po jednej na stronę) z zaznaczenia tekstu.</summary>
    public Task ApplySelectionAsync(IPdfDocumentSession session, PdfTextSelection sel)
    {
        var style = Style;
        var specs = sel.Spans.GroupBy(s => s.PageIndex).Select(g => new AnnotationSpec
        {
            Kind = _kind,
            PageIndex = g.Key,
            Quads = g.SelectMany(s => s.Quads).ToList(),
            Color = style.Color,
            Opacity = style.Opacity,
            Author = Ctx.Author,
        }).Where(s => s.Quads!.Count > 0).ToList();
        if (specs.Count == 0) return Task.CompletedTask;
        return Ctx.RunAsync(session, "Annot.Undo.Add", async (pdf, ct) =>
        {
            foreach (var s in specs) pdf = (await Ctx.Annotations.AddAsync(pdf, s, ct)).Pdf;
            return pdf;
        }, pages: specs.Select(s => s.PageIndex).ToList());
    }

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _pressed = true;
        _page = e.PageIndex;
        _anchor = _focus = -1;
        var start = e.PagePoint;
        _ = LoadAsync(view, e.PageIndex, start);
        return true;
    }

    private async Task LoadAsync(IDocumentView view, int page, PdfPoint start)
    {
        try
        {
            var text = await view.GetPageTextAsync(page);
            if (_page != page) return;
            _text = text;
            _anchor = HitChar(text, start);
            if (_focus < 0) _focus = _anchor;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _text = null; }
    }

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        if (_text is not null && e.PageIndex == _page)
        {
            int f = HitChar(_text, e.PagePoint);
            if (f >= 0) _focus = f;
            view.InvalidateOverlays();
        }
        return true;
    }

    public override bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _pressed = false;
        var quads = CurrentQuads();
        int page = _page;
        Cancel();
        view.InvalidateOverlays();
        if (quads.Count == 0) return true;
        var style = Style;
        _ = Ctx.AddAsync(view.Session, new AnnotationSpec { Kind = _kind, PageIndex = page, Quads = quads, Color = style.Color, Opacity = style.Opacity });
        return true;
    }

    protected override void Cancel()
    {
        _pressed = false;
        _text = null;
        _anchor = _focus = -1;
        _page = -1;
    }

    private List<PdfQuad> CurrentQuads()
    {
        if (_text is null || _anchor < 0 || _focus < 0) return new List<PdfQuad>();
        return LineQuads(_text, Math.Min(_anchor, _focus), Math.Max(_anchor, _focus));
    }

    protected override void RenderPreview(DrawingContext dc, int page, PageTransform t)
    {
        if (!_pressed || page != _page) return;
        var brush = Ui.ContentBrush(Style.Color, 0.35);
        foreach (var q in CurrentQuads()) dc.DrawRectangle(brush, null, t.ToView(q.Bounds));
    }

    /// <summary>Indeks znaku najbliższego punktowi (pomija znaki generowane).</summary>
    public static int HitChar(PageText text, PdfPoint p)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < text.Chars.Count; i++)
        {
            var c = text.Chars[i];
            if (c.IsGenerated || c.Box.IsEmpty) continue;
            var b = c.Box;
            double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
            double dy = p.Y < b.Bottom ? b.Bottom - p.Y : p.Y > b.Top ? p.Y - b.Top : 0;
            double d = dx * dx + dy * dy * 4;
            if (d < bestD) { bestD = d; best = i; }
        }
        return bestD > 60 * 60 ? -1 : best;
    }

    /// <summary>Czworokąty wierszy dla zakresu znaków [from, to].</summary>
    public static List<PdfQuad> LineQuads(PageText text, int from, int to)
    {
        var result = new List<PdfQuad>();
        PdfRect? line = null;
        for (int i = from; i <= to && i < text.Chars.Count; i++)
        {
            var c = text.Chars[i];
            if (c.IsGenerated || c.Box.IsEmpty)
            {
                if (c.Char is '\n' or '\r' && line is { } l0) { result.Add(PdfQuad.FromRect(l0)); line = null; }
                continue;
            }
            var b = c.Box;
            if (line is { } l)
            {
                double overlap = Math.Min(l.Top, b.Top) - Math.Max(l.Bottom, b.Bottom);
                bool sameLine = overlap > Math.Min(l.Height, b.Height) * 0.4 && b.Left >= l.Left - 2;
                if (sameLine) { line = l.Union(b); continue; }
                result.Add(PdfQuad.FromRect(l));
            }
            line = b;
        }
        if (line is { } last) result.Add(PdfQuad.FromRect(last));
        return result;
    }
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Ui.Dialogs;

namespace ZetPDF.Editing.Ui.Tools;

/// <summary>Edytuj tekst (T): obrys bloków tekstu strony, kliknięcie otwiera pole edycji nad blokiem.</summary>
internal sealed class EditTextTool : ToolBase
{
    private readonly PageDataCache<IReadOnlyList<PdfTextBlock>> _blocks;
    private PdfTextBlock? _hover;
    private bool _busy;

    public EditTextTool(EditingContext ctx) : base(ctx, CommandIds.Edit.EditText)
    {
        var svc = ctx.Get<ITextEditService>();
        _blocks = new PageDataCache<IReadOnlyList<PdfTextBlock>>((pdf, page) => svc.GetTextBlocksAsync(pdf, page));
    }

    public override Cursor Cursor => Cursors.IBeam;
    public override int ZIndex => 300;

    public override bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        var hit = e.PageIndex < 0 ? null : _blocks.TryGet(view, e.PageIndex)?.LastOrDefault(b => b.Bounds.Inflate(1).Contains(e.PagePoint));
        if (!Equals(hit, _hover)) { _hover = hit; view.InvalidateOverlays(); }
        return false;
    }

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0 || _busy) return false;
        var blocks = _blocks.TryGet(view, e.PageIndex);
        var block = blocks?.LastOrDefault(b => b.Bounds.Inflate(1).Contains(e.PagePoint));
        if (block is null) return blocks is not null;
        Run(() => EditAsync(view, block));
        return true;
    }

    private async Task EditAsync(IDocumentView view, PdfTextBlock block)
    {
        var t = view.GetPageTransform(block.PageIndex);
        if (t is null) return;
        var r = t.ToView(block.Bounds);
        var pos = TextEditorPopup.ScreenPoint(view.Element, new Point(r.Left - 8, r.Top - 70));
        var style = new TextStyle { FontFamily = Text.FontNames.ToFamily(block.FontName), FontSize = block.FontSize, Bold = block.Bold, Italic = block.Italic, Color = block.Color };
        var popup = new TextEditorPopup(Ctx.Loc, "Edit.Text.EditTitle", block.Text, style, pos, r.Width);
        _busy = true;
        try
        {
            if (Ctx.ShowDialog(popup) != true) return;
            var text = popup.ResultText;
            var newStyle = popup.ResultStyle;
            if (text == block.Text && newStyle == style) return;
            var svc = Ctx.Get<ITextEditService>();
            await Ctx.ExecuteAsync(view.Session, "Edit.Undo.EditText",
                (pdf, _, ct) => svc.ReplaceTextBlockAsync(pdf, block.PageIndex, block.Id, text, newStyle, ct),
                DocumentChangeKind.PageContent, pages: new[] { block.PageIndex });
        }
        finally { _busy = false; }
    }

    public override void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        var view = Ctx.View;
        if (view is null) return;
        var blocks = _blocks.TryGet(view, pageIndex);
        if (blocks is null) return;
        var pen = OutlinePen(0.75, dashed: true);
        foreach (var b in blocks)
        {
            var r = transform.ToView(b.Bounds.Inflate(1));
            if (ReferenceEquals(b, _hover) || Equals(b, _hover)) dc.DrawRectangle(SelectionFill, OutlinePen(1.25), r);
            else dc.DrawRectangle(null, pen, r);
        }
    }
}

/// <summary>Dodaj tekst: kliknięcie w miejscu → pole tekstu; tekst zapisywany czcionką TrueType z polskimi znakami.</summary>
internal sealed class AddTextTool : ToolBase
{
    private bool _busy;

    public AddTextTool(EditingContext ctx) : base(ctx, CommandIds.Edit.AddText) { }

    public override Cursor Cursor => Cursors.IBeam;

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0 || _busy) return false;
        int page = e.PageIndex;
        var point = e.PagePoint;
        var viewPoint = e.ViewPoint;
        Run(() => AddAsync(view, page, point, viewPoint));
        return true;
    }

    private async Task AddAsync(IDocumentView view, int page, PdfPoint point, Point viewPoint)
    {
        var s = Ctx.Settings;
        var style = new TextStyle { FontFamily = s.FontFamily, FontSize = s.FontSize, Bold = s.Bold, Italic = s.Italic, Color = EditingSettings.Color(s.TextColor) };
        var popup = new TextEditorPopup(Ctx.Loc, "Edit.Text.AddTitle", string.Empty, style, TextEditorPopup.ScreenPoint(view.Element, new Point(viewPoint.X - 8, viewPoint.Y - 70)), 200);
        _busy = true;
        try
        {
            if (Ctx.ShowDialog(popup) != true || string.IsNullOrWhiteSpace(popup.ResultText)) return;
            var text = popup.ResultText;
            var st = popup.ResultStyle;
            Ctx.SaveSettings(x => { x.FontFamily = st.FontFamily; x.FontSize = st.FontSize; x.Bold = st.Bold; x.Italic = st.Italic; x.TextColor = EditingSettings.Pack(st.Color); });
            // Kliknięcie = lewy górny róg pierwszej linii (na stronie bez obrotu); linia bazowa niżej o ~0,8 em.
            var t = view.GetPageTransform(page);
            var baseline = t is null ? new PdfPoint(point.X, point.Y - st.FontSize * 0.8)
                : t.ToPage(new Point(viewPoint.X, viewPoint.Y + t.ToViewLength(st.FontSize * 0.8)));
            var svc = Ctx.Get<ITextEditService>();
            await Ctx.ExecuteAsync(view.Session, "Edit.Undo.AddText", (pdf, _, ct) => svc.AddTextAsync(pdf, page, baseline, text, st, null, ct),
                DocumentChangeKind.PageContent, pages: new[] { page });
        }
        finally { _busy = false; }
    }

    public override void Render(DrawingContext dc, int pageIndex, PageTransform transform) { }
}

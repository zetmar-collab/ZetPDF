using System.Windows.Input;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Viewer.Text;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tools;

/// <summary>
/// Zaznacz tekst (V): przeciąganie zaznacza tekst (także przez wiele stron), dwuklik – słowo.
/// Wynik w <see cref="IDocumentView.TextSelection"/> (adiustacja, redakcja i Ctrl+C korzystają z niego).
/// Tekst stron pochodzi z pamięci podręcznej widoku (PDFium); brakujące strony doczytywane są w tle.
/// </summary>
public sealed class SelectTextTool : IViewerTool
{
    private bool _pressed;
    private bool _moved;
    private int _anchorPage = -1;
    private PdfPoint _anchorPoint;
    private int _focusPage = -1;
    private PdfPoint _focusPoint;

    public string Id => CommandIds.Tool.SelectText;
    public Cursor Cursor => Cursors.IBeam;
    public IPageOverlay? Overlay => null;

    public void OnActivated(IDocumentView view) => (view as DocumentView)?.UpdateCursor();
    public void OnDeactivated(IDocumentView view) => _pressed = false;

    public bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (view is not DocumentView dv || e.Button != MouseButton.Left) return false;
        var (page, point) = dv.NearestPage(e);
        if (page < 0) return false;
        if (e.ClickCount >= 2)
        {
            SelectWord(dv, page, point);
            return true;
        }
        if (e.Modifiers.HasFlag(ModifierKeys.Shift) && _anchorPage >= 0)
        {
            _focusPage = page;
            _focusPoint = point;
            _pressed = true;
            _moved = true;
            Update(dv);
            return true;
        }
        _pressed = true;
        _moved = false;
        _anchorPage = _focusPage = page;
        _anchorPoint = _focusPoint = point;
        dv.EnsureText(page);
        return true;
    }

    public bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed || view is not DocumentView dv) return false;
        var (page, point) = dv.NearestPage(e);
        if (page < 0) return true;
        _focusPage = page;
        _focusPoint = point;
        _moved = true;
        Update(dv);
        return true;
    }

    public bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _pressed = false;
        if (view is DocumentView dv && !_moved) dv.ClearTextSelection();
        return true;
    }

    public bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && view.TextSelection is not null)
        {
            view.ClearTextSelection();
            return true;
        }
        return false;
    }

    private void Update(DocumentView dv)
    {
        int a = _anchorPage, f = _focusPage;
        var ap = _anchorPoint;
        var fp = _focusPoint;
        // wszystkie strony zakresu muszą mieć tekst – brakujące doczytujemy i ponawiamy
        var missing = new List<Task>();
        for (int p = Math.Min(a, f); p <= Math.Max(a, f); p++)
            if (dv.GetCachedText(p) is null) missing.Add(dv.EnsureText(p));
        if (missing.Count > 0)
        {
            _ = WhenLoaded(dv, missing);
            return;
        }
        var anchorText = dv.GetCachedText(a)!;
        var focusText = dv.GetCachedText(f)!;
        int ac = TextGeometry.HitCaret(anchorText, ap);
        int fc = TextGeometry.HitCaret(focusText, fp);
        if (ac < 0) ac = a <= f ? 0 : anchorText.Text.Length;
        if (fc < 0) fc = f >= a ? focusText.Text.Length : 0;
        dv.SetTextSelection(TextGeometry.BuildSelection(new TextCaret(a, ac), new TextCaret(f, fc), dv.GetCachedText));
    }

    private async Task WhenLoaded(DocumentView dv, List<Task> tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch { return; }
        if (_anchorPage >= 0) Update(dv);
    }

    private static async void SelectWord(DocumentView dv, int page, PdfPoint point)
    {
        PageText text;
        try { text = await dv.EnsureText(page); }
        catch { return; }
        int i = TextGeometry.HitChar(text, point);
        if (i < 0) return;
        var (s, e) = TextGeometry.WordAt(text.Text, i);
        dv.SetTextSelection(TextGeometry.BuildSelection(new TextCaret(page, s), new TextCaret(page, e), dv.GetCachedText));
    }
}

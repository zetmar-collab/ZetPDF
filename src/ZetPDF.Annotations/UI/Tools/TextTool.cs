using System.Windows;
using System.Windows.Input;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Tools;

/// <summary>
/// Pole tekstowe (FreeText z ramką), maszyna do pisania (bez ramki), objaśnienie (callout z linią do punktu)
/// i tekst Wypełnij i podpisz: kliknięcie otwiera edytor w miejscu; po zatwierdzeniu powstaje adnotacja o rozmiarze
/// dopasowanym do tekstu. Notatka: kliknięcie otwiera edytor treści i tworzy ikonę z okienkiem.
/// </summary>
internal sealed class TextTool : ToolBase
{
    private readonly AnnotationKind _kind;

    public TextTool(AnnotContext ctx, string id, AnnotationKind kind) : base(ctx, id) => _kind = kind;

    public override Cursor Cursor => _kind == AnnotationKind.Note ? Cursors.Cross : Cursors.IBeam;

    public override bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        if (InlineEditor.IsOpen) { InlineEditor.CloseCurrent(); return true; }
        var t = view.GetPageTransform(e.PageIndex);
        if (t is null) return true;
        var style = Style;
        var session = view.Session;
        int page = e.PageIndex;
        var at = e.PagePoint;
        PdfPoint boxTopLeft = _kind == AnnotationKind.Callout ? new PdfPoint(at.X + 50, at.Y + 60) : at;
        var editorAt = t.ToView(boxTopLeft);
        const bool multiline = true;
        double width = _kind == AnnotationKind.Note ? 220 : 200;
        InlineEditor.EditText(view, new Rect(editorAt.X, editorAt.Y, t.ToViewLength(width), t.ToViewLength(style.FontSize * 2.2)), string.Empty, multiline,
            t.ToViewLength(style.FontSize), (exit, text) =>
            {
                if (exit == EditorExit.Cancel || string.IsNullOrWhiteSpace(text)) return;
                var spec = new AnnotationSpec
                {
                    Kind = _kind,
                    PageIndex = page,
                    Contents = text,
                    Color = style.Color,
                    FillColor = _kind is AnnotationKind.FreeText or AnnotationKind.Callout ? style.Fill : null,
                    Opacity = style.Opacity,
                    LineWidth = _kind == AnnotationKind.Typewriter ? 0 : style.LineWidth,
                    TextStyle = new TextStyle { FontFamily = style.FontFamily, FontSize = style.FontSize, Color = _kind == AnnotationKind.Note ? PdfColor.Black : style.Color },
                    Rect = new PdfRect(boxTopLeft.X, boxTopLeft.Y, boxTopLeft.X, boxTopLeft.Y),
                    Points = _kind == AnnotationKind.Callout ? new[] { at, new PdfPoint(boxTopLeft.X, boxTopLeft.Y - style.FontSize) } : null,
                };
                _ = Ctx.AddAsync(session, spec);
            });
        return true;
    }
}

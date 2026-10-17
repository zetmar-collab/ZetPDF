using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI;

/// <summary>Akcje wspólne dla narzędzia Wybierz adnotację i panelu Komentarze.</summary>
internal static class AnnotationActions
{
    public static async Task EditPropertiesAsync(AnnotContext ctx, IPdfDocumentSession session, AnnotationInfo a)
    {
        var parts = StyleParts.Contents | StyleParts.Opacity;
        if (a.Kind is not (AnnotationKind.Stamp or AnnotationKind.FileAttachment)) parts |= StyleParts.Color;
        if (a.Kind is AnnotationKind.Square or AnnotationKind.Circle or AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.Polygon
            or AnnotationKind.Polyline or AnnotationKind.Ink or AnnotationKind.FreeText or AnnotationKind.Callout) parts |= StyleParts.LineWidth;
        var text = a.InReplyTo is null ? await ctx.Annotations.GetTextStyleAsync(session.Current.GetBytes(), a.Id) : null;
        if (text is not null) parts |= StyleParts.Font;
        if (a.InReplyTo is not null) parts = StyleParts.Contents;
        var style = new ToolStyle { Color = a.Color ?? PdfColor.Yellow, Opacity = a.Opacity, LineWidth = 1 };
        if (text is not null) { style.FontFamily = text.FontFamily; style.FontSize = text.FontSize; }
        var info = ctx.Loc.Format("Annot.Props.Info", ctx.Loc["Annot.Kind." + a.Kind], a.Author, (a.Modified ?? a.Created)?.LocalDateTime.ToString("g", ctx.Loc.CurrentCulture) ?? "");
        var dlg = new StyleDialog(ctx.Loc, "Annot.Props.Title", parts, style, a.Contents, info);
        if (Ui.Show(ctx.Dialogs, dlg) != true) return;
        var r = dlg.Result;
        var update = new AnnotationUpdate
        {
            Contents = dlg.Contents != a.Contents ? dlg.Contents : null,
            Color = parts.HasFlag(StyleParts.Color) && r.Color != (a.Color ?? PdfColor.Yellow) ? r.Color : null,
            Opacity = parts.HasFlag(StyleParts.Opacity) && Math.Abs(r.Opacity - a.Opacity) > 0.005 ? r.Opacity : null,
            LineWidth = parts.HasFlag(StyleParts.LineWidth) && Math.Abs(r.LineWidth - 1) > 0.001 ? r.LineWidth : null,
            TextStyle = text is not null && (r.FontFamily != text.FontFamily || Math.Abs(r.FontSize - text.FontSize) > 0.01)
                ? text with { FontFamily = r.FontFamily, FontSize = r.FontSize, Color = r.Color != (a.Color ?? PdfColor.Yellow) ? r.Color : text.Color }
                : null,
        };
        await ctx.RunAsync(session, "Annot.Undo.Edit", (pdf, ct) => ctx.Annotations.UpdateAsync(pdf, a.Id, update, ct), pages: new[] { a.Id.PageIndex });
    }

    public static async Task EditFieldAsync(AnnotContext ctx, IPdfDocumentSession session, string fieldName)
    {
        var bytes = session.Current.GetBytes();
        var spec = await ctx.Forms.DescribeAsync(bytes, fieldName);
        var names = (await ctx.Forms.GetFieldsAsync(bytes)).Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        var dlg = new FieldPropertiesDialog(ctx.Loc, spec, n => names.Contains(n));
        if (Ui.Show(ctx.Dialogs, dlg) != true || dlg.Result is not { } result) return;
        await ctx.RunAsync(session, "Annot.Undo.FieldEdit", (pdf, ct) => ctx.Forms.UpdateFieldAsync(pdf, fieldName, result, ct),
            DocumentChangeKind.FormFields, DocumentPermissions.ModifyContent, new[] { spec.PageIndex });
    }

    public static async Task OpenAsync(AnnotContext ctx, SelectedItem sel)
    {
        if (sel.FieldName is { } name) { await EditFieldAsync(ctx, sel.Session, name); return; }
        if (sel.Annotation is not { } id) return;
        var a = (await ctx.GetAnnotationsAsync(sel.Session)).FirstOrDefault(x => x.Id == id);
        if (a is not null) await EditPropertiesAsync(ctx, sel.Session, a);
    }

    public static async Task DeleteAsync(AnnotContext ctx, SelectedItem sel)
    {
        bool ok;
        if (sel.FieldName is { } name)
            ok = await ctx.RunAsync(sel.Session, "Annot.Undo.FieldDelete", (pdf, ct) => ctx.Forms.DeleteFieldsAsync(pdf, new[] { name }, ct),
                DocumentChangeKind.FormFields | DocumentChangeKind.Annotations, DocumentPermissions.ModifyContent, new[] { sel.PageIndex });
        else if (sel.Annotation is { } id)
            ok = await ctx.RunAsync(sel.Session, "Annot.Undo.Delete", (pdf, ct) => ctx.Annotations.DeleteAsync(pdf, new[] { id }, ct), pages: new[] { sel.PageIndex });
        else return;
        if (ok) ctx.Selection = null;
    }
}

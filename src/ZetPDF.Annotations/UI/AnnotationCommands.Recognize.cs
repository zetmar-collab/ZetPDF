using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Annotations.Forms.Recognition;
using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI;

/// <summary>Rozpoznawanie pól na płaskim PDF lub skanie: analiza w tle, okno podglądu, tworzenie pól jednym krokiem Cofnij.</summary>
internal sealed partial class AnnotationCommands
{
    private async Task RecognizeFieldsAsync(IPdfDocumentSession s)
    {
        var renderer = _ctx.Services.GetService<IPdfRenderer>();
        if (renderer is null || Dialogs is not { } dialogs) return;
        var bytes = s.Current.GetSharedBytes();
        var generic = new FieldGenericNames(_loc["Annot.Recognize.GenericText"], _loc["Annot.Recognize.GenericCheckBox"]);
        FieldRecognitionResult? result = null;
        bool finished = await dialogs.RunWithProgressAsync(_loc["Annot.Recognize.Progress"],
            async (progress, ct) => result = await _ctx.Forms.RecognizeFieldsAsync(bytes, renderer, generic, progress, ct));
        if (!finished || result is null) return;
        var skipped = SkippedText(result);
        var found = result.Proposals;
        if (found.Count == 0)
        {
            dialogs.ShowMessage(skipped.Length > 0 ? _loc["Annot.Recognize.None"] + " " + skipped : _loc["Annot.Recognize.None"], null, skipped.Length > 0 ? MessageSeverity.Warning : MessageSeverity.Info);
            return;
        }

        using var preview = await renderer.OpenAsync(bytes);
        var existing = (await _ctx.Forms.GetFieldsAsync(bytes)).Select(f => f.FullName).ToList();
        var dlg = new RecognizeFieldsDialog(_loc, found, preview, existing, skipped);
        if (Ui.Show(dialogs, dlg) != true) return;
        var specs = dlg.SelectedSpecs();
        if (specs.Count == 0) return;
        await _ctx.RunAsync(s, "Annot.Undo.RecognizeFields", (pdf, ct) => _ctx.Forms.CreateFieldsAsync(pdf, specs, ct),
            DocumentChangeKind.FormFields | DocumentChangeKind.Annotations, DocumentPermissions.ModifyContent, specs.Select(f => f.PageIndex).Distinct().ToList());
    }

    private string SkippedText(FieldRecognitionResult result) => result.SkippedPages.Count == 0 ? string.Empty
        : _loc.Format("Annot.Recognize.Skipped", string.Join(", ", result.SkippedPages.Select(p => p.PageIndex + 1).Take(50)));
}

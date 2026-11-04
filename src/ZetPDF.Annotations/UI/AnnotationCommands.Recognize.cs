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
        IReadOnlyList<FieldProposal>? found = null;
        bool finished = await dialogs.RunWithProgressAsync(_loc["Annot.Recognize.Progress"],
            async (progress, ct) => found = await _ctx.Forms.RecognizeFieldsAsync(bytes, renderer, generic, progress, ct));
        if (!finished || found is null) return;
        if (found.Count == 0)
        {
            dialogs.ShowMessage(_loc["Annot.Recognize.None"], null, MessageSeverity.Info);
            return;
        }

        using var preview = await renderer.OpenAsync(bytes);
        var existing = (await _ctx.Forms.GetFieldsAsync(bytes)).Select(f => f.FullName).ToList();
        var dlg = new RecognizeFieldsDialog(_loc, found, preview, existing);
        if (Ui.Show(dialogs, dlg) != true) return;
        var specs = dlg.SelectedSpecs();
        if (specs.Count == 0) return;
        await _ctx.RunAsync(s, "Annot.Undo.RecognizeFields", (pdf, ct) => _ctx.Forms.CreateFieldsAsync(pdf, specs, ct),
            DocumentChangeKind.FormFields | DocumentChangeKind.Annotations, DocumentPermissions.ModifyContent, specs.Select(f => f.PageIndex).Distinct().ToList());
    }
}

using ZetPDF.Annotations.Measure;
using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Annotations.UI.Tools;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI;

/// <summary>Polecenia grupy Pomiary (etap 3): odległość, obwód, pole, skala i kalibracja, eksport CSV.</summary>
internal sealed partial class AnnotationCommands
{
    private DragTool? _calibrateTool;

    private void RegisterMeasure()
    {
        Tool(new MeasureTool(_ctx, CommandIds.Measure.Distance, MeasureType.Distance));
        Tool(new MeasureTool(_ctx, CommandIds.Measure.Perimeter, MeasureType.Perimeter));
        Tool(new MeasureTool(_ctx, CommandIds.Measure.Area, MeasureType.Area));
        _registry.Register(CommandIds.Measure.Scale, Doc(s => ScaleAsync(s, null), DocumentPermissions.Annotate));
        _registry.Register(CommandIds.Measure.Export, Doc(ExportMeasurementsAsync, DocumentPermissions.None));
        // kalibracja: zmierz znany odcinek (narzędzie tymczasowe, bez przycisku wstążki)
        _calibrateTool = new DragTool(_ctx, CommandIds.Measure.Scale, DragShape.Line, (s, page, a, b) => CalibratedAsync(s, page, a, b), new System.Windows.Size(0, 0));
    }

    private int CurrentPage(IPdfDocumentSession s) => _ctx.Viewer?.GetView(s)?.CurrentPageIndex ?? 0;

    private async Task<int> PageCountAsync(IPdfDocumentSession s) =>
        _ctx.Viewer?.GetView(s)?.PageCount ?? await Task.Run(() => Core.Documents.PdfSharpEdit.Read(s.Current.GetSharedBytes(), d => d.PageCount));

    private async Task ScaleAsync(IPdfDocumentSession s, MeasureScale? proposed)
    {
        int page = CurrentPage(s);
        var current = proposed ?? await _ctx.Measures.GetScaleAsync(s.Current.GetSharedBytes(), page) ?? MeasureScale.Default;
        int pageCount = await PageCountAsync(s);
        var dlg = new ScaleDialog(_loc, current, page, pageCount);
        var ok = Ui.Show(Dialogs, dlg) == true;
        if (dlg.CalibrateRequested && _calibrateTool is not null)
        {
            _ctx.Shell?.SetStatus(_loc["Annot.Measure.Calibrate.Hint"]);
            Activate(_calibrateTool);
            return;
        }
        if (!ok || dlg.Result is not { } scale) return;
        IReadOnlyCollection<int> pages = dlg.AllPages ? Enumerable.Range(0, pageCount).ToList() : new[] { page };
        bool update = dlg.UpdateExisting;
        await _ctx.RunAsync(s, "Annot.Undo.MeasureScale", (pdf, ct) => _ctx.Measures.SetScaleAsync(pdf, pages, scale, update, ct), pages: pages.ToList());
        _ctx.Shell?.SetStatus(_loc.Format("Annot.Measure.Scale.Done", scale.Ratio(_loc.CurrentCulture)));
    }

    private async Task CalibratedAsync(IPdfDocumentSession s, int page, PdfPoint a, PdfPoint b)
    {
        _ctx.Tools?.ActivateDefault();
        double len = MeasureGeometry.Length(a, b);
        if (len < 3) return;
        var current = await _ctx.Measures.GetScaleAsync(s.Current.GetSharedBytes(), page) ?? MeasureScale.Default;
        var dlg = new CalibrateDialog(_loc, len, current.RealUnit);
        if (Ui.Show(Dialogs, dlg) != true || dlg.Result is not { } scale) return;
        await ScaleAsync(s, scale with { Decimals = current.Decimals });
    }

    private async Task ExportMeasurementsAsync(IPdfDocumentSession s)
    {
        var items = await _ctx.Measures.GetMeasurementsAsync(s.Current.GetSharedBytes());
        if (items.Count == 0) { Dialogs?.ShowMessage(_loc["Annot.Measure.Export.None"]); return; }
        var path = Dialogs?.SaveFile(_loc["Cmd.Measure.Export"], new[] { new FileFilter(_loc["Annot.Filter.Csv"], "*.csv") },
            Path.GetFileNameWithoutExtension(s.DisplayName) + ".csv");
        if (path is null) return;
        await WriteFile(path, _ctx.Measures.ExportCsv(items));
        _ctx.Shell?.SetStatus(_loc.Format("Annot.Export.Done", Path.GetFileName(path)));
    }
}

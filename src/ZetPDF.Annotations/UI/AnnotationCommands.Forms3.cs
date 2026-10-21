using System.Windows;
using ZetPDF.Annotations.UI.Dialogs;
using ZetPDF.Annotations.UI.Tools;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI;

/// <summary>Polecenia etapu 3: kalkulator księgowy, pole kodu kreskowego, skrypty formularza.</summary>
internal sealed partial class AnnotationCommands
{
    private CalculatorWindow? _calculator;
    private ClickTool? _calcInsertTool;
    private string _calcInsertText = string.Empty;

    private void RegisterStage3Forms()
    {
        // ---------------------------------------------------------------- kalkulator księgowy
        _calcInsertTool = new ClickTool(_ctx, CommandIds.Measure.Calculator, (s, page, at) => InsertCalcResult(s, page, at), () => new Size(80, 16));
        _registry.Register(CommandIds.Measure.Calculator, new RelayCmd(_ => ShowCalculator()));
        RegisterBarcode();
        RegisterScripts();
    }

    private void ShowCalculator()
    {
        if (_calculator is { IsLoaded: true })
        {
            _calculator.Activate();
            return;
        }
        _calculator = new CalculatorWindow(_loc, () => _ctx.ActiveView?.TextSelection?.Text, text =>
        {
            if (!CanAnnotate() || _calcInsertTool is null) { _ctx.Shell?.SetStatus(_loc["Annot.Calc.NoDocument"]); return; }
            _calcInsertText = text;
            _ctx.Shell?.SetStatus(_loc["Annot.Calc.InsertHint"]);
            Activate(_calcInsertTool);
        });
        _calculator.Closed += (_, _) => _calculator = null;
        _calculator.Show();
    }

    private Task InsertCalcResult(IPdfDocumentSession s, int page, PdfPoint at)
    {
        _ctx.Tools?.ActivateDefault();
        var style = _ctx.Settings.StyleFor(CommandIds.Comment.Typewriter);
        return _ctx.AddAsync(s, new AnnotationSpec
        {
            Kind = AnnotationKind.Typewriter,
            PageIndex = page,
            Rect = new PdfRect(at.X, at.Y, at.X, at.Y),
            Contents = _calcInsertText,
            Color = style.Color,
            Opacity = style.Opacity,
            TextStyle = new TextStyle { FontFamily = style.FontFamily, FontSize = style.FontSize, Color = style.Color },
        });
    }

    private void RegisterBarcode()
    {
        var tool = new DragTool(_ctx, CommandIds.Form.BarcodeField, DragShape.Rectangle, CreateBarcodeAsync, new Size(120, 120));
        Tool(tool, DocumentPermissions.ModifyContent);
    }

    private async Task CreateBarcodeAsync(IPdfDocumentSession s, int page, PdfPoint a, PdfPoint b)
    {
        _ctx.Tools?.ActivateDefault();
        var rect = PdfRect.FromPoints(a, b);
        var bytes = s.Current.GetBytes();
        var sources = await _ctx.Forms.GetBarcodeSourceNamesAsync(bytes);
        var dlg = new BarcodeFieldDialog(_loc, new Forms.BarcodeOptions(), sources);
        if (Ui.Show(Dialogs, dlg) != true) return;
        var options = dlg.Result;
        var existing = (await _ctx.Forms.GetFieldsAsync(bytes)).Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        int n = 1;
        while (existing.Contains("Barcode" + n)) n++;
        var spec = new FormFieldSpec { Kind = FormFieldKind.Text, Name = "Barcode" + n, PageIndex = page, Rect = rect, Tooltip = _loc["Cmd.Form.BarcodeField"] };
        if (await _ctx.RunAsync(s, "Annot.Undo.Barcode", (pdf, ct) => _ctx.Forms.CreateBarcodeFieldAsync(pdf, spec, options, ct),
                DocumentChangeKind.FormFields | DocumentChangeKind.Annotations, DocumentPermissions.ModifyContent, new[] { page }))
            _ctx.Selection = new SelectedItem(s, null, spec.Name, page, rect);
    }

    private void RegisterScripts()
    {
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Annotations.UI.Tools;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI;

/// <summary>
/// Integracja z widokami: nakładki modułu (zaznaczenie komentarza – 100, wyróżnienie pól – 200) i wypełnianie formularza
/// narzędziem Ręka: kliknięcie w pole otwiera edytor w miejscu (tekst, lista, kalendarz), pola wyboru/opcje przełącza,
/// Tab przechodzi do następnego pola wg kolejności tabulacji. Każda zmiana wartości = polecenie z Cofnij/Ponów.
/// Widok (Viewer) nie jest modyfikowany – zdarzenia myszy przechwytujemy na <see cref="IDocumentView.Element"/>.
/// </summary>
internal sealed class FormFillController
{
    private readonly AnnotContext _ctx;
    private readonly SelectAnnotationTool _select;
    private readonly ConditionalWeakTable<IDocumentView, object> _attached = new();

    public FormFillController(AnnotContext ctx, SelectAnnotationTool select)
    {
        _ctx = ctx;
        _select = select;
    }

    public void Start()
    {
        if (_ctx.Viewer is not { } viewer) return;
        viewer.ActiveViewChanged += (_, v) => Attach(v);
        Attach(viewer.ActiveView);
    }

    private void Attach(IDocumentView? view)
    {
        if (view is null || _attached.TryGetValue(view, out _)) return;
        _attached.Add(view, new object());
        view.AddOverlay(new DelegateOverlay(100, (dc, page, t) => SelectAnnotationTool.RenderSelection(_ctx, _select, dc, page, t, view)));
        view.AddOverlay(new DelegateOverlay(200, (dc, page, t) => RenderFields(view, dc, page, t), () => _ctx.Settings.HighlightFields));
        view.Element.PreviewMouseLeftButtonDown += (_, e) => OnMouseDown(view, e);
        view.Session.Changed += (_, _) => { Prefetch(view); view.InvalidateOverlays(); };
        Prefetch(view);
    }

    /// <summary>Lista widżetów i komentarzy wczytywana z wyprzedzeniem (pierwsze kliknięcie w pole działa od razu).</summary>
    private void Prefetch(IDocumentView view)
    {
        _ = _ctx.GetWidgetsAsync(view.Session).ContinueWith(_ => view.InvalidateOverlays(), TaskScheduler.FromCurrentSynchronizationContext());
        _ = _ctx.GetAnnotationsAsync(view.Session);
    }

    private bool FillMode => _ctx.Tools is not { } t || t.IsActive(CommandIds.Tool.Hand) || t.IsActive(CommandIds.Tool.SelectText);

    private void RenderFields(IDocumentView view, DrawingContext dc, int page, PageTransform t)
    {
        var widgets = _ctx.CachedWidgets(view.Session);
        if (widgets is null) return;
        var fill = Ui.Resource(ThemeKeys.ViewerFormFieldHighlight);
        if (fill is null) return;
        foreach (var w in widgets)
            if (w.Page == page && w.Kind != FormFieldKind.PushButton) dc.DrawRectangle(fill, null, t.ToView(w.Rect));
    }

    private (string Name, FormFieldKind Kind, int Page, PdfRect Rect, string? OnState)? Hit(IDocumentView view, Point viewPoint)
    {
        if (!view.TryHitPage(viewPoint, out int page, out var p)) return null;
        var widgets = _ctx.CachedWidgets(view.Session);
        if (widgets is null) return null;
        for (int i = widgets.Count - 1; i >= 0; i--)
            if (widgets[i].Page == page && widgets[i].Rect.Contains(p)) return widgets[i];
        return null;
    }

    private void OnMouseDown(IDocumentView view, MouseButtonEventArgs e)
    {
        if (!FillMode || e.ClickCount > 1) return;
        var hit = Hit(view, e.GetPosition(view.Element));
        if (hit is not { } h) return;
        if (!view.Session.Can(DocumentPermissions.FillForms) && !view.Session.Can(DocumentPermissions.Annotate)) return;
        e.Handled = true;
        _ = ActivateAsync(view, h.Name, h.OnState);
    }

    /// <summary>Uaktywnia pole (edytor albo przełączenie).</summary>
    public async Task ActivateAsync(IDocumentView view, string name, string? onState)
    {
        var session = view.Session;
        var fields = await _ctx.Forms.GetFieldsAsync(session.Current.GetBytes());
        var f = fields.FirstOrDefault(x => x.FullName == name);
        if (f is null) return;
        if (f.ReadOnly) { _ctx.Shell?.SetStatus(_ctx.Loc["Annot.Form.ReadOnly"]); return; }
        var widgets = await _ctx.GetWidgetsAsync(session);
        var w = widgets.FirstOrDefault(x => x.Name == name && (onState is null || x.OnState == onState));
        if (w.Name is null) return;
        view.ScrollIntoView(w.Page, w.Rect);
        var t = view.GetPageTransform(w.Page);
        switch (f.Kind)
        {
            case FormFieldKind.CheckBox:
                await SetAsync(session, name, f.Value == "Off" || string.IsNullOrEmpty(f.Value) ? (w.OnState ?? "Yes") : "Off");
                return;
            case FormFieldKind.RadioButton:
                if (w.OnState is { } on && f.Value != on) await SetAsync(session, name, on);
                return;
            case FormFieldKind.PushButton:
                await PressButtonAsync(session, name);
                return;
            case FormFieldKind.Signature:
                if (!(_ctx.Registry?.TryExecute(CommandIds.Sign.PlaceSignature) ?? false))
                    _ctx.Dialogs?.ShowMessage(_ctx.Loc["Annot.Form.SignatureHint"]);
                return;
        }
        if (t is null) return;
        var rect = t.ToView(w.Rect);
        void Done(EditorExit exit, string value)
        {
            if (exit == EditorExit.Cancel) return;
            _ = CommitAndMove(view, f, value, exit);
        }
        switch (f.Kind)
        {
            case FormFieldKind.ComboBox:
                InlineEditor.EditChoice(view, rect, f.Options, DisplayOf(f), editable: false, Done);
                break;
            case FormFieldKind.ListBox:
                InlineEditor.EditChoice(view, new Rect(rect.X, rect.Y, rect.Width, Math.Max(rect.Height, 60)), f.Options, DisplayOf(f), editable: false, Done);
                break;
            case FormFieldKind.Date:
                var spec = await _ctx.Forms.DescribeAsync(session.Current.GetBytes(), name);
                var fmt = spec.DateFormat ?? "yyyy-MM-dd";
                DateTime? cur = DateTime.TryParseExact(f.Value, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                InlineEditor.EditDate(view, rect, cur, fmt, Done);
                break;
            default:
                InlineEditor.EditText(view, rect, f.Value, f.Multiline, Math.Max(10, rect.Height * (f.Multiline ? 0.25 : 0.6)), Done);
                break;
        }
    }

    private static string DisplayOf(FormFieldInfo f)
    {
        int i = f.ExportValues.ToList().IndexOf(f.Value);
        return i >= 0 && i < f.Options.Count ? f.Options[i] : f.Value;
    }

    private async Task CommitAndMove(IDocumentView view, FormFieldInfo f, string value, EditorExit exit)
    {
        if (value != f.Value && value != DisplayOf(f)) await SetAsync(view.Session, f.FullName, value);
        if (exit is not (EditorExit.TabNext or EditorExit.TabPrevious)) return;
        var fields = (await _ctx.Forms.GetFieldsAsync(view.Session.Current.GetBytes()))
            .Where(x => !x.ReadOnly && x.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature))
            .OrderBy(x => x.PageIndex).ThenBy(x => x.TabOrder).ToList();
        if (fields.Count == 0) return;
        int i = fields.FindIndex(x => x.FullName == f.FullName);
        int next = exit == EditorExit.TabNext ? (i + 1) % fields.Count : (i - 1 + fields.Count) % fields.Count;
        var target = fields[next];
        if (target.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton)
        {
            // pola wyboru nie mają edytora – zaznaczamy je tylko w widoku
            var w = (await _ctx.GetWidgetsAsync(view.Session)).FirstOrDefault(x => x.Name == target.FullName);
            if (w.Name is not null) { view.ScrollIntoView(w.Page, w.Rect); _ctx.Selection = new SelectedItem(view.Session, null, w.Name, w.Page, w.Rect, w.OnState); }
            return;
        }
        await ActivateAsync(view, target.FullName, null);
    }

    private async Task<bool> SetAsync(IPdfDocumentSession session, string name, string value)
    {
        var report = new Forms.Scripting.ScriptReport();
        var policy = _ctx.ScriptPolicyFor(session);
        bool ok = await _ctx.RunAsync(session, "Annot.Undo.FormFill", (pdf, ct) => _ctx.Forms.SetValuesAsync(pdf, new Dictionary<string, string> { [name] = value }, policy, report, ct),
            DocumentChangeKind.FormFields, DocumentPermissions.FillForms);
        _ctx.ShowScriptReport(report);
        return ok;
    }

    private async Task PressButtonAsync(IPdfDocumentSession session, string name)
    {
        var spec = await _ctx.Forms.DescribeAsync(session.Current.GetBytes(), name);
        switch (spec.ButtonAction)
        {
            case FormButtonAction.ResetForm:
                var policy = _ctx.ScriptPolicyFor(session);
                await _ctx.RunAsync(session, "Annot.Undo.FormReset", (pdf, ct) => _ctx.Forms.ResetAsync(pdf, null, policy, ct), DocumentChangeKind.FormFields, DocumentPermissions.FillForms);
                break;
            case FormButtonAction.OpenUrl when Uri.TryCreate(spec.ButtonUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto":
                if (_ctx.Dialogs?.Confirm(_ctx.Loc.Format("Annot.Form.OpenUrlConfirm", uri.AbsoluteUri)) == true)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                break;
        }
    }
}

using System.Text;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Editing.PdfA;

namespace ZetPDF.Editing.Ui.PdfA;

/// <summary>Polecenia Convert.CheckPdfA i Convert.ToPdfA.</summary>
internal sealed class PdfACommands
{
    private readonly EditingContext _ctx;

    public PdfACommands(EditingContext ctx) => _ctx = ctx;

    private ILocalizationService L => _ctx.Loc;

    public void Register(ICommandRegistry registry)
    {
        registry.Register(CommandIds.Convert.CheckPdfA, new ActiveDocumentCommand(_ctx.Documents, _ctx.Dialogs, CheckAsync));
        // Wynik jest bez szyfrowania, więc polecenie nie może omijać ograniczeń pliku chronionego.
        registry.Register(CommandIds.Convert.ToPdfA, new ActiveDocumentCommand(_ctx.Documents, _ctx.Dialogs, ConvertAsync, DocumentPermissions.ModifyContent | DocumentPermissions.CopyContent));
    }

    private string BaseName(IPdfDocumentSession s) => Path.GetFileNameWithoutExtension(s.FilePath ?? s.DisplayName);

    private async Task CheckAsync(IPdfDocumentSession session)
    {
        var service = _ctx.Get<IPdfAService>();
        var pdf = session.Current.GetSharedBytes();
        bool encrypted = session.Current.Encryption is not null;
        PdfACheckReport? report = null;
        if (!await _ctx.RunAsync("Edit.PdfA.Check.Progress", async (p, ct) => report = await service.CheckAsync(pdf, encrypted, p, ct)) || report is null) return;
        var window = new PdfACheckWindow(L, report, () => SaveReportAsync(session, report));
        _ctx.ShowDialog(window);
        _ctx.Status(L.Format("Edit.PdfA.Summary", report.Passed, report.Failed, report.NotChecked));
    }

    private async Task SaveReportAsync(IPdfDocumentSession session, PdfACheckReport report)
    {
        var path = _ctx.Dialogs?.SaveFile(L["Edit.PdfA.Report.Save"],
            new[] { new FileFilter(L["Edit.Filter.Html"], "*.html", "*.htm"), new FileFilter(L["Edit.Filter.Text"], "*.txt") },
            BaseName(session) + "_PDFA.html", session.FilePath is { } fp ? Path.GetDirectoryName(fp) : null);
        if (path is null) return;
        bool html = !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        var text = PdfAChecker.BuildReport(report, L, session.FilePath is { } f ? Path.GetFileName(f) : session.DisplayName, html);
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        if (_ctx.Files is { } files) await files.WriteAllBytesSafeAsync(path, bytes);
        else await File.WriteAllBytesAsync(path, bytes);
        _ctx.Status(L.Format("Edit.Convert.Saved", path));
    }

    private async Task ConvertAsync(IPdfDocumentSession session)
    {
        var service = _ctx.Get<IPdfAService>();
        var pdf = session.Current.GetSharedBytes();
        PdfAConversionPlan? plan = null;
        if (!await _ctx.RunAsync("Edit.PdfA.Plan.Progress", async (_, ct) => plan = await service.PlanAsync(pdf, ct)) || plan is null) return;
        var dlg = new PdfAConvertDialog(L, plan, session.Current.Encryption is not null);
        if (_ctx.ShowDialog(dlg) != true) return;
        var path = _ctx.Dialogs?.SaveFile(L["Edit.PdfA.Convert.Save"], new[] { FileFilter.Pdf(L) }, BaseName(session) + "_PDFA.pdf", session.FilePath is { } fp ? Path.GetDirectoryName(fp) : null);
        if (path is null) return;
        if (session.FilePath is { } current && string.Equals(Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
        {
            _ctx.Dialogs?.ShowMessage(L["Edit.PdfA.Convert.SameFile"], L["Edit.PdfA.Convert.Title"], MessageSeverity.Warning);
            return;
        }
        var options = new PdfAConvertOptions { AllowRasterize = dlg.AllowRasterize, AllowContentRemoval = true };
        PdfAConvertResult? result = null;
        if (!await _ctx.RunAsync("Edit.PdfA.Convert.Progress", async (p, ct) => result = await service.ConvertAsync(pdf, options, p, ct)) || result is null) return;
        if (result.Pdf is null)
        {
            _ctx.Dialogs?.ShowMessage(FailureText(result), L["Edit.PdfA.Convert.Title"], MessageSeverity.Warning);
            return;
        }
        if (_ctx.Files is { } files) await files.WriteAllBytesSafeAsync(path, result.Pdf);
        else await File.WriteAllBytesAsync(path, result.Pdf);
        _ctx.Dialogs?.ShowMessage(L.Format("Edit.PdfA.Convert.Done", path), L["Edit.PdfA.Convert.Title"]);
        _ctx.Status(L.Format("Edit.Convert.Saved", path));
    }

    private string FailureText(PdfAConvertResult result)
    {
        if (result.Reason == PdfANotConverted.NeedsRasterize) return L.Format("Edit.PdfA.Convert.NeedsRasterize", result.Plan.PagesToRasterize.Count);
        var failed = result.After?.Results.Where(r => r.Status == PdfARuleStatus.Failed).Select(r => L["Edit.PdfA.Rule." + r.Rule]) ?? Array.Empty<string>();
        return L.Format("Edit.PdfA.Convert.RulesFailing", string.Join(", ", failed));
    }
}

/// <summary>Wyniki sprawdzania: reguły ze statusem, wyraźne zastrzeżenie, zapis raportu.</summary>
internal sealed class PdfACheckWindow : DialogBase
{
    public PdfACheckWindow(ILocalizationService loc, PdfACheckReport report, Func<Task> saveReport) : base(loc, "Edit.PdfA.Check.Title", 640)
    {
        var disclaimer = Ui.Label(loc, "Edit.PdfA.Disclaimer");
        disclaimer.FontWeight = FontWeights.SemiBold;
        disclaimer.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusWarning);
        var summary = Ui.Text(loc.Format("Edit.PdfA.Summary", report.Passed, report.Failed, report.NotChecked));

        var list = new ListBox { Height = 380, Margin = new Thickness(0, 6, 0, 0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(list, loc["Edit.PdfA.Check.Title"]);
        foreach (var r in report.Results.OrderBy(r => r.Status switch { PdfARuleStatus.Failed => 0, PdfARuleStatus.NotChecked => 1, PdfARuleStatus.Info => 2, _ => 3 }))
        {
            var item = new ListBoxItem { Content = Row(loc, r), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(item, loc["Edit.PdfA.Rule." + r.Rule] + ": " + loc["Edit.PdfA.Status." + r.Status]);
            list.Items.Add(item);
        }
        var root = new StackPanel();
        root.Children.Add(disclaimer);
        root.Children.Add(summary);
        root.Children.Add(list);
        SetBody(root);
        Buttons.Children.Add(Ui.Button(loc, "Edit.PdfA.Report.SaveButton", async (_, _) =>
        {
            try { await saveReport(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); }
        }));
        Buttons.Children.Add(Ui.Button(loc, "Edit.PdfA.Check.Close", (_, _) => DialogResult = false, isDefault: true, isCancel: true));
    }

    private static FrameworkElement Row(ILocalizationService loc, PdfARuleResult r)
    {
        var name = Ui.Text(loc["Edit.PdfA.Rule." + r.Rule] + " – " + loc["Edit.PdfA.Status." + r.Status]);
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(0);
        name.SetResourceReference(TextBlock.ForegroundProperty, r.Status switch
        {
            PdfARuleStatus.Failed => ThemeKeys.StatusError,
            PdfARuleStatus.Passed => ThemeKeys.StatusSuccess,
            PdfARuleStatus.NotChecked => ThemeKeys.StatusWarning,
            _ => ThemeKeys.StatusInfo,
        });
        var detail = Ui.Text(PdfAChecker.Detail(r, loc), secondary: true);
        detail.Margin = new Thickness(0, 0, 0, 2);
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(name);
        panel.Children.Add(detail);
        return panel;
    }
}

/// <summary>Zgoda przed konwersją: co się zmieni i czy wolno zamienić strony na obraz. Bez zgody na rasteryzację, gdy jest potrzebna, konwersja jest zablokowana.</summary>
internal sealed class PdfAConvertDialog : DialogBase
{
    private readonly PdfAConversionPlan _plan;
    private readonly CheckBox _raster;

    public PdfAConvertDialog(ILocalizationService loc, PdfAConversionPlan plan, bool encrypted) : base(loc, "Edit.PdfA.Convert.Title", 560)
    {
        _plan = plan;
        var root = new StackPanel();
        var warning = Ui.Label(loc, "Edit.PdfA.Convert.Warning");
        warning.FontWeight = FontWeights.SemiBold;
        warning.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusWarning);
        root.Children.Add(warning);
        void Line(string text) => root.Children.Add(Ui.Text("•  " + text));
        if (encrypted) Line(loc["Edit.PdfA.Plan.Encryption"]);
        if (plan.ActionsToRemove > 0) Line(loc.Format("Edit.PdfA.Plan.Actions", plan.ActionsToRemove));
        if (plan.Attachments > 0) Line(loc.Format("Edit.PdfA.Plan.Attachments", plan.Attachments));
        if (plan.AnnotationsToRemove > 0) Line(loc.Format("Edit.PdfA.Plan.Annotations", plan.AnnotationsToRemove));
        if (plan.HasXfa) Line(loc["Edit.PdfA.Plan.Xfa"]);
        if (plan.IsSigned) Line(loc["Edit.PdfA.Plan.Signed"]);
        if (plan.FontsToEmbed.Count > 0) Line(loc.Format("Edit.PdfA.Plan.Fonts", string.Join(", ", plan.FontsToEmbed)));
        Line(loc["Edit.PdfA.Plan.Metadata"]);

        _raster = Ui.Check(loc, "Edit.PdfA.Plan.RasterizeAllow", false);
        if (plan.PagesToRasterize.Count > 0)
        {
            root.Children.Add(Ui.Text(loc.Format("Edit.PdfA.Plan.Rasterize", plan.PagesToRasterize.Count, PdfAChecker.Pages(plan.PagesToRasterize))));
            root.Children.Add(_raster);
        }
        root.Children.Add(Ui.Label(loc, "Edit.PdfA.Disclaimer", secondary: true));
        SetBody(root);
        AddOkCancel("Edit.PdfA.Convert.Button");
    }

    public bool AllowRasterize => _raster.IsChecked == true;

    protected override bool Validate()
    {
        if (_plan.PagesToRasterize.Count > 0 && !AllowRasterize) { ShowError(L["Edit.PdfA.Plan.RasterizeRequired"]); return false; }
        return true;
    }
}

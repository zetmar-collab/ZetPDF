
using System.Runtime.CompilerServices;
using System.Text;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Accessibility;

namespace ZetPDF.Editing.Ui.Accessibility;

/// <summary>
/// Stan sprawdzania ułatwień dostępu (wyniki per dokument) i operacje używane przez polecenia i panel:
/// sprawdzenie, naprawy (polecenia z Cofnij/Ponów), raport, okno właściwości.
/// </summary>
internal sealed class AccessibilityController
{
    public const string PanelId = "Panel.Accessibility";

    private readonly EditingContext _ctx;
    private readonly AccessibilityService _service;
    private readonly ConditionalWeakTable<IPdfDocumentSession, AccessReport> _reports = new();
    private readonly HashSet<IPdfDocumentSession> _watched = new(ReferenceEqualityComparer.Instance);

    public AccessibilityController(EditingContext ctx, AccessibilityService service)
    {
        _ctx = ctx;
        _service = service;
        ctx.Documents.DocumentClosed += (_, s) => { _watched.Remove(s); _reports.Remove(s); };
    }

    private ILocalizationService L => _ctx.Loc;

    /// <summary>Zmiana wyników (dla panelu).</summary>
    public event EventHandler<IPdfDocumentSession>? ReportChanged;

    public AccessReport? GetReport(IPdfDocumentSession? session) => session is not null && _reports.TryGetValue(session, out var r) ? r : null;

    public async Task<AccessReport> CheckAsync(IPdfDocumentSession session, bool showPanel = true)
    {
        var report = await _service.CheckAsync(session.Current.GetSharedBytes());
        _reports.AddOrUpdate(session, report);
        if (_watched.Add(session)) session.Changed += OnSessionChanged;
        ReportChanged?.Invoke(this, session);
        if (showPanel) _ctx.Shell?.ShowPanel(PanelId);
        _ctx.Status(L.Format("Edit.Access.Summary", report.Passed, report.Failed, report.Manual));
        return report;
    }

    private async void OnSessionChanged(object? sender, DocumentChangedEventArgs e)
    {
        // po zmianie (naprawa, Cofnij, inna edycja) wyniki są odświeżane w tle
        if (sender is not IPdfDocumentSession s || !_reports.TryGetValue(s, out _)) return;
        try { await CheckAsync(s, showPanel: false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ctx.Dialogs?.ShowError(ex); }
    }

    /// <summary>Propozycja tytułu: istniejący, nazwa pliku bez rozszerzenia.</summary>
    public static string SuggestTitle(IPdfDocumentSession s) => Path.GetFileNameWithoutExtension(s.FilePath ?? s.DisplayName);

    /// <summary>Propozycja języka: język interfejsu (pl-PL / en-US).</summary>
    public string SuggestLanguage() => L.CurrentCulture.TwoLetterISOLanguageName == "pl" ? "pl-PL" : "en-US";

    /// <summary>Naprawa reguł; tytuł i język – przez okno właściwości (użytkownik zatwierdza wartości).</summary>
    public async Task FixAsync(IPdfDocumentSession session, IReadOnlyCollection<AccessRule> rules)
    {
        var report = GetReport(session) ?? await CheckAsync(session, showPanel: false);
        var fixable = rules.Where(r => report.Results.Any(x => x.Rule == r && x.CanFix)).ToList();
        if (fixable.Count == 0) return;
        var options = new AccessFixOptions();
        if (fixable.Contains(AccessRule.DocTitle) || fixable.Contains(AccessRule.Language))
        {
            var props = await AskPropertiesAsync(session, includeFigures: false);
            if (props is null) return;
            options = new AccessFixOptions { Title = props.Title, Language = props.Language };
        }
        await _ctx.ExecuteAsync(session, "Edit.Undo.AccessFix", (pdf, _, ct) => _service.FixAsync(pdf, fixable, options, ct),
            DocumentChangeKind.Metadata | DocumentChangeKind.FormFields | DocumentChangeKind.Annotations, DocumentPermissions.ModifyContent);
    }

    public async Task FixAllAsync(IPdfDocumentSession session)
    {
        var report = GetReport(session) ?? await CheckAsync(session);
        await FixAsync(session, report.Results.Where(r => r.CanFix).Select(r => r.Rule).ToList());
    }

    public void Explain(AccessCheckResult result) =>
        _ctx.Dialogs?.ShowMessage(L[result.ExplainKey], L[result.NameKey], result.Status == AccessCheckStatus.Failed ? MessageSeverity.Warning : MessageSeverity.Info);

    private async Task<AccessProperties?> AskPropertiesAsync(IPdfDocumentSession session, bool includeFigures)
    {
        var pdf = session.Current.GetSharedBytes();
        var meta = await _ctx.Get<IMetadataService>().GetAsync(pdf);
        string lang = await Task.Run(() => Core.Documents.PdfSharpEdit.Read(pdf, d => d.Internals.Catalog.Elements.GetString("/Lang")));
        var figures = includeFigures ? await _service.GetFiguresAsync(pdf) : Array.Empty<AccessFigure>();
        var dlg = new AccessPropertiesDialog(L,
            string.IsNullOrWhiteSpace(meta.Title) ? SuggestTitle(session) : meta.Title,
            string.IsNullOrWhiteSpace(lang) ? SuggestLanguage() : lang,
            figures, includeFigures);
        return _ctx.ShowDialog(dlg) == true ? dlg.Result : null;
    }

    /// <summary>Access.SetProperties – tytuł, język, /DisplayDocTitle i tekst alternatywny figur (gdy dokument otagowany).</summary>
    public async Task SetPropertiesAsync(IPdfDocumentSession session)
    {
        var props = await AskPropertiesAsync(session, includeFigures: true);
        if (props is null) return;
        await _ctx.ExecuteAsync(session, "Edit.Undo.AccessProperties", (pdf, _, ct) => _service.SetPropertiesAsync(pdf, props, ct), DocumentChangeKind.Metadata, DocumentPermissions.ModifyContent);
    }

    /// <summary>Access.Report – raport HTML/TXT (sprawdza dokument, jeśli nie ma aktualnych wyników).</summary>
    public async Task ReportAsync(IPdfDocumentSession session)
    {
        var report = GetReport(session) ?? await CheckAsync(session);
        var name = Path.GetFileNameWithoutExtension(session.FilePath ?? session.DisplayName);
        var path = _ctx.Dialogs?.SaveFile(L["Edit.Access.Report.Save"],
            new[] { new FileFilter(L["Edit.Filter.Html"], "*.html", "*.htm"), new FileFilter(L["Edit.Filter.Text"], "*.txt") },
            name + "_" + L["Edit.Access.Report.FileSuffix"] + ".html",
            session.FilePath is { } fp ? Path.GetDirectoryName(fp) : null);
        if (path is null) return;
        bool html = !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        var text = AccessibilityService.BuildReport(report, L, session.FilePath is { } f ? Path.GetFileName(f) : session.DisplayName, html);
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        if (_ctx.Files is { } files) await files.WriteAllBytesSafeAsync(path, bytes);
        else await File.WriteAllBytesAsync(path, bytes);
        _ctx.Status(L.Format("Edit.Convert.Saved", path));
    }

    public string Detail(AccessCheckResult r) => r.DetailArgs.Count == 0 ? L[r.DetailKey] : L.Format(r.DetailKey, r.DetailArgs.ToArray());
}

/// <summary>Podpięcie poleceń Access.* i panelu „Sprawdzanie ułatwień dostępu”.</summary>
internal static class AccessibilityCommands
{
    public static AccessibilityController Register(EditingContext ctx, ICommandRegistry? registry, IPanelRegistry? panels)
    {
        var controller = new AccessibilityController(ctx, ctx.Get<AccessibilityService>());
        if (registry is not null)
        {
            registry.Register(CommandIds.Access.FullCheck, new ActiveDocumentCommand(ctx.Documents, ctx.Dialogs, s => controller.CheckAsync(s)));
            registry.Register(CommandIds.Access.Report, new ActiveDocumentCommand(ctx.Documents, ctx.Dialogs, controller.ReportAsync));
            registry.Register(CommandIds.Access.SetProperties, new ActiveDocumentCommand(ctx.Documents, ctx.Dialogs, controller.SetPropertiesAsync, DocumentPermissions.ModifyContent));
        }
        panels?.Register(new PanelDescriptor(AccessibilityController.PanelId, "Edit.Access.Panel.Title", "", 60, _ => new AccessibilityPanel(ctx, controller)));
        return controller;
    }


}

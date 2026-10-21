using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI;

/// <summary>Zaznaczona adnotacja lub widżet pola (narzędzie Wybierz adnotację, panel Komentarze).</summary>
internal sealed record SelectedItem(IPdfDocumentSession Session, AnnotationId? Annotation, string? FieldName, int PageIndex, PdfRect Rect, string? OnState = null);

/// <summary>
/// Wspólny kontekst interfejsu modułu: usługi, wykonywanie poleceń dokumentu (Cofnij/Ponów), pamięć podręczna
/// listy adnotacji i widżetów dla bieżącego zrzutu, styl narzędzi, zaznaczenie.
/// </summary>
internal sealed class AnnotContext
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<Guid, (long Version, Task<IReadOnlyList<AnnotationInfo>> Task)> _annots = new();
    private readonly Dictionary<Guid, (long Version, Task<List<(string Name, FormFieldKind Kind, int Page, PdfRect Rect, string? OnState)>> Task)> _widgets = new();
    private AnnotationSettings? _settings;
    private SelectedItem? _selection;

    public AnnotContext(IServiceProvider services)
    {
        _services = services;
        Loc = services.GetRequiredService<ILocalizationService>();
        Documents = services.GetRequiredService<IDocumentManager>();
        Annotations = services.GetRequiredService<AnnotationService>();
        Forms = services.GetRequiredService<FormService>();
        Attachments = services.GetRequiredService<AttachmentService>();
        Measures = services.GetRequiredService<MeasureService>();
    }

    public ILocalizationService Loc { get; }
    public IDocumentManager Documents { get; }
    public AnnotationService Annotations { get; }
    public FormService Forms { get; }
    public AttachmentService Attachments { get; }
    public MeasureService Measures { get; }
    public IViewerService? Viewer => _services.GetService<IViewerService>();
    public IToolService? Tools => _services.GetService<IToolService>();
    public IDialogService? Dialogs => _services.GetService<IDialogService>();
    public IShellService? Shell => _services.GetService<IShellService>();
    public ISettingsService? SettingsService => _services.GetService<ISettingsService>();
    public IAppPaths? Paths => _services.GetService<IAppPaths>();
    public ICommandRegistry? Registry => _services.GetService<ICommandRegistry>();
    public IPageOperations? Pages => _services.GetService<IPageOperations>();
    public IServiceProvider Services => _services;

    public string Author => SettingsService?.Current.AuthorName is { Length: > 0 } a ? a : Environment.UserName;

    public AnnotationSettings Settings => _settings ??= SettingsService?.GetModuleSettings<AnnotationSettings>(AnnotationSettings.Key) ?? new AnnotationSettings();

    public void SaveSettings()
    {
        if (_settings is not null) SettingsService?.SetModuleSettings(AnnotationSettings.Key, _settings);
    }

    public IDocumentView? ActiveView => Viewer?.ActiveView;

    // ------------------------------------------------------------------ zaznaczenie

    public SelectedItem? Selection
    {
        get => _selection;
        set
        {
            if (Equals(_selection, value)) return;
            _selection = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            ActiveView?.InvalidateOverlays();
        }
    }

    public event EventHandler? SelectionChanged;

    // ------------------------------------------------------------------ dane

    public Task<IReadOnlyList<AnnotationInfo>> GetAnnotationsAsync(IPdfDocumentSession session)
    {
        var snap = session.Current;
        if (_annots.TryGetValue(session.Id, out var c) && c.Version == snap.Version && !c.Task.IsFaulted) return c.Task;
        var task = Annotations.GetAnnotationsAsync(snap.GetBytes());
        _annots[session.Id] = (snap.Version, task);
        return task;
    }

    /// <summary>Lista z pamięci, jeśli gotowa dla bieżącego zrzutu (rysowanie nakładek bez czekania).</summary>
    public IReadOnlyList<AnnotationInfo>? CachedAnnotations(IPdfDocumentSession session)
    {
        if (_annots.TryGetValue(session.Id, out var c) && c.Version == session.Current.Version && c.Task.IsCompletedSuccessfully) return c.Task.Result;
        if (System.Threading.SynchronizationContext.Current is not null)
            _ = GetAnnotationsAsync(session).ContinueWith(_ => ActiveView?.InvalidateOverlays(), TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    public Task<List<(string Name, FormFieldKind Kind, int Page, PdfRect Rect, string? OnState)>> GetWidgetsAsync(IPdfDocumentSession session)
    {
        var snap = session.Current;
        if (_widgets.TryGetValue(session.Id, out var c) && c.Version == snap.Version && !c.Task.IsFaulted) return c.Task;
        var task = Forms.GetWidgetsAsync(snap.GetBytes());
        _widgets[session.Id] = (snap.Version, task);
        return task;
    }

    public List<(string Name, FormFieldKind Kind, int Page, PdfRect Rect, string? OnState)>? CachedWidgets(IPdfDocumentSession session)
    {
        if (_widgets.TryGetValue(session.Id, out var c) && c.Version == session.Current.Version && c.Task.IsCompletedSuccessfully) return c.Task.Result;
        if (System.Threading.SynchronizationContext.Current is not null)
            _ = GetWidgetsAsync(session).ContinueWith(_ => ActiveView?.InvalidateOverlays(), TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    // ------------------------------------------------------------------ skrypty formularza (ustawienie dokumentu, nie w pliku)

    private readonly Dictionary<Guid, ZetPDF.Annotations.Forms.Scripting.ScriptPolicy> _scriptPolicies = new();

    public ZetPDF.Annotations.Forms.Scripting.ScriptPolicy ScriptPolicyFor(IPdfDocumentSession session) =>
        _scriptPolicies.TryGetValue(session.Id, out var p) ? p : ZetPDF.Annotations.Forms.Scripting.ScriptPolicy.AfOnly;

    public void SetScriptPolicy(IPdfDocumentSession session, ZetPDF.Annotations.Forms.Scripting.ScriptPolicy policy) => _scriptPolicies[session.Id] = policy;

    /// <summary>Komunikaty skryptów (app.alert, błędy, odrzucone wartości) – w pasku stanu, bez okien wyskakujących.</summary>
    public void ShowScriptReport(ZetPDF.Annotations.Forms.Scripting.ScriptReport report)
    {
        if (report.Rejected.Count > 0)
            Shell?.SetStatus(Loc.Format("Annot.Script.Rejected", string.Join(", ", report.Rejected.Distinct())) + (report.Alerts.Count > 0 ? " " + report.Alerts[0] : ""));
        else if (report.Errors.Count > 0) Shell?.SetStatus(Loc.Format("Annot.Script.Errors", report.Errors.Count, report.Errors[0]));
        else if (report.Alerts.Count > 0) Shell?.SetStatus(Loc.Format("Annot.Script.Alert", report.Alerts[^1]));
    }

    // ------------------------------------------------------------------ skala pomiarów

    private readonly Dictionary<(Guid, int), (long Version, Task<Measure.MeasureScale?> Task)> _scales = new();

    public Task<Measure.MeasureScale?> GetScaleAsync(IPdfDocumentSession session, int page)
    {
        var snap = session.Current;
        if (_scales.TryGetValue((session.Id, page), out var c) && c.Version == snap.Version && !c.Task.IsFaulted) return c.Task;
        var task = Measures.GetScaleAsync(snap.GetBytes(), page);
        _scales[(session.Id, page)] = (snap.Version, task);
        return task;
    }

    /// <summary>Skala strony z pamięci (podgląd narzędzi); gdy nie wczytana – domyślna i wczytanie w tle.</summary>
    public Measure.MeasureScale ScaleFor(IPdfDocumentSession session, int page)
    {
        if (_scales.TryGetValue((session.Id, page), out var c) && c.Version == session.Current.Version && c.Task.IsCompletedSuccessfully)
            return c.Task.Result ?? Measure.MeasureScale.Default;
        if (System.Threading.SynchronizationContext.Current is not null)
            _ = GetScaleAsync(session, page).ContinueWith(_ => ActiveView?.InvalidateOverlays(), TaskScheduler.FromCurrentSynchronizationContext());
        return Measure.MeasureScale.Default;
    }

    // ------------------------------------------------------------------ polecenia

    /// <summary>Wykonuje zmianę dokumentu jako polecenie z Cofnij/Ponów. Błędy → okno błędu. Zwraca, czy coś zmieniono.</summary>
    public async Task<bool> RunAsync(IPdfDocumentSession session, string undoKey, Func<byte[], CancellationToken, Task<byte[]>> transform,
        DocumentChangeKind kind = DocumentChangeKind.Annotations, DocumentPermissions permission = DocumentPermissions.Annotate, IReadOnlyList<int>? pages = null)
    {
        try
        {
            var cmd = new DelegateDocumentCommand(undoKey, (pdf, _, ct) => transform(pdf, ct), kind, permission, pages);
            return await session.ExecuteAsync(cmd);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (Dialogs is { } d) d.ShowError(ex); else throw;
            return false;
        }
    }

    public Task<bool> AddAsync(IPdfDocumentSession session, AnnotationSpec spec) =>
        RunAsync(session, "Annot.Undo.Add", async (pdf, ct) => (await Annotations.AddAsync(pdf, spec with { Author = spec.Author ?? Author }, ct)).Pdf, pages: new[] { spec.PageIndex });

    public string Text(string key) => Loc[key];
}

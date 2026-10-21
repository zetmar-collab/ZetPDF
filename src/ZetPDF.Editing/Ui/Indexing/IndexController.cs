using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Editing.Indexing;

namespace ZetPDF.Editing.Ui.Indexing;

public enum SearchScope { CurrentDocument, OpenDocuments, FolderIndex }

/// <summary>
/// Indeks pełnotekstowy folderu i wyszukiwanie zaawansowane (Index.BuildFolder, Index.Search): budowanie indeksu w tle,
/// wyszukiwanie w bieżącym/otwartych dokumentach lub w indeksie, przejście do trafienia z podświetleniem.
/// </summary>
internal sealed class IndexController
{
    private readonly EditingContext _ctx;
    private readonly Dictionary<string, IPdfDocumentSession> _sessionSources = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<IDocumentView, SearchHighlightOverlay> _overlays = new();
    public const string PanelId = "Panel.AdvancedSearch";
    private AdvancedSearchPanel? _window;
    private string? _pendingQuery;

    public IndexController(EditingContext ctx, FullTextIndex index)
    {
        _ctx = ctx;
        Index = index;
    }

    public FullTextIndex Index { get; }
    private ILocalizationService L => _ctx.Loc;

    public void Register(ICommandRegistry? registry, IPanelRegistry? panels)
    {
        panels?.Register(new PanelDescriptor(PanelId, "Edit.Search.Title", "", 70, _ =>
        {
            _window = new AdvancedSearchPanel(_ctx.Loc, this);
            _window.FocusQuery(_pendingQuery);
            return _window;
        }));
        if (registry is null) return;
        registry.Register(CommandIds.Index.BuildFolder, new AsyncCommand(() => BuildFolderAsync(), _ctx));
        registry.Register(CommandIds.Index.Search, new AsyncCommand(() => { ShowSearch(); return Task.CompletedTask; }, _ctx));
    }

    // ------------------------------------------------------------------ budowanie

    /// <summary>Wybór folderu, opcja podfolderów, budowa/aktualizacja indeksu w oknie postępu. Zwraca indeks albo null.</summary>
    public async Task<FolderIndex?> BuildFolderAsync(string? folder = null)
    {
        folder ??= _ctx.Dialogs?.SelectFolder(L["Edit.Index.ChooseFolder"], _ctx.Documents.ActiveDocument?.FilePath is { } p ? Path.GetDirectoryName(p) : null);
        if (folder is null) return null;
        var answer = _ctx.Dialogs?.AskYesNoCancel(L.Format("Edit.Index.Recursive", folder), L["Edit.Index.Title"]) ?? YesNoCancel.Yes;
        if (answer == YesNoCancel.Cancel) return null;
        IndexBuildResult? result = null;
        if (!await _ctx.RunAsync("Edit.Index.Progress", async (pr, ct) => result = await Index.BuildAsync(folder, answer == YesNoCancel.Yes, pr, ct)) || result is null)
        {
            _ctx.Status(L["Edit.Index.Cancelled"]);
            return null;
        }
        var text = L.Format("Edit.Index.Done", result.Index.Files.Count, result.Indexed, result.Reused, result.Removed);
        if (result.Encrypted.Count > 0) text += "\n\n" + L.Format("Edit.Index.SkippedEncrypted", result.Encrypted.Count, string.Join(", ", result.Encrypted.Take(10)));
        if (result.Failed.Count > 0) text += "\n\n" + L.Format("Edit.Index.SkippedFailed", result.Failed.Count, string.Join(", ", result.Failed.Take(10)));
        _ctx.Dialogs?.ShowMessage(text, L["Edit.Index.Title"]);
        _window?.RefreshIndexes(result.Index.Root);
        return result.Index;
    }

    // ------------------------------------------------------------------ wyszukiwanie

    public void ShowSearch()
    {
        var initial = _ctx.View?.TextSelection?.Text?.Trim();
        _pendingQuery = string.IsNullOrEmpty(initial) || initial.Length > 200 ? null : initial;
        _ctx.Shell?.ShowPanel(PanelId);
        _window?.FocusQuery(_pendingQuery);
    }

    public bool HasActiveDocument => _ctx.Documents.ActiveDocument is not null;
    public int OpenDocumentCount => _ctx.Documents.Documents.Count;

    public async Task<(IReadOnlyList<FullTextHit> Hits, int Skipped)> SearchAsync(SearchScope scope, string? folder, string query, TextSearchOptionsEx options, CancellationToken ct)
    {
        if (scope == SearchScope.FolderIndex)
        {
            if (folder is null) return (Array.Empty<FullTextHit>(), 0);
            return await Task.Run(() =>
            {
                var index = Index.Load(folder) ?? throw new FileNotFoundException(L.Format("Edit.Index.Missing", folder));
                int skipped = index.Files.Count(f => f.Status != IndexedFileStatus.Ok);
                return (FullTextIndex.Search(index, query, options, ct), skipped);
            }, ct);
        }
        var sessions = scope == SearchScope.CurrentDocument
            ? (_ctx.Documents.ActiveDocument is { } a ? new[] { a } : Array.Empty<IPdfDocumentSession>())
            : _ctx.Documents.Documents.ToArray();
        var hits = new List<FullTextHit>();
        int failed = 0;
        _sessionSources.Clear();
        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var key = SourceKey(s);
            _sessionSources[key] = s;
            if (!s.Can(DocumentPermissions.CopyContent)) { failed++; continue; }
            var bytes = s.Current.GetBytes();
            try
            {
                var pages = await Task.Run(() => FullTextIndex.ExtractPages(bytes), ct);
                FullTextIndex.SearchPages(key, s.DisplayName, pages, query, options, hits);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { failed++; }
            if (hits.Count >= options.MaxHits) break;
        }
        return (hits, failed);
    }

    private static string SourceKey(IPdfDocumentSession s) => s.FilePath ?? s.DisplayName + "#" + RuntimeHelpers.GetHashCode(s).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Otwiera plik/dokument trafienia na stronie i podświetla trafienie (nakładka nad stroną).</summary>
    public async Task NavigateAsync(FullTextHit hit, string query, TextSearchOptionsEx options)
    {
        var session = _sessionSources.TryGetValue(hit.Source, out var known) && _ctx.Documents.Documents.Contains(known)
            ? known
            : _ctx.Documents.Documents.FirstOrDefault(d => d.FilePath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(hit.Source), StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            if (!File.Exists(hit.Source)) { _ctx.Message("Edit.Index.FileMissing", hit.Source); return; }
            session = await _ctx.Documents.OpenAsync(hit.Source);
            if (session is null) return;
        }
        _ctx.Documents.ActiveDocument = session;
        var view = await WaitForViewAsync(session);
        if (view is null) return;
        int page = Math.Clamp(hit.PageIndex, 0, view.PageCount - 1);
        view.GoToPage(page);
        var text = await view.GetPageTextAsync(page);
        var rects = HitRects(text, query, options, hit.OccurrenceOnPage);
        var overlay = Overlay(view);
        overlay.Set(page, rects);
        if (rects.Count > 0) view.ScrollIntoView(page, rects[0]);
        view.InvalidateOverlays();
        _ctx.Status(L.Format("Edit.Index.HitStatus", hit.DisplayName, page + 1));
    }

    /// <summary>Prostokąty (wiersze) n-tego wystąpienia frazy w tekście strony z PDFium; gdy brak – pierwszego.</summary>
    internal static IReadOnlyList<PdfRect> HitRects(PageText text, string query, TextSearchOptionsEx options, int occurrence)
    {
        var positions = FullTextIndex.FindAll(text.Text, query, options);
        if (positions.Count == 0) return Array.Empty<PdfRect>();
        int start = positions[Math.Clamp(occurrence, 0, positions.Count - 1)];
        int len = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Length;
        var result = new List<PdfRect>();
        PdfRect? cur = null;
        for (int i = start; i < Math.Min(text.Chars.Count, start + len); i++)
        {
            var b = text.Chars[i].Box;
            if (b.IsEmpty) continue;
            if (cur is { } c && Math.Abs(c.Bottom - b.Bottom) < Math.Max(2, b.Height * 0.5)) cur = c.Union(b);
            else { if (cur is { } done) result.Add(done); cur = b; }
        }
        if (cur is { } last) result.Add(last);
        return result;
    }

    private SearchHighlightOverlay Overlay(IDocumentView view)
    {
        if (_overlays.TryGetValue(view, out var o)) return o;
        o = new SearchHighlightOverlay();
        _overlays.Add(view, o);
        view.AddOverlay(o);
        view.Session.Changed += (_, _) => { o.Set(-1, Array.Empty<PdfRect>()); view.InvalidateOverlays(); };
        return o;
    }

    private async Task<IDocumentView?> WaitForViewAsync(IPdfDocumentSession session)
    {
        for (int i = 0; i < 200; i++)
        {
            if (_ctx.Viewer?.GetView(session) is { PageCount: > 0 } v) return v;
            await Task.Delay(50);
        }
        return null;
    }

    public Task ExportCsvAsync(IReadOnlyList<FullTextHit> hits)
    {
        var path = _ctx.Dialogs?.SaveFile(L["Edit.Search.ExportTitle"], new[] { new FileFilter(L["Edit.Filter.Csv"], "*.csv") }, L["Edit.Search.ExportFileName"] + ".csv");
        if (path is null) return Task.CompletedTask;
        var headers = new[] { L["Edit.Search.Column.File"], L["Edit.Search.Column.Path"], L["Edit.Search.Column.Page"], L["Edit.Search.Column.Context"] };
        var csv = FullTextIndex.ToCsv(hits, headers, L.CurrentCulture.TextInfo.ListSeparator);
        var bytes = new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray();
        _ctx.Status(L.Format("Edit.Convert.Saved", path));
        return _ctx.Files is { } files ? files.WriteAllBytesSafeAsync(path, bytes) : File.WriteAllBytesAsync(path, bytes);
    }

    public void ShowError(Exception ex) => _ctx.Dialogs?.ShowError(ex);

    /// <summary>Polecenie asynchroniczne niezależne od dokumentu.</summary>
    private sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _run;
        private readonly EditingContext _ctx;
        private bool _busy;

        public AsyncCommand(Func<Task> run, EditingContext ctx) { _run = run; _ctx = ctx; }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => !_busy;

        public async void Execute(object? parameter)
        {
            if (_busy) return;
            _busy = true;
            try { await _run(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ctx.Dialogs?.ShowError(ex); }
            finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
        }
    }
}

/// <summary>Podświetlenie trafienia wyszukiwania zaawansowanego (pędzel motywu „bieżące trafienie”).</summary>
internal sealed class SearchHighlightOverlay : IPageOverlay
{
    private int _page = -1;
    private IReadOnlyList<PdfRect> _rects = Array.Empty<PdfRect>();

    public int ZIndex => 950;
    public bool IsVisible => _page >= 0 && _rects.Count > 0;
    public int Page => _page;
    public IReadOnlyList<PdfRect> Rects => _rects;

    public void Set(int page, IReadOnlyList<PdfRect> rects)
    {
        _page = page;
        _rects = rects;
    }

    public void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        if (pageIndex != _page) return;
        var app = Application.Current;
        var fill = app?.TryFindResource(ThemeKeys.ViewerSearchHitCurrent) as Brush;
        var outline = app?.TryFindResource(ThemeKeys.ViewerOverlayOutline) as Brush;
        if (fill is null && outline is null) return;
        var pen = outline is null ? null : new Pen(outline, 1);
        foreach (var r in _rects) dc.DrawRectangle(fill, pen, transform.ToView(r.Inflate(1)));
    }
}

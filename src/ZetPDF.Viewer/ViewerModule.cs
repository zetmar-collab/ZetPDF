using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Viewer.Bookmarks;
using ZetPDF.Viewer.Dialogs;
using ZetPDF.Viewer.Panels;
using ZetPDF.Viewer.Printing;
using ZetPDF.Viewer.ReadAloud;
using ZetPDF.Viewer.Tools;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer;

/// <summary>
/// Moduł ZetPDF.Viewer (właściciel: renderer-viewer): widok dokumentu, narzędzia Ręka/Zaznacz tekst/Migawka,
/// powiększenie, układy, obrót widoku, nawigacja, wyszukiwanie, zakładki, druk, panele Miniatury i Zakładki.
/// </summary>
public sealed class ViewerModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Viewer.Resources.Strings", typeof(ViewerModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Viewer;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ViewerService>();
        services.AddSingleton<IViewerService>(sp => sp.GetRequiredService<ViewerService>());
        services.AddSingleton<IDocumentViewFactory, DocumentViewFactory>();
        services.AddSingleton<HandTool>();
        services.AddSingleton<SelectTextTool>();
        services.AddSingleton<SnapshotTool>();
        services.AddSingleton<IToolService, ToolService>();
        services.AddSingleton<IBookmarkService, BookmarkService>();
        services.AddSingleton<IPrintService, PrintService>();
        services.AddSingleton(sp => new BatchPrintService(sp.GetRequiredService<IPdfRenderer>()));
        services.AddSingleton<ISpeechEngineFactory, SystemSpeechEngineFactory>();
        services.AddSingleton<ReadAloudService>();
        services.AddSingleton<ParallelViewController>();
    }

    public void Initialize(IServiceProvider services)
    {
        var loc = services.GetRequiredService<ILocalizationService>();
        loc.RegisterResources(Strings);
        var registry = services.GetService<ICommandRegistry>();
        if (registry is not null) new ViewerCommands(services, registry).RegisterAll();

        var panels = services.GetService<IPanelRegistry>();
        panels?.Register(new PanelDescriptor(PanelIds.Thumbnails, "Viewer.Panel.Thumbnails", "", 10, sp => new ThumbnailsPanel(sp)));
        panels?.Register(new PanelDescriptor(PanelIds.Bookmarks, "Viewer.Panel.Bookmarks", "", 20, sp => new BookmarksPanel(sp)));
        panels?.Register(new PanelDescriptor(PanelIds.Layers, "Viewer.Layers.Title", "", 45, sp => new Layers.LayersPanel(sp)));
    }
}

/// <summary>Obsługa poleceń wstążki należących do Viewer (39 poleceń z katalogu).</summary>
internal sealed class ViewerCommands
{
    private readonly IServiceProvider _services;
    private readonly ICommandRegistry _registry;
    private readonly IViewerService _viewer;
    private readonly IToolService _tools;
    private readonly IDocumentManager _documents;
    private readonly ILocalizationService _loc;
    private readonly List<ToggleCommand> _toggles = new();
    private DocumentView? _observed;

    public ViewerCommands(IServiceProvider services, ICommandRegistry registry)
    {
        _services = services;
        _registry = registry;
        _viewer = services.GetRequiredService<IViewerService>();
        _tools = services.GetRequiredService<IToolService>();
        _documents = services.GetRequiredService<IDocumentManager>();
        _loc = services.GetRequiredService<ILocalizationService>();
    }

    private DocumentView? View => _viewer.ActiveView as DocumentView;
    private IShellService? Shell => _services.GetService<IShellService>();
    private IDialogService? Dialogs => _services.GetService<IDialogService>();

    public void RegisterAll()
    {
        // druk
        _registry.Register(CommandIds.View.PanelLayers, new RelayCommandLite(() => _services.GetService<IShellService>()?.ShowPanel(PanelIds.Layers), () => true));
        _registry.Register(CommandIds.File.BatchPrint, new RelayCommandLite(() => _ = BatchPrintAsync(), () => true));
        _registry.Register(CommandIds.File.Print, new ActiveDocumentCommand(_documents, Dialogs,
            s => _services.GetRequiredService<IPrintService>().ShowPrintDialogAsync(s, _viewer.GetView(s)?.CurrentPageIndex ?? 0), DocumentPermissions.Print));

        // narzędzia
        Tool(CommandIds.Tool.Hand, _services.GetRequiredService<HandTool>());
        Tool(CommandIds.Tool.SelectText, _services.GetRequiredService<SelectTextTool>());
        Tool(CommandIds.Tool.Snapshot, _services.GetRequiredService<SnapshotTool>());

        // powiększenie
        Cmd(CommandIds.View.ZoomIn, v => v.ZoomStep(true), v => v.Zoom < PageLayout.MaxZoom);
        Cmd(CommandIds.View.ZoomOut, v => v.ZoomStep(false), v => v.Zoom > PageLayout.MinZoom);
        Cmd(CommandIds.View.ZoomTo, ShowZoomTo);
        Cmd(CommandIds.View.FitPage, v => v.ZoomMode = ZoomMode.FitPage);
        Cmd(CommandIds.View.FitWidth, v => v.ZoomMode = ZoomMode.FitWidth);
        Cmd(CommandIds.View.FitVisible, v => v.ZoomMode = ZoomMode.FitVisible);
        Cmd(CommandIds.View.ActualSize, v => v.ZoomMode = ZoomMode.ActualSize);
        Cmd(CommandIds.View.RotateLeft, v => v.ViewRotation -= 90);
        Cmd(CommandIds.View.RotateRight, v => v.ViewRotation += 90);

        // układy
        LayoutToggle(CommandIds.View.SinglePage, PageLayoutMode.SinglePage);
        LayoutToggle(CommandIds.View.Continuous, PageLayoutMode.Continuous);
        LayoutToggle(CommandIds.View.TwoPages, PageLayoutMode.TwoPages);
        LayoutToggle(CommandIds.View.TwoPagesContinuous, PageLayoutMode.TwoPagesContinuous);

        // tryby powłoki
        ShellToggle(CommandIds.View.ReadingMode, s => s.IsReadingMode, (s, v) => s.IsReadingMode = v);
        ShellToggle(CommandIds.View.FullScreen, s => s.IsFullScreen, (s, v) => s.IsFullScreen = v);

        // nawigacja
        Cmd(CommandIds.Nav.GoToPage, ShowGoToPage);
        Cmd(CommandIds.Nav.FirstPage, v => v.GoToPage(0), v => v.CurrentPageIndex > 0);
        Cmd(CommandIds.Nav.PreviousPage, v => v.NavigatePage(-1), v => v.CurrentPageIndex > 0);
        Cmd(CommandIds.Nav.NextPage, v => v.NavigatePage(+1), v => PageLayout.SpreadStart(v.CurrentPageIndex, v.Layout) + (PageLayout.IsTwoUp(v.Layout) ? 2 : 1) < v.PageCount);
        Cmd(CommandIds.Nav.LastPage, v => v.GoToPage(v.PageCount - 1), v => v.CurrentPageIndex < v.PageCount - 1);

        // wyszukiwanie
        Cmd(CommandIds.Nav.Find, v => v.Find.Open(v.TextSelection?.Text));
        Cmd(CommandIds.Nav.FindNext, v => v.Find.Move(+1));
        Cmd(CommandIds.Nav.FindPrevious, v => v.Find.Move(-1));

        // zakładki
        _registry.Register(CommandIds.Nav.AddBookmark, new ActiveDocumentCommand(_documents, Dialogs, AddBookmarkAsync, DocumentPermissions.AssembleDocument,
            s => _viewer.GetView(s) is DocumentView { IsDocumentLoaded: true }));

        // schowek
        Cmd(CommandIds.Edit.Copy, v =>
        {
            if (v.TextSelection is { Text.Length: > 0 } sel) Clipboard.SetText(sel.Text);
        }, v => v.TextSelection is { Text.Length: > 0 } && v.Session.Can(DocumentPermissions.CopyContent));
        Cmd(CommandIds.Edit.SelectAll, v => _ = SelectAllSafe(v), v => v.Session.Can(DocumentPermissions.CopyContent) || v.Session.Can(DocumentPermissions.Annotate));

        // czytanie na głos (etap 3)
        RegisterReadAloud();

        // widok równoległy (etap 3)
        RegisterParallel();

        // odświeżanie stanów przełączników
        _tools.ActiveToolChanged += (_, _) => RaiseToggles();
        _viewer.ActiveViewChanged += (_, _) => { Observe(View); RaiseToggles(); };
        if (Shell is { } shell) shell.ShellStateChanged += (_, _) => RaiseToggles();
        Observe(View);
    }

    private void Observe(DocumentView? view)
    {
        if (_observed is not null) _observed.ViewStateChanged -= OnViewStateChanged;
        _observed = view;
        if (view is not null) view.ViewStateChanged += OnViewStateChanged;
    }

    private void OnViewStateChanged(object? sender, EventArgs e) => RaiseToggles();

    private void RaiseToggles()
    {
        foreach (var t in _toggles) t.RaiseIsCheckedChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private bool HasView() => View is { IsDocumentLoaded: true };

    private void Cmd(string id, Action<DocumentView> execute, Func<DocumentView, bool>? canExecute = null) =>
        _registry.Register(id, new ViewCommand(() => View, execute, canExecute, Dialogs));

    private void Tool(string id, IViewerTool tool)
    {
        var t = new ToggleCommand(_ => _tools.Activate(tool), () => _tools.IsActive(id));
        _toggles.Add(t);
        _registry.Register(id, t);
    }

    private void LayoutToggle(string id, PageLayoutMode mode)
    {
        var t = new ToggleCommand(_ => { if (View is { } v) v.Layout = mode; }, () => View?.Layout == mode, _ => HasView());
        _toggles.Add(t);
        _registry.Register(id, t);
    }

    private void ShellToggle(string id, Func<IShellService, bool> get, Action<IShellService, bool> set)
    {
        var t = new ToggleCommand(_ => { if (Shell is { } s) set(s, !get(s)); }, () => Shell is { } s && get(s), _ => Shell is not null);
        _toggles.Add(t);
        _registry.Register(id, t);
    }

    private async Task SelectAllSafe(DocumentView v)
    {
        try { await v.SelectAllAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Dialogs?.ShowError(ex); }
    }

    private void ShowZoomTo(DocumentView v)
    {
        var suggestions = PageLayout.ZoomSteps.Where(s => s >= 0.25 && s <= 16).Select(s => (s * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%");
        var dlg = new InputDialog(_loc, "Viewer.ZoomTo.Title", _loc["Viewer.ZoomTo.Prompt"], (v.Zoom * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%", suggestions,
            text => PageLayout.TryParsePercent(text, out _) ? null : _loc["Viewer.ZoomTo.Invalid"]);
        if (ShowDialog(dlg) == true && PageLayout.TryParsePercent(dlg.Value, out double z)) v.Zoom = z;
    }

    private void ShowGoToPage(DocumentView v)
    {
        string? Validate(string text) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) && n >= 1 && n <= v.PageCount ? null : _loc.Format("Viewer.GoTo.Invalid", v.PageCount);
        var dlg = new InputDialog(_loc, "Viewer.GoTo.Title", _loc.Format("Viewer.GoTo.Prompt", v.PageCount), (v.CurrentPageIndex + 1).ToString(CultureInfo.CurrentCulture), Array.Empty<string>(), Validate);
        if (ShowDialog(dlg) == true && int.TryParse(dlg.Value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int page)) v.GoToPage(page - 1);
    }

    private void RegisterParallel()
    {
        var parallel = _services.GetRequiredService<ParallelViewController>();
        var view = new ToggleCommand(_ =>
        {
            if (View is not { } v) return;
            if (parallel.GetPair(v) is not null) { parallel.Close(v); return; }
            var others = _documents.Documents.Where(d => !ReferenceEquals(d, v.Session)).ToList();
            if (others.Count == 0)
            {
                Dialogs?.ShowMessage(_loc["Viewer.Parallel.NoOther"], _loc["Viewer.Parallel.Title"]);
                return;
            }
            var dlg = new ParallelViewDialog(_loc, others);
            if (ShowDialog(dlg) == true && dlg.Selected is { } other) parallel.Open(v, other, dlg.Orientation);
        }, () => parallel.GetPair(View) is not null, _ => HasView());
        var sync = new ToggleCommand(_ =>
        {
            if (parallel.GetPair(View) is { } p) parallel.SetSync(p, !p.Sync);
        }, () => parallel.GetPair(View)?.Sync == true, _ => parallel.GetPair(View) is not null);
        _toggles.Add(view);
        _toggles.Add(sync);
        _registry.Register(CommandIds.View.ParallelView, view);
        _registry.Register(CommandIds.View.SyncScroll, sync);
        parallel.Changed += (_, _) => RaiseToggles();
    }

    private ReadAloudPanel? _readPanel;

    private void RegisterReadAloud()
    {
        var read = _services.GetRequiredService<ReadAloudService>();
        var activate = new ToggleCommand(_ => read.IsActive = !read.IsActive, () => read.IsActive);
        _toggles.Add(activate);
        _registry.Register(CommandIds.Read.Activate, activate);
        Cmd(CommandIds.Read.CurrentPage, v => _ = ReadSafe(read, v, false));
        Cmd(CommandIds.Read.FromPage, v => _ = ReadSafe(read, v, true));
        _registry.Register(CommandIds.Read.Pause, new RelayCommandLite(read.TogglePause, () => read.State != ReadAloudState.Stopped));
        _registry.Register(CommandIds.Read.Stop, new RelayCommandLite(read.Stop, () => read.IsActive || read.State != ReadAloudState.Stopped));
        read.StateChanged += (_, _) =>
        {
            activate.RaiseIsCheckedChanged();
            CommandManager.InvalidateRequerySuggested();
            if (read.IsActive) ShowReadPanel(read);
            else if (_readPanel is { } p) { _readPanel = null; p.Close(); }
            if (read.State == ReadAloudState.Stopped && read.IsActive) Shell?.SetStatus(null);
        };
    }

    private void ShowReadPanel(ReadAloudService read)
    {
        if (_readPanel is not null || Shell?.MainWindow is not { IsLoaded: true } owner) return;
        var panel = new ReadAloudPanel(read, _loc, _registry) { Owner = owner };
        panel.Left = owner.Left + Math.Max(0, owner.ActualWidth - panel.Width - 40);
        panel.Top = owner.Top + 160;
        panel.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_readPanel, panel)) return;
            _readPanel = null;
            read.IsActive = false;
        };
        _readPanel = panel;
        panel.Show();
    }

    private async Task ReadSafe(ReadAloudService read, DocumentView view, bool continueToEnd)
    {
        try { await read.ReadAsync(view, view.CurrentPageIndex, continueToEnd); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Dialogs?.ShowError(ex); }
    }

    /// <summary>Druk wsadowy: okno, sprawdzenie plików w tle, hasła, druk w tle, raport.</summary>
    private async Task BatchPrintAsync()
    {
        try
        {
            var window = new BatchPrintWindow(_loc, Dialogs);
            if (ShowDialog(window) != true || window.Options is not { } options || Dialogs is not { } dialogs) return;
            var service = _services.GetRequiredService<BatchPrintService>();
            IReadOnlyList<BatchPrintEntry>? entries = null;
            if (!await dialogs.RunWithProgressAsync(_loc["Viewer.Batch.Checking"], async (p, ct) => entries = await service.ProbeAsync(window.Files, p, ct)) || entries is null) return;
            await service.ResolvePasswordsAsync(entries, dialogs.AskPassword);
            await dialogs.RunWithProgressAsync(_loc["Viewer.Batch.Printing"], (p, ct) => service.PrintAsync(entries, options, (doc, o, _, inner, c) => PrintService.PrintDocumentAsync(doc, o, inner, c), p, ct));
            service.MarkUnfinishedCancelled(entries);
            ShowDialog(new BatchPrintReportWindow(_loc, entries));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Dialogs?.ShowError(ex);
        }
    }

    private bool? ShowDialog(Window w) => Dialogs is { } d ? d.ShowDialog(w) : w.ShowDialog();

    private async Task AddBookmarkAsync(IPdfDocumentSession session)
    {
        if (_viewer.GetView(session) is not DocumentView view) return;
        Shell?.ShowPanel(PanelIds.Bookmarks);
        if (BookmarksPanel.Instance?.TryGetTarget(out var panel) == true && panel.IsLoaded && ReferenceEquals(_viewer.ActiveView, view))
        {
            await panel.AddBookmarkAsync();
            return;
        }
        var service = _services.GetRequiredService<IBookmarkService>();
        var tree = await service.GetBookmarksAsync(session.Current.GetSharedBytes());
        var node = new BookmarkNode(BookmarksPanel.DefaultTitle(view, _loc), view.CurrentPageIndex, BookmarkNode.NoChildren);
        await BookmarkTree.CommitAsync(session, service, BookmarkTree.Insert(tree, Array.Empty<int>(), tree.Count, node), "Viewer.Undo.AddBookmark");
    }
}

/// <summary>Proste polecenie (akcja + warunek).</summary>
internal sealed class RelayCommandLite : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommandLite(Action execute, Func<bool> canExecute) { _execute = execute; _canExecute = canExecute; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute();
    public void Execute(object? parameter) { if (_canExecute()) _execute(); }
}

/// <summary>Polecenie działające na aktywnym (wczytanym) widoku.</summary>
internal sealed class ViewCommand : ICommand
{
    private readonly Func<DocumentView?> _view;
    private readonly Action<DocumentView> _execute;
    private readonly Func<DocumentView, bool>? _canExecute;
    private readonly IDialogService? _dialogs;

    public ViewCommand(Func<DocumentView?> view, Action<DocumentView> execute, Func<DocumentView, bool>? canExecute, IDialogService? dialogs)
    {
        _view = view;
        _execute = execute;
        _canExecute = canExecute;
        _dialogs = dialogs;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _view() is { IsDocumentLoaded: true } v && (_canExecute?.Invoke(v) ?? true);

    public void Execute(object? parameter)
    {
        if (_view() is not { IsDocumentLoaded: true } v) return;
        try { _execute(v); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _dialogs?.ShowError(ex); }
    }
}

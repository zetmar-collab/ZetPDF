using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer;

// ZAŚLEPKI szkieletu (renderer-viewer zastępuje je właściwą implementacją). Działają bez wyjątków, żeby
// powłoka (ui-shell) mogła pracować równolegle: widok pokazuje tylko nazwę dokumentu.

/// <summary>Śledzi aktywny widok (na podstawie IDocumentManager.ActiveDocument).</summary>
public sealed class ViewerService : IViewerService
{
    private readonly Dictionary<IPdfDocumentSession, IDocumentView> _views = new();
    private readonly IDocumentManager _documents;

    public ViewerService(IDocumentManager documents)
    {
        _documents = documents;
        documents.ActiveDocumentChanged += (_, d) =>
        {
            ActiveView = d is null ? null : GetView(d);
            ActiveViewChanged?.Invoke(this, ActiveView);
        };
        documents.DocumentClosed += (_, d) =>
        {
            if (_views.Remove(d, out var v)) v.Dispose();
        };
    }

    public IDocumentView? ActiveView { get; private set; }
    public event EventHandler<IDocumentView?>? ActiveViewChanged;

    public IDocumentView? GetView(IPdfDocumentSession session) => _views.TryGetValue(session, out var v) ? v : null;

    internal void Track(IDocumentView view)
    {
        _views[view.Session] = view;
        if (ReferenceEquals(_documents.ActiveDocument, view.Session))
        {
            ActiveView = view;
            ActiveViewChanged?.Invoke(this, view);
        }
    }
}

public sealed class PlaceholderDocumentViewFactory : IDocumentViewFactory
{
    private readonly ViewerService _viewer;

    public PlaceholderDocumentViewFactory(ViewerService viewer) => _viewer = viewer;

    public IDocumentView CreateView(IPdfDocumentSession session)
    {
        var view = new PlaceholderDocumentView(session);
        _viewer.Track(view);
        return view;
    }
}

internal sealed class PlaceholderDocumentView : IDocumentView
{
    public PlaceholderDocumentView(IPdfDocumentSession session)
    {
        Session = session;
        var text = new TextBlock { Text = session.DisplayName, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        var host = new Border { Child = text };
        host.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ViewerBackground);
        Element = host;
    }

    public IPdfDocumentSession Session { get; }
    public FrameworkElement Element { get; }
    public int PageCount => 0;
    public int CurrentPageIndex => 0;
    public event EventHandler? CurrentPageChanged { add { } remove { } }
    public void GoToPage(int pageIndex, PdfPoint? target = null) { }
    public double Zoom { get; set; } = 1;
    public ZoomMode ZoomMode { get; set; } = ZoomMode.FitWidth;
    public PageLayoutMode Layout { get; set; } = PageLayoutMode.Continuous;
    public int ViewRotation { get; set; }
    public event EventHandler? ViewStateChanged { add { } remove { } }
    public IReadOnlyList<int> SelectedPages { get; set; } = Array.Empty<int>();
    public event EventHandler? SelectedPagesChanged { add { } remove { } }
    public PdfTextSelection? TextSelection => null;
    public event EventHandler? TextSelectionChanged { add { } remove { } }
    public void ClearTextSelection() { }
    public PageTransform? GetPageTransform(int pageIndex) => null;
    public bool TryHitPage(Point viewPoint, out int pageIndex, out PdfPoint pagePoint) { pageIndex = -1; pagePoint = default; return false; }
    public void ScrollIntoView(int pageIndex, PdfRect rect) { }
    public void AddOverlay(IPageOverlay overlay) { }
    public void RemoveOverlay(IPageOverlay overlay) { }
    public void InvalidateOverlays() { }
    public Task<PageText> GetPageTextAsync(int pageIndex, CancellationToken ct = default) => Task.FromResult(PageText.Empty(pageIndex));
    public void Dispose() { }
}

/// <summary>Usługa narzędzi z narzędziem domyślnym "Ręka" (bez działania w zaślepce).</summary>
public sealed class ToolService : IToolService
{
    private readonly IViewerTool _default = new NullTool("Tool.Hand");

    public ToolService() => ActiveTool = _default;

    public IViewerTool ActiveTool { get; private set; }
    public event EventHandler? ActiveToolChanged;

    public void Activate(IViewerTool tool)
    {
        ActiveTool = tool;
        ActiveToolChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ActivateDefault() => Activate(_default);

    public bool IsActive(string toolId) => ActiveTool.Id == toolId;

    private sealed class NullTool : IViewerTool
    {
        public NullTool(string id) => Id = id;
        public string Id { get; }
        public Cursor Cursor => Cursors.Arrow;
        public IPageOverlay? Overlay => null;
        public void OnActivated(IDocumentView view) { }
        public void OnDeactivated(IDocumentView view) { }
        public bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e) => false;
        public bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e) => false;
        public bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e) => false;
        public bool OnKeyDown(IDocumentView view, KeyEventArgs e) => false;
    }
}

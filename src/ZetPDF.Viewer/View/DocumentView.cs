using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Viewer.Text;
using ZetPDF.Viewer.Tools;

namespace ZetPDF.Viewer.View;

/// <summary>Po przeładowaniu dokumentu PDFium w widoku (po zmianie sesji) – panele odświeżają się wg rodzaju zmiany.</summary>
public sealed class DocumentReloadedEventArgs : EventArgs
{
    public DocumentReloadedEventArgs(DocumentChangeKind kind, IReadOnlyList<int>? affectedPages) { Kind = kind; AffectedPages = affectedPages; }
    public DocumentChangeKind Kind { get; }
    public IReadOnlyList<int>? AffectedPages { get; }
}

/// <summary>
/// Widok dokumentu (<see cref="IDocumentView"/>): wirtualizowana lista stron, render asynchroniczny przez
/// <see cref="IRenderDocument"/> z anulowaniem, pamięć podręczna LRU z limitem bajtów (najpierw podgląd niskiej
/// rozdzielczości, potem ostra bitmapa – przy dużym powiększeniu tylko widoczny fragment strony),
/// powiększenie/dopasowania, układy, obrót widoku, wyszukiwanie, zaznaczanie tekstu i nakładki modułów.
/// <para>
/// KONTRAKT WSPÓŁRZĘDNYCH (dla modułów komentarzy, formularzy, redakcji, edycji):
/// „współrzędne widoku” = DIP względem <see cref="Element"/> (lewy górny róg obszaru stron, bez przesunięcia przewijania).
/// <see cref="GetPageTransform"/> zwraca <see cref="PageTransform"/> z ViewOrigin w tym układzie, obrotem łącznym
/// (/Rotate strony + <see cref="ViewRotation"/>) i skalą = Zoom·96/72 – zgodnie z FPDF_PageToDevice (test).
/// Nakładki (<see cref="IPageOverlay.Render"/>) dostają ten sam transform i rysują w tym samym układzie.
/// Zdarzenia myszy trafiają do aktywnego <see cref="IViewerTool"/> jako <see cref="PagePointerEventArgs"/>
/// (ViewPoint w układzie Element, PagePoint w punktach strony; PageIndex = -1 poza stronami).
/// Transform jest ważny do następnej zmiany przewijania/powiększenia – nie przechowuj go, pobieraj przy rysowaniu.
/// </para>
/// </summary>
public sealed class DocumentView : IDocumentView
{
    private const int KindLow = 0;
    private const int KindHigh = 1;
    private const long HighBudget = 256L * 1024 * 1024;
    private const long LowBudget = 48L * 1024 * 1024;
    private const long FullPageMaxPixels = 8_000_000;

    private readonly IServiceProvider _services;
    private readonly IPdfRenderer _renderer;
    private readonly ILocalizationService _loc;
    private readonly IToolService _tools;
    private readonly HandTool _hand;
    private readonly ILogger? _log;
    private readonly Grid _root;
    private readonly ScrollViewer _scroll;
    private readonly PagesPresenter _presenter;
    private readonly FindBar _find;
    private readonly TextBlock _status;

    private readonly RenderCache _hi = new(HighBudget);
    private readonly RenderCache _lo = new(LowBudget);
    private readonly Dictionary<(int Page, int Kind), (CancellationTokenSource Cts, string Key)> _pending = new();
    private readonly HashSet<string> _failed = new();
    private readonly Dictionary<int, PageText> _texts = new();
    private readonly Dictionary<int, Task<PageText>> _textTasks = new();
    private readonly Dictionary<int, IReadOnlyList<PdfLinkInfo>> _links = new();
    private readonly List<IPageOverlay> _overlays = new();

    private IRenderDocument? _doc;
    private IReadOnlyList<PageInfo> _pages = Array.Empty<PageInfo>();
    private PageLayout _layout;
    private int _generation;
    private int _reloadVersion;
    private bool _renderScheduled;
    private bool _disposed;
    private bool _initialPlacementDone;
    private bool _spaceHand;
    private Cursor? _hoverCursor;

    private double _zoom = 1;
    private ZoomMode _zoomMode;
    private PageLayoutMode _layoutMode;
    private int _viewRotation;
    private bool _showAnnotations = true;
    private int _renderEpoch;
    private int _currentPage;
    private IReadOnlyList<int> _selectedPages = Array.Empty<int>();
    private PdfTextSelection? _selection;

    public DocumentView(IPdfDocumentSession session, IServiceProvider services)
    {
        Session = session;
        _services = services;
        _renderer = services.GetRequiredService<IPdfRenderer>();
        _loc = services.GetRequiredService<ILocalizationService>();
        _tools = services.GetRequiredService<IToolService>();
        _hand = services.GetRequiredService<HandTool>();
        _log = services.GetService<ILoggerFactory>()?.CreateLogger<DocumentView>();
        var settings = services.GetService<ISettingsService>()?.Current;
        _zoomMode = settings?.DefaultZoomMode ?? ZoomMode.FitWidth;
        _zoom = settings is { DefaultZoom: > 0 } ? PageLayout.Clamp(settings.DefaultZoom) : 1;
        _layoutMode = settings?.DefaultLayout ?? PageLayoutMode.Continuous;
        _layout = PageLayout.Build(_pages, _layoutMode, 0, _zoom, 0, new Size(0, 0));

        _presenter = new PagesPresenter(this);
        _presenter.SetResourceReference(PagesPresenter.BackgroundBrushProperty, ThemeKeys.ViewerBackground);
        _presenter.SetResourceReference(PagesPresenter.PageBorderBrushProperty, ThemeKeys.ViewerPageBorder);
        _presenter.SetResourceReference(PagesPresenter.PageShadowBrushProperty, ThemeKeys.ViewerPageShadow);
        var overlay = _presenter.Overlay;
        overlay.SetResourceReference(OverlayLayer.SearchHitBrushProperty, ThemeKeys.ViewerSearchHit);
        overlay.SetResourceReference(OverlayLayer.SearchHitCurrentBrushProperty, ThemeKeys.ViewerSearchHitCurrent);
        overlay.SetResourceReference(OverlayLayer.SelectionBrushProperty, ThemeKeys.ViewerTextSelection);
        overlay.SetResourceReference(OverlayLayer.OutlineBrushProperty, ThemeKeys.ViewerOverlayOutline);
        overlay.SetResourceReference(OverlayLayer.HandleBrushProperty, ThemeKeys.ViewerOverlayHandle);

        _scroll = new ScrollViewer
        {
            Content = _presenter,
            CanContentScroll = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
        };
        _scroll.SetResourceReference(Control.BackgroundProperty, ThemeKeys.ViewerBackground);

        _find = new FindBar(this, _loc);
        _status = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, TextAlignment = TextAlignment.Center };
        _status.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _status.Loc(TextBlock.TextProperty, _loc, "Viewer.Loading");
        _status.IsHitTestVisible = false;

        // nawigacja stron i powiększenie: pasek stanu powłoki (bez własnego paska pod widokiem)
        _root = new Grid();
        _root.Children.Add(_scroll);
        _root.Children.Add(_status);
        _root.Children.Add(_find);
        _root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.ViewerBackground);
        _root.PreviewKeyDown += OnPreviewKeyDown;
        _root.PreviewKeyUp += OnPreviewKeyUp;
        _root.Tag = this;
        System.Windows.Automation.AutomationProperties.SetName(_root, session.DisplayName);

        _presenter.ContextMenu = new ContextMenu();
        _presenter.ContextMenuOpening += OnContextMenuOpening;

        _tools.ActiveToolChanged += OnActiveToolChanged;
        session.Changed += OnSessionChanged;
        UpdateCursor();
        _ = ReloadAsync(DocumentChangeKind.All, null);
    }

    // ================================================================== IDocumentView

    public IPdfDocumentSession Session { get; }
    public FrameworkElement Element => _root;
    public int PageCount => _pages.Count;
    public int CurrentPageIndex => _currentPage;
    public event EventHandler? CurrentPageChanged;
    public event EventHandler? ViewStateChanged;
    public event EventHandler? SelectedPagesChanged;
    public event EventHandler? TextSelectionChanged;

    /// <summary>Dokument przeładowany (po poleceniu, Cofnij/Ponów, Przywróć) – dla paneli.</summary>
    public event EventHandler<DocumentReloadedEventArgs>? DocumentReloaded;

    public double Zoom
    {
        get => _zoom;
        set => SetZoom(value, null, ZoomMode.Custom);
    }

    public ZoomMode ZoomMode
    {
        get => _zoomMode;
        set
        {
            _zoomMode = value;
            if (value == ZoomMode.ActualSize) SetZoom(1.0, null, ZoomMode.ActualSize);
            else if (value == ZoomMode.Custom) ViewStateChanged?.Invoke(this, EventArgs.Empty);
            else
            {
                SetZoom(PageLayout.ComputeZoom(value, _pages, _layoutMode, _viewRotation, _currentPage, _presenter.Viewport, _zoom), null, value);
                if (value == ZoomMode.FitPage) GoToPage(_currentPage);
            }
        }
    }

    public PageLayoutMode Layout
    {
        get => _layoutMode;
        set
        {
            if (_layoutMode == value) return;
            _layoutMode = value;
            RecomputeFitZoom();
            RebuildLayout();
            GoToPage(_currentPage);
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int ViewRotation
    {
        get => _viewRotation;
        set
        {
            int v = ((value % 360) + 360) % 360;
            if (v % 90 != 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (v == _viewRotation) return;
            _viewRotation = v;
            CancelAllRenders();
            RecomputeFitZoom();
            RebuildLayout();
            GoToPage(_currentPage);
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool ShowAnnotations
    {
        get => _showAnnotations;
        set
        {
            if (_showAnnotations == value) return;
            _showAnnotations = value;
            // bitmapy z poprzednimi flagami są nieaktualne – renderujemy od nowa (plik bez zmian)
            _renderEpoch++;
            CancelAllRenders();
            _hi.Clear();
            _lo.Clear();
            _failed.Clear();
            _presenter.InvalidateVisual();
            ScheduleRenders();
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Flagi renderu widoku: bez komentarzy (FPDF_ANNOT) przy ukryciu, zawsze z widżetami pól (FPDF_FFLDraw).</summary>
    internal RenderFlags ViewRenderFlags => _showAnnotations ? RenderFlags.Default : RenderFlags.FormFields;

    public IReadOnlyList<int> SelectedPages
    {
        get => _selectedPages.Count > 0 ? _selectedPages : (_pages.Count > 0 ? new[] { _currentPage } : Array.Empty<int>());
        set
        {
            var v = (value ?? Array.Empty<int>()).Where(i => i >= 0 && i < _pages.Count).Distinct().OrderBy(i => i).ToArray();
            if (v.SequenceEqual(_selectedPages)) return;
            _selectedPages = v;
            SelectedPagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PdfTextSelection? TextSelection => _selection;

    public void ClearTextSelection() => SetTextSelection(null);

    public void GoToPage(int pageIndex, PdfPoint? target = null)
    {
        if (_pages.Count == 0) return;
        pageIndex = Math.Clamp(pageIndex, 0, _pages.Count - 1);
        if (!PageLayout.IsContinuous(_layoutMode) && PageLayout.SpreadStart(pageIndex, _layoutMode) != PageLayout.SpreadStart(_currentPage, _layoutMode) || !_layout.IsPlaced(pageIndex))
        {
            _currentPage = pageIndex;
            if (_zoomMode == ZoomMode.FitPage) _zoom = PageLayout.ComputeZoom(_zoomMode, _pages, _layoutMode, _viewRotation, _currentPage, _presenter.Viewport, _zoom);
            RebuildLayout();
        }
        var r = _layout.GetPageRect(pageIndex);
        if (r.IsEmpty) return;
        double y = r.Y - PageLayout.Margin;
        double x = _presenter.Offset.X;
        if (target is { } t && GetPresenterTransform(pageIndex, new Point(r.X, r.Y)) is { } pt)
        {
            var v = pt.ToView(t);
            y = v.Y - PageLayout.Margin;
            if (v.X < x || v.X > x + _presenter.Viewport.Width) x = v.X - PageLayout.Margin;
        }
        _presenter.SetOffset(new Point(x, y));
        SetCurrentPage(pageIndex);
        ScheduleRenders();
    }

    public PageTransform? GetPageTransform(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)_pages.Count) return null;
        var r = _presenter.PageRectInView(pageIndex);
        if (r.IsEmpty) return null;
        var shift = PresenterToElement();
        return new PageTransform(_pages[pageIndex].Size, _pages[pageIndex].CropOrigin, _pages[pageIndex].Rotation + _viewRotation, _layout.Scale, new Point(r.X + shift.X, r.Y + shift.Y));
    }

    public bool TryHitPage(Point viewPoint, out int pageIndex, out PdfPoint pagePoint)
    {
        var shift = PresenterToElement();
        var p = new Point(viewPoint.X - shift.X, viewPoint.Y - shift.Y);
        foreach (int i in _layout.PlacedPages)
        {
            var r = _presenter.PageRectInView(i);
            if (r.Contains(p))
            {
                pageIndex = i;
                pagePoint = GetPresenterTransform(i, r.TopLeft)!.ToPage(p);
                return true;
            }
        }
        pageIndex = -1;
        pagePoint = default;
        return false;
    }

    public void ScrollIntoView(int pageIndex, PdfRect rect)
    {
        if ((uint)pageIndex >= (uint)_pages.Count) return;
        if (!_layout.IsPlaced(pageIndex)) GoToPage(pageIndex);
        var r = _presenter.PageRectInView(pageIndex);
        if (r.IsEmpty) return;
        Rect target = rect.IsEmpty ? r : GetPresenterTransform(pageIndex, r.TopLeft)!.ToView(rect);
        var vp = new Rect(_presenter.Viewport);
        var off = _presenter.Offset;
        double nx = off.X, ny = off.Y;
        if (target.Top < 0 || target.Bottom > vp.Height) ny = off.Y + target.Top - Math.Max(PageLayout.Margin, (vp.Height - target.Height) / 3);
        if (target.Left < 0 || target.Right > vp.Width) nx = off.X + target.Left - Math.Max(PageLayout.Margin, (vp.Width - target.Width) / 2);
        _presenter.SetOffset(new Point(nx, ny));
    }

    public void AddOverlay(IPageOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!_overlays.Contains(overlay)) _overlays.Add(overlay);
        InvalidateOverlays();
    }

    public void RemoveOverlay(IPageOverlay overlay)
    {
        if (_overlays.Remove(overlay)) InvalidateOverlays();
    }

    public void InvalidateOverlays() => _presenter.Overlay.InvalidateVisual();

    public Task<PageText> GetPageTextAsync(int pageIndex, CancellationToken ct = default) => EnsureText(pageIndex).WaitAsync(ct);

    // ================================================================== stan wewnętrzny dla narzędzi/paneli/poleceń

    internal PageLayout PageGeometry => _layout;
    internal IRenderDocument? RenderDocument => _doc;
    internal FindBar Find => _find;
    internal Point ScrollOffset => _presenter.Offset;
    internal bool IsDocumentLoaded => _doc is not null;

    internal void ScrollTo(Point offset) => _presenter.SetOffset(offset);

    internal void FocusPages() => _presenter.Focus();

    private IViewerTool CurrentTool => _spaceHand ? _hand : _tools.ActiveTool;

    internal void UpdateCursor() => _presenter.Cursor = _hoverCursor ?? CurrentTool.Cursor;

    internal void SetHoverCursor(Cursor? cursor)
    {
        if (ReferenceEquals(_hoverCursor, cursor)) return;
        _hoverCursor = cursor;
        UpdateCursor();
    }

    internal void SetTextSelection(PdfTextSelection? selection)
    {
        if (selection is null && _selection is null) return;
        _selection = selection;
        InvalidateOverlays();
        TextSelectionChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    internal PageText? GetCachedText(int page) => _texts.TryGetValue(page, out var t) ? t : null;

    /// <summary>Tekst strony z pamięci podręcznej widoku (ładowany raz z PDFium).</summary>
    internal Task<PageText> EnsureText(int page)
    {
        if (_texts.TryGetValue(page, out var t)) return Task.FromResult(t);
        if (_textTasks.TryGetValue(page, out var task)) return task;
        var doc = _doc;
        if (doc is null || (uint)page >= (uint)_pages.Count) return Task.FromResult(PageText.Empty(page));
        int gen = _generation;
        task = LoadText(doc, page, gen);
        _textTasks[page] = task;
        return task;
    }

    private async Task<PageText> LoadText(IRenderDocument doc, int page, int gen)
    {
        try
        {
            var text = await doc.GetPageTextAsync(page);
            if (gen == _generation)
            {
                if (_texts.Count > 600) _texts.Clear();
                _texts[page] = text;
            }
            return text;
        }
        finally
        {
            if (gen == _generation) _textTasks.Remove(page);
        }
    }

    /// <summary>Strona pod kursorem albo najbliższa (gdy kursor między stronami) i punkt strony przycięty do niej.</summary>
    internal (int Page, PdfPoint Point) NearestPage(PagePointerEventArgs e)
    {
        if (e.PageIndex >= 0) return (e.PageIndex, e.PagePoint);
        var shift = PresenterToElement();
        var p = new Point(e.ViewPoint.X - shift.X, e.ViewPoint.Y - shift.Y);
        int best = -1;
        double bestD = double.MaxValue;
        Rect bestRect = Rect.Empty;
        foreach (int i in _layout.PlacedPages)
        {
            var r = _presenter.PageRectInView(i);
            double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
            double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; bestRect = r; }
        }
        if (best < 0) return (-1, default);
        var clamped = new Point(Math.Clamp(p.X, bestRect.Left, bestRect.Right), Math.Clamp(p.Y, bestRect.Top, bestRect.Bottom));
        return (best, GetPresenterTransform(best, bestRect.TopLeft)!.ToPage(clamped));
    }

    internal PdfLinkInfo? HitLink(int page, PdfPoint point)
    {
        if (page < 0 || _doc is null) return null;
        if (!_links.TryGetValue(page, out var links))
        {
            _ = LoadLinks(_doc, page, _generation);
            return null;
        }
        foreach (var l in links) if (l.Rect.Contains(point)) return l;
        return null;
    }

    private async Task LoadLinks(IRenderDocument doc, int page, int gen)
    {
        _links[page] = Array.Empty<PdfLinkInfo>(); // zapobiega wielokrotnemu ładowaniu
        try
        {
            var links = await doc.GetLinksAsync(page);
            if (gen == _generation) _links[page] = links;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or PdfCorruptException) { }
    }

    // ================================================================== powiększenie

    internal void SetZoom(double zoom, Point? anchor, ZoomMode mode)
    {
        zoom = PageLayout.Clamp(zoom);
        _zoomMode = mode;
        if (Math.Abs(zoom - _zoom) < 1e-9)
        {
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var a = anchor ?? new Point(_presenter.Viewport.Width / 2, _presenter.Viewport.Height / 2);
        int hitPage = -1;
        PdfPoint hitPoint = default;
        foreach (int i in _layout.PlacedPages)
        {
            var r = _presenter.PageRectInView(i);
            if (r.Contains(a)) { hitPage = i; hitPoint = GetPresenterTransform(i, r.TopLeft)!.ToPage(a); break; }
        }
        double relY = _layout.Extent.Height > 0 ? (_presenter.Offset.Y + a.Y) / _layout.Extent.Height : 0;
        _zoom = zoom;
        RebuildLayout();
        if (hitPage >= 0)
        {
            var r = _presenter.PageRectInView(hitPage);
            var v = GetPresenterTransform(hitPage, r.TopLeft)!.ToView(hitPoint);
            _presenter.SetOffset(new Point(_presenter.Offset.X + v.X - a.X, _presenter.Offset.Y + v.Y - a.Y));
        }
        else
        {
            _presenter.SetOffset(new Point(_presenter.Offset.X, relY * _layout.Extent.Height - a.Y));
        }
        ScheduleRenders();
        ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ZoomStep(bool up, Point? anchor = null) => SetZoom(PageLayout.NextZoomStep(_zoom, up), anchor, ZoomMode.Custom);

    private void RecomputeFitZoom()
    {
        if (_zoomMode is ZoomMode.Custom or ZoomMode.ActualSize) return;
        _zoom = PageLayout.ComputeZoom(_zoomMode, _pages, _layoutMode, _viewRotation, _currentPage, _presenter.Viewport, _zoom);
    }

    private void RebuildLayout()
    {
        _layout = PageLayout.Build(_pages, _layoutMode, _viewRotation, _zoom, _currentPage, _presenter.Viewport);
        _presenter.OnLayoutChanged();
        ScheduleRenders();
    }

    // ================================================================== zdarzenia prezentera

    internal void OnViewportSizeChanged()
    {
        if (_pages.Count == 0) return;
        int page = _currentPage;
        double oldZoom = _zoom;
        RecomputeFitZoom();
        RebuildLayout();
        if (!_initialPlacementDone && _presenter.Viewport.Width > 0)
        {
            _initialPlacementDone = true;
            GoToPage(page);
        }
        else if (Math.Abs(oldZoom - _zoom) > 1e-9)
        {
            GoToPage(page);
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void OnScrolled()
    {
        UpdateCurrentPageFromScroll();
        ScheduleRenders();
        if (CurrentTool is SelectTextTool) PrefetchVisibleText();
    }

    private void UpdateCurrentPageFromScroll()
    {
        if (!PageLayout.IsContinuous(_layoutMode) || _pages.Count == 0) return;
        var vp = new Rect(_presenter.Viewport);
        int best = _currentPage;
        double bestArea = -1;
        foreach (int i in VisiblePages(vp))
        {
            var r = Rect.Intersect(_presenter.PageRectInView(i), vp);
            double area = r.IsEmpty ? 0 : r.Height * Math.Max(1, r.Width);
            if (area > bestArea + 0.5) { bestArea = area; best = i; }
        }
        SetCurrentPage(best);
    }

    private void SetCurrentPage(int page)
    {
        if (page == _currentPage) return;
        _currentPage = page;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<int> VisiblePages(Rect area)
    {
        // strony w układzie są uporządkowane pionowo – szukamy binarnie pierwszej przecinającej obszar
        var placed = _layout.PlacedPages;
        if (placed.Count == 0) yield break;
        double top = _presenter.Offset.Y + area.Top;
        int lo = 0, hi = placed.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            var r = _layout.GetPageRect(placed[mid]);
            if (r.Bottom < top) lo = mid + 1; else hi = mid;
        }
        for (int k = Math.Max(0, lo - 2); k < placed.Count; k++)
        {
            var r = _presenter.PageRectInView(placed[k]);
            if (r.Top > area.Bottom) yield break;
            if (r.IntersectsWith(area)) yield return placed[k];
        }
    }

    // ================================================================== renderowanie

    private void ScheduleRenders()
    {
        if (_renderScheduled || _disposed) return;
        _renderScheduled = true;
        _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _renderScheduled = false;
            UpdateRenders();
        }));
    }

    private static (int W, int H) FullPixelSize(PageInfo info, int viewRotation, double scale)
    {
        int total = ((info.Rotation + viewRotation) % 360 + 360) % 360;
        double w = info.Size.Width, h = info.Size.Height;
        if (total % 180 != 0) (w, h) = (h, w);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    private void UpdateRenders()
    {
        if (_disposed || _doc is null || _presenter.Viewport.Width <= 0) return;
        var vp = new Rect(_presenter.Viewport);
        var prefetch = Rect.Inflate(vp, 0, vp.Height * 0.75);
        double pxPerDip = _presenter.PixelsPerDip;
        double scalePx = _layout.Scale * pxPerDip;
        var center = new Point(vp.Width / 2, vp.Height / 2);
        var wanted = new HashSet<(int, int)>();
        var pages = VisiblePages(prefetch)
            .OrderBy(i => { var r = _presenter.PageRectInView(i); return Math.Abs(r.Top + r.Height / 2 - center.Y); })
            .ToList();

        // 1) najpierw podglądy niskiej rozdzielczości (szybkie), potem ostre bitmapy widocznych stron
        foreach (int page in pages)
        {
            wanted.Add((page, KindLow));
            double loScale = Math.Clamp(scalePx * 0.3, 0.05, 0.4);
            var lo = _lo.Get(page, KindLow);
            if (lo is null || lo.ViewRotation != _viewRotation)
            {
                var (w, h) = FullPixelSize(_pages[page], _viewRotation, loScale);
                Request(page, KindLow, loScale, new PixelRect(0, 0, w, h), w, h);
            }
        }
        foreach (int page in pages)
        {
            var info = _pages[page];
            var r = _presenter.PageRectInView(page);
            var (fw, fh) = FullPixelSize(info, _viewRotation, scalePx);
            double f = fw / r.Width; // piksele na DIP w tej bitmapie
            PixelRect ToPixels(Rect area)
            {
                var a = Rect.Intersect(r, area);
                if (a.IsEmpty) return new PixelRect(0, 0, 0, 0);
                int x0 = Math.Max(0, (int)Math.Floor((a.Left - r.Left) * f)), y0 = Math.Max(0, (int)Math.Floor((a.Top - r.Top) * f));
                int x1 = Math.Min(fw, (int)Math.Ceiling((a.Right - r.Left) * f)), y1 = Math.Min(fh, (int)Math.Ceiling((a.Bottom - r.Top) * f));
                return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
            }
            var needed = ToPixels(vp);
            bool visible = needed.Width > 0 && needed.Height > 0;
            bool full = (long)fw * fh <= FullPageMaxPixels;
            if (!visible && !full) continue; // fragmentów poza oknem nie renderujemy z wyprzedzeniem
            wanted.Add((page, KindHigh));
            var region = full ? new PixelRect(0, 0, fw, fh) : ToPixels(Rect.Inflate(vp, vp.Width * 0.25, vp.Height * 0.25));
            var hi = _hi.Get(page, KindHigh);
            bool ok = hi is not null && hi.ViewRotation == _viewRotation && Math.Abs(hi.PixelScale - scalePx) <= scalePx * 1e-3
                      && (full ? hi.IsFullPage : Covers(hi.Region, needed));
            if (!ok) Request(page, KindHigh, scalePx, region, fw, fh);
        }

        foreach (var key in _pending.Keys.ToList())
        {
            if (wanted.Contains(key)) continue;
            _pending[key].Cts.Cancel();
            _pending.Remove(key);
        }
    }

    private static bool Covers(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    private void Request(int page, int kind, double scale, PixelRect region, int fullW, int fullH)
    {
        string key = FormattableString.Invariant($"{_generation}|{_renderEpoch}|{page}|{kind}|{scale:F5}|{_viewRotation}|{region.X},{region.Y},{region.Width},{region.Height}");
        if (_failed.Contains(key)) return;
        if (_pending.TryGetValue((page, kind), out var p))
        {
            if (p.Key == key) return;
            p.Cts.Cancel();
        }
        var cts = new CancellationTokenSource();
        _pending[(page, kind)] = (cts, key);
        _ = RunRender(_doc!, _generation, _renderEpoch, ViewRenderFlags, page, kind, scale, _viewRotation, region, fullW, fullH, key, cts);
    }

    private async Task RunRender(IRenderDocument doc, int gen, int epoch, RenderFlags flags, int page, int kind, double scale, int rotation, PixelRect region, int fullW, int fullH, string key, CancellationTokenSource cts)
    {
        try
        {
            var request = new RenderRequest(scale, rotation, region.X == 0 && region.Y == 0 && region.Width >= fullW && region.Height >= fullH ? null : region, flags);
            var bmp = await doc.RenderPageAsync(page, request, cts.Token);
            var src = await Task.Run(() => Ui.ToBitmapSource(bmp));
            if (gen != _generation || epoch != _renderEpoch || cts.IsCancellationRequested || _disposed) return;
            var entry = new PageBitmap(page, rotation, scale, new PixelRect(region.X, region.Y, bmp.Width, bmp.Height), fullW, fullH, src);
            (kind == KindLow ? _lo : _hi).Put(page, kind, entry);
            _presenter.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _failed.Add(key);
            _log?.LogWarning(ex, "Render of page {Page} failed", page + 1);
        }
        finally
        {
            if (_pending.TryGetValue((page, kind), out var p) && ReferenceEquals(p.Cts, cts)) _pending.Remove((page, kind));
            cts.Dispose();
        }
    }

    private void CancelAllRenders()
    {
        foreach (var p in _pending.Values) p.Cts.Cancel();
        _pending.Clear();
    }

    /// <summary>Rysuje bitmapy strony (podgląd + ostra) w prostokącie strony.</summary>
    internal void DrawPageBitmaps(DrawingContext dc, int page, Rect r)
    {
        if (_lo.Peek(page, KindLow) is { } lo && lo.ViewRotation == _viewRotation) dc.DrawImage(lo.Bitmap, r);
        if (_hi.Peek(page, KindHigh) is { } hi && hi.ViewRotation == _viewRotation)
        {
            double f = r.Width / hi.FullWidth;
            dc.DrawImage(hi.Bitmap, new Rect(r.X + hi.Region.X * f, r.Y + hi.Region.Y * f, hi.Region.Width * f, hi.Region.Height * f));
        }
    }

    /// <summary>Miniatura strony (bez obrotu widoku) mieszcząca się w maxWidth × maxHeight pikseli.</summary>
    internal async Task<BitmapSource?> RenderThumbnailAsync(int page, int maxWidth, int maxHeight, CancellationToken ct)
    {
        var doc = _doc;
        if (doc is null || (uint)page >= (uint)doc.PageCount) return null;
        var info = doc.Pages[page];
        var s = info.DisplaySize;
        double scale = Math.Min(maxWidth / s.Width, maxHeight / s.Height);
        var bmp = await doc.RenderPageAsync(page, new RenderRequest(scale), ct);
        return await Task.Run(() => Ui.ToBitmapSource(bmp), ct);
    }

    /// <summary>Obszar strony (punkty) jako obraz o co najmniej <paramref name="dpi"/> (migawka), z obrotem widoku.</summary>
    internal async Task<BitmapSource?> RenderRegionAsync(int page, PdfRect rect, double dpi, int maxSide)
    {
        var doc = _doc;
        if (doc is null || (uint)page >= (uint)doc.PageCount) return null;
        var info = doc.Pages[page];
        double scale = Math.Max(dpi / 72.0, _layout.Scale * _presenter.PixelsPerDip);
        scale = Math.Min(scale, maxSide / Math.Max(1, Math.Max(rect.Width, rect.Height)));
        var t = new PageTransform(info.Size, info.CropOrigin, info.Rotation + _viewRotation, scale, new Point(0, 0));
        var px = t.ToView(rect);
        var clip = new PixelRect((int)Math.Floor(px.X), (int)Math.Floor(px.Y), Math.Max(1, (int)Math.Ceiling(px.Width)), Math.Max(1, (int)Math.Ceiling(px.Height)));
        var bmp = await doc.RenderPageAsync(page, new RenderRequest(scale, _viewRotation, clip, ViewRenderFlags));
        return await Task.Run(() => Ui.ToBitmapSource(bmp, scale * 72));
    }

    // ================================================================== nakładki

    private Vector PresenterToElement()
    {
        if (!_presenter.IsLoaded || !_root.IsAncestorOf(_presenter)) return new Vector(0, 0);
        try { return (Vector)_presenter.TranslatePoint(new Point(0, 0), _root); }
        catch (InvalidOperationException) { return new Vector(0, 0); }
    }

    private PageTransform? GetPresenterTransform(int page, Point viewOrigin)
    {
        if ((uint)page >= (uint)_pages.Count) return null;
        var info = _pages[page];
        return new PageTransform(info.Size, info.CropOrigin, info.Rotation + _viewRotation, _layout.Scale, viewOrigin);
    }

    private bool _overlayErrorLogged;

    internal void RenderOverlays(DrawingContext dc, Rect bounds)
    {
        if (_doc is null) return;
        var layer = _presenter.Overlay;
        var shift = PresenterToElement();
        var modules = _overlays.Where(o => o.IsVisible).OrderBy(o => o.ZIndex).ToList();
        if (CurrentTool.Overlay is { IsVisible: true } toolOverlay && !modules.Contains(toolOverlay)) modules.Add(toolOverlay);
        foreach (int page in VisiblePages(bounds))
        {
            var r = _presenter.PageRectInView(page);
            var t = GetPresenterTransform(page, r.TopLeft)!;

            // trafienia wyszukiwania
            var hits = _find.HitsOnPage(page);
            if (hits.Count > 0)
            {
                foreach (int idx in hits)
                {
                    var brush = idx == _find.CurrentIndex ? layer.SearchHitCurrentBrush : layer.SearchHitBrush;
                    if (brush is null) continue;
                    foreach (var rect in _find.Hits[idx].Rects) dc.DrawRectangle(brush, null, t.ToView(rect.Inflate(0.5)));
                }
            }

            // zaznaczenie tekstu
            if (_selection is { } sel && layer.SelectionBrush is { } selBrush)
                foreach (var span in sel.Spans)
                    if (span.PageIndex == page)
                        foreach (var q in span.Quads) dc.DrawRectangle(selBrush, null, t.ToView(q.Bounds));

            // nakładki modułów (układ Element)
            if (modules.Count == 0) continue;
            var te = new PageTransform(t.PageSize, t.CropOrigin, t.Rotation, t.Scale, new Point(r.X + shift.X, r.Y + shift.Y));
            bool shifted = shift.X != 0 || shift.Y != 0;
            if (shifted) dc.PushTransform(new TranslateTransform(-shift.X, -shift.Y));
            foreach (var o in modules)
            {
                try { o.Render(dc, page, te); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!_overlayErrorLogged) { _overlayErrorLogged = true; _log?.LogError(ex, "Overlay {Overlay} failed", o.GetType().FullName); }
                }
            }
            if (shifted) dc.Pop();
        }
    }

    // ================================================================== mysz

    private PagePointerEventArgs MakeArgs(MouseEventArgs e, MouseButton? button, int clickCount)
    {
        var vp = e.GetPosition(_root);
        TryHitPage(vp, out int page, out var pp);
        double pressure = 0.5;
        if (e.StylusDevice is { } stylus)
        {
            var pts = stylus.GetStylusPoints(_root);
            if (pts.Count > 0) pressure = pts[^1].PressureFactor;
        }
        return new PagePointerEventArgs(page, pp, vp, button, Keyboard.Modifiers, clickCount, pressure);
    }

    internal bool HandlePointerPressed(MouseButtonEventArgs e)
    {
        if (_doc is null || e.ChangedButton == MouseButton.Right) return false;
        var args = MakeArgs(e, e.ChangedButton, e.ClickCount);
        var tool = e.ChangedButton == MouseButton.Middle ? _hand : CurrentTool;
        _middleHand = e.ChangedButton == MouseButton.Middle;
        return SafeTool(() => tool.OnPointerPressed(this, args));
    }

    private bool _middleHand;

    internal void HandlePointerMoved(MouseEventArgs e)
    {
        if (_doc is null) return;
        var args = MakeArgs(e, null, 0);
        var tool = _middleHand ? _hand : CurrentTool;
        if (tool is not HandTool && _hoverCursor is not null) SetHoverCursor(null);
        SafeTool(() => tool.OnPointerMoved(this, args));
    }

    internal void HandlePointerReleased(MouseButtonEventArgs e)
    {
        if (_doc is null) return;
        var args = MakeArgs(e, e.ChangedButton, e.ClickCount);
        var tool = _middleHand ? _hand : CurrentTool;
        _middleHand = false;
        SafeTool(() => tool.OnPointerReleased(this, args));
    }

    private bool SafeTool(Func<bool> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Tool {Tool} failed", CurrentTool.Id);
            _services.GetService<IDialogService>()?.ShowError(ex);
            return false;
        }
    }

    internal bool HandleWheel(MouseWheelEventArgs e)
    {
        if (_doc is null) return false;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double factor = Math.Pow(1.1, e.Delta / 120.0);
            SetZoom(_zoom * factor, e.GetPosition(_presenter), ZoomMode.Custom);
            return true;
        }
        if (!PageLayout.IsContinuous(_layoutMode))
        {
            if (e.Delta < 0 && _presenter.IsAtBottom) return NextSpread(+1, toBottom: false);
            if (e.Delta > 0 && _presenter.IsAtTop) return NextSpread(-1, toBottom: true);
        }
        return false;
    }

    private bool NextSpread(int direction, bool toBottom)
    {
        int step = PageLayout.IsTwoUp(_layoutMode) ? 2 : 1;
        int target = PageLayout.SpreadStart(_currentPage, _layoutMode) + direction * step;
        if (target < 0 || target >= _pages.Count) return false;
        GoToPage(target);
        if (toBottom) _presenter.SetOffset(new Point(_presenter.Offset.X, double.MaxValue));
        return true;
    }

    internal bool HandleNavigationKey(KeyEventArgs e)
    {
        if (_pages.Count == 0) return false;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Home: GoToPage(0); return true;
                case Key.End: GoToPage(_pages.Count - 1); return true;
                case Key.PageUp: NavigatePage(-1); return true;
                case Key.PageDown: NavigatePage(+1); return true;
            }
        }
        if (mods == ModifierKeys.None && !PageLayout.IsContinuous(_layoutMode))
        {
            if (e.Key is Key.PageDown or Key.Next && _presenter.IsAtBottom) return NextSpread(+1, false);
            if (e.Key is Key.PageUp or Key.Prior && _presenter.IsAtTop) return NextSpread(-1, true);
            if (e.Key == Key.Home) { GoToPage(0); return true; }
            if (e.Key == Key.End) { GoToPage(_pages.Count - 1); return true; }
        }
        return false;
    }

    /// <summary>Następna/poprzednia strona (w trybach dwustronicowych – rozkładówka).</summary>
    internal void NavigatePage(int direction)
    {
        int step = PageLayout.IsTwoUp(_layoutMode) ? 2 : 1;
        GoToPage(PageLayout.SpreadStart(_currentPage, _layoutMode) + direction * step);
    }

    // ================================================================== klawiatura

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Ui.IsTextInputFocused(Keyboard.FocusedElement)) return;
        if (_doc is null) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && CurrentTool is not HandTool)
        {
            if (!_spaceHand) { _spaceHand = true; UpdateCursor(); }
            e.Handled = true;
            return;
        }
        if (SafeTool(() => CurrentTool.OnKeyDown(this, e))) { e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            if (_find.IsOpen) { _find.Close(); e.Handled = true; }
            else if (_selection is not null) { ClearTextSelection(); e.Handled = true; }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHand)
        {
            _spaceHand = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void OnActiveToolChanged(object? sender, EventArgs e)
    {
        _hoverCursor = null;
        UpdateCursor();
        InvalidateOverlays();
        if (_tools.ActiveTool is SelectTextTool) PrefetchVisibleText();
    }

    private void PrefetchVisibleText()
    {
        if (_doc is null) return;
        foreach (int p in VisiblePages(new Rect(_presenter.Viewport))) _ = EnsureText(p);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var registry = _services.GetService<ICommandRegistry>();
        var menu = _presenter.ContextMenu!;
        menu.Items.Clear();
        void Add(string id, string key)
        {
            var item = new MenuItem { Command = registry?.GetHandler(id) }.Loc(HeaderedItemsControl.HeaderProperty, _loc, key);
            if (item.Command is null) item.IsEnabled = false;
            menu.Items.Add(item);
        }
        Add(CommandIds.Edit.Copy, "Viewer.Menu.Copy");
        Add(CommandIds.Edit.SelectAll, "Viewer.Menu.SelectAll");
        menu.Items.Add(new Separator());
        Add(CommandIds.Tool.Hand, "Viewer.Menu.Hand");
        Add(CommandIds.Tool.SelectText, "Viewer.Menu.SelectText");
        Add(CommandIds.Tool.Snapshot, "Viewer.Menu.Snapshot");
        menu.Items.Add(new Separator());
        Add(CommandIds.Nav.Find, "Viewer.Menu.Find");
        Add(CommandIds.Nav.AddBookmark, "Viewer.Menu.AddBookmark");
        Add(CommandIds.File.Print, "Viewer.Menu.Print");
    }

    // ================================================================== zaznacz wszystko

    internal async Task SelectAllAsync()
    {
        var doc = _doc;
        if (doc is null) return;
        var tasks = Enumerable.Range(0, doc.PageCount).Select(EnsureText).ToArray();
        await Task.WhenAll(tasks);
        if (!ReferenceEquals(doc, _doc)) return;
        int last = doc.PageCount - 1;
        SetTextSelection(TextGeometry.BuildSelection(new TextCaret(0, 0), new TextCaret(last, GetCachedText(last)?.Text.Length ?? 0), GetCachedText));
    }

    // ================================================================== przeładowanie po zmianie sesji

    private void OnSessionChanged(object? sender, DocumentChangedEventArgs e) => _ = ReloadAsync(e.Kind, e.AffectedPages);

    private async Task ReloadAsync(DocumentChangeKind kind, IReadOnlyList<int>? affected)
    {
        int version = ++_reloadVersion;
        IRenderDocument doc;
        try
        {
            doc = await _renderer.OpenAsync(Session.Current.GetBytes());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (version != _reloadVersion || _disposed) return;
            _log?.LogError(ex, "Cannot open document in view");
            BindingOperationsClear(_status);
            _status.Text = _loc.Format("Viewer.LoadError", ex.Message);
            _status.Visibility = Visibility.Visible;
            return;
        }
        if (version != _reloadVersion || _disposed)
        {
            doc.Dispose();
            return;
        }

        var old = _doc;
        CancelAllRenders();
        _generation++;
        _failed.Clear();
        _doc = doc;
        bool structure = old is null || (kind & (DocumentChangeKind.PageStructure)) != 0 || old.PageCount != doc.PageCount;
        bool content = (kind & (DocumentChangeKind.PageContent | DocumentChangeKind.Annotations | DocumentChangeKind.FormFields | DocumentChangeKind.PageStructure)) != 0;
        if (structure || content)
        {
            if (!structure && affected is { Count: > 0 })
            {
                foreach (int p in affected) { _hi.Remove(p, KindHigh); _lo.Remove(p, KindLow); }
            }
            else
            {
                _hi.Clear();
                _lo.Clear();
            }
            _texts.Clear();
            _textTasks.Clear();
            _links.Clear();
            if (_selection is not null) SetTextSelection(null);
        }
        _pages = doc.Pages;
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _pages.Count - 1));
        if (structure) _selectedPages = _selectedPages.Where(i => i < _pages.Count).ToArray();
        _status.Visibility = Visibility.Collapsed;
        RecomputeFitZoom();
        RebuildLayout();
        if (old is null && _presenter.Viewport.Width > 0 && !_initialPlacementDone)
        {
            _initialPlacementDone = true;
            GoToPage(0);
        }
        old?.Dispose();
        _presenter.InvalidateAll();
        if (_find.IsOpen && (structure || content)) _find.Refresh();
        DocumentReloaded?.Invoke(this, new DocumentReloadedEventArgs(old is null ? DocumentChangeKind.All : kind, affected));
        ViewStateChanged?.Invoke(this, EventArgs.Empty);
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    private static void BindingOperationsClear(TextBlock t) => System.Windows.Data.BindingOperations.ClearBinding(t, TextBlock.TextProperty);

    // ================================================================== Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Session.Changed -= OnSessionChanged;
        _tools.ActiveToolChanged -= OnActiveToolChanged;
        _find.Close();
        CancelAllRenders();
        _generation++;
        _hi.Clear();
        _lo.Clear();
        _texts.Clear();
        _overlays.Clear();
        _doc?.Dispose();
        _doc = null;
        _pages = Array.Empty<PageInfo>();
    }

    /// <summary>Diagnostyka (testy): zużycie pamięci podręcznej i liczba renderów w toku.</summary>
    internal (long HighBytes, long LowBytes, int Pending, int HighCount) CacheStats => (_hi.UsedBytes, _lo.UsedBytes, _pending.Count, _hi.Count);
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Panels;

/// <summary>Element listy miniatur.</summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private string _label = string.Empty;

    public ThumbnailItem(int pageIndex, double width, double height)
    {
        PageIndex = pageIndex;
        Width = width;
        Height = height;
        _label = (pageIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    public int PageIndex { get; }
    public double Width { get; }
    public double Height { get; }
    public string Label { get => _label; set => Set(ref _label, value); }
    public BitmapSource? Image { get => _image; set => Set(ref _image, value); }

    internal bool IsRealized { get; set; }
    internal CancellationTokenSource? Pending { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Panel miniatur: lista wirtualizowana (renderowane są tylko widoczne miniatury, w tle, z anulowaniem po przewinięciu),
/// zaznaczanie wielu stron (→ <see cref="IDocumentView.SelectedPages"/>), zmiana kolejności przeciąganiem
/// (<see cref="IPageOperations.MoveAsync"/> jako polecenie z Cofnij/Ponów) i menu kontekstowe wykonujące polecenia
/// stron z rejestru (implementuje je pages-engine; bez obsługi pozycje są wyszarzone).
/// </summary>
public partial class ThumbnailsPanel : UserControl
{
    public const double ThumbWidth = 116;
    private const int MaxImages = 160;
    private const string DragFormat = "ZetPDF.ThumbnailPages";

    private readonly IServiceProvider _services;
    private readonly IViewerService _viewer;
    private readonly ObservableCollection<ThumbnailItem> _items = new();
    private readonly LinkedList<ThumbnailItem> _withImages = new();
    private DocumentView? _view;
    private bool _syncing;
    private Point _dragStart;
    private bool _dragCandidate;
    private IReadOnlyList<int>? _selectAfterReload;

    public ThumbnailsPanel(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _viewer = services.GetRequiredService<IViewerService>();
        List.ItemsSource = _items;
        List.SelectionChanged += OnSelectionChanged;
        List.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        List.PreviewMouseMove += OnPreviewMouseMove;
        List.DragOver += OnDragOver;
        List.DragLeave += (_, _) => DropLine.Visibility = Visibility.Collapsed;
        List.Drop += OnDrop;
        List.KeyDown += OnListKeyDown;
        _viewer.ActiveViewChanged += (_, v) => Attach(v as DocumentView);
        Attach(_viewer.ActiveView as DocumentView);
    }

    internal IReadOnlyList<ThumbnailItem> Items => _items;

    private void Attach(DocumentView? view)
    {
        if (ReferenceEquals(view, _view)) return;
        if (_view is not null)
        {
            _view.DocumentReloaded -= OnReloaded;
            _view.CurrentPageChanged -= OnCurrentPageChanged;
            _view.SelectedPagesChanged -= OnViewSelectionChanged;
        }
        _view = view;
        if (view is not null)
        {
            view.DocumentReloaded += OnReloaded;
            view.CurrentPageChanged += OnCurrentPageChanged;
            view.SelectedPagesChanged += OnViewSelectionChanged;
        }
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var i in _items) i.Pending?.Cancel();
        _items.Clear();
        _withImages.Clear();
        var doc = _view?.RenderDocument;
        EmptyText.Visibility = doc is null ? Visibility.Visible : Visibility.Collapsed;
        if (doc is null) return;
        foreach (var p in doc.Pages)
        {
            var s = p.DisplaySize;
            double h = Math.Min(ThumbWidth * 2, ThumbWidth * s.Height / s.Width);
            double w = h < ThumbWidth * s.Height / s.Width ? h * s.Width / s.Height : ThumbWidth;
            _items.Add(new ThumbnailItem(p.Index, w, h));
        }
        SyncSelectionFromView(scroll: true);
    }

    private void OnReloaded(object? sender, DocumentReloadedEventArgs e)
    {
        var doc = _view?.RenderDocument;
        bool structure = doc is null || e.Kind.HasFlag(DocumentChangeKind.PageStructure) || doc.PageCount != _items.Count;
        if (structure)
        {
            Rebuild();
            if (_selectAfterReload is { } sel && _view is not null)
            {
                _selectAfterReload = null;
                _view.SelectedPages = sel;
                SyncSelectionFromView(scroll: true);
            }
            return;
        }
        if ((e.Kind & (DocumentChangeKind.PageContent | DocumentChangeKind.Annotations | DocumentChangeKind.FormFields)) == 0) return;
        var pages = e.AffectedPages ?? Enumerable.Range(0, _items.Count).ToArray();
        foreach (int p in pages)
        {
            if ((uint)p >= (uint)_items.Count) continue;
            var item = _items[p];
            item.Pending?.Cancel();
            item.Pending = null;
            item.Image = null;
            if (item.IsRealized) RequestThumb(item);
        }
    }

    // ------------------------------------------------------------------ miniatury

    private void OnThumbLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThumbnailItem item) return;
        item.IsRealized = true;
        if (item.Image is null) RequestThumb(item);
    }

    private void OnThumbUnloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThumbnailItem item) return;
        item.IsRealized = false;
        item.Pending?.Cancel();
        item.Pending = null;
    }

    private async void RequestThumb(ThumbnailItem item)
    {
        var view = _view;
        if (view is null || item.Pending is not null) return;
        var cts = new CancellationTokenSource();
        item.Pending = cts;
        try
        {
            // ustępujemy pierwszeństwa renderom widoku głównego
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (cts.IsCancellationRequested || !ReferenceEquals(view, _view)) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var img = await view.RenderThumbnailAsync(item.PageIndex, (int)Math.Ceiling(item.Width * dpi), (int)Math.Ceiling(item.Height * dpi), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(view, _view) || img is null) return;
            item.Image = img;
            _withImages.AddLast(item);
            TrimImages();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (PdfCorruptException) { }
        finally
        {
            if (ReferenceEquals(item.Pending, cts)) item.Pending = null;
            cts.Dispose();
        }
    }

    /// <summary>Limit pamięci: zwalniamy obrazy najdawniej wyrenderowanych, niewidocznych miniatur.</summary>
    private void TrimImages()
    {
        var node = _withImages.First;
        while (_withImages.Count > MaxImages && node is not null)
        {
            var next = node.Next;
            if (!node.Value.IsRealized)
            {
                node.Value.Image = null;
                _withImages.Remove(node);
            }
            node = next;
        }
    }

    // ------------------------------------------------------------------ zaznaczenie i nawigacja

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _view is null) return;
        var pages = List.SelectedItems.Cast<ThumbnailItem>().Select(i => i.PageIndex).OrderBy(i => i).ToArray();
        _syncing = true;
        try
        {
            _view.SelectedPages = pages;
            // zmiana zaznaczenia przez użytkownika (programowe zmiany idą pod flagą _syncing) – jedna strona = przejdź do niej
            if (pages.Length == 1) _view.GoToPage(pages[0]);
        }
        finally { _syncing = false; }
    }

    private void OnCurrentPageChanged(object? sender, EventArgs e)
    {
        if (_syncing || _view is null || List.SelectedItems.Count > 1) return;
        SyncSelectionFromView(scroll: true, single: _view.CurrentPageIndex);
    }

    private void OnViewSelectionChanged(object? sender, EventArgs e)
    {
        if (!_syncing) SyncSelectionFromView(scroll: false);
    }

    private void SyncSelectionFromView(bool scroll, int? single = null)
    {
        if (_view is null || _items.Count == 0) return;
        var pages = single is int s ? new[] { s } : _view.SelectedPages;
        _syncing = true;
        try
        {
            List.SelectedItems.Clear();
            foreach (int p in pages) if ((uint)p < (uint)_items.Count) List.SelectedItems.Add(_items[p]);
            if (scroll && pages.Count > 0 && (uint)pages[0] < (uint)_items.Count) List.ScrollIntoView(_items[pages[0]]);
        }
        finally { _syncing = false; }
    }

    private IReadOnlyList<int> SelectedPageIndices() =>
        List.SelectedItems.Cast<ThumbnailItem>().Select(i => i.PageIndex).OrderBy(i => i).ToArray();

    // ------------------------------------------------------------------ polecenia stron (rejestr)

    private bool CanRun(string id) => _services.GetService<ICommandRegistry>()?.GetHandler(id)?.CanExecute(null) == true;

    private void Run(string id)
    {
        if (_view is null) return;
        var sel = SelectedPageIndices();
        if (sel.Count > 0) _view.SelectedPages = sel;
        _services.GetService<ICommandRegistry>()?.TryExecute(id);
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuRotateLeft.IsEnabled = CanRun(CommandIds.Pages.RotateLeft);
        MenuRotateRight.IsEnabled = CanRun(CommandIds.Pages.RotateRight);
        MenuInsert.IsEnabled = CanRun(CommandIds.Pages.InsertBlank);
        MenuDuplicate.IsEnabled = CanRun(CommandIds.Pages.Duplicate);
        MenuExtract.IsEnabled = CanRun(CommandIds.Pages.Extract);
        MenuDelete.IsEnabled = CanRun(CommandIds.Pages.Delete);
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Run(CommandIds.Pages.RotateLeft);
    private void OnRotateRight(object sender, RoutedEventArgs e) => Run(CommandIds.Pages.RotateRight);
    private void OnDelete(object sender, RoutedEventArgs e) => Run(CommandIds.Pages.Delete);
    private void OnInsertBlank(object sender, RoutedEventArgs e) => Run(CommandIds.Pages.InsertBlank);
    private void OnExtract(object sender, RoutedEventArgs e) => Run(CommandIds.Pages.Extract);
    private void OnDuplicate(object sender, RoutedEventArgs e) => Run(CommandIds.Pages.Duplicate);

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Run(CommandIds.Pages.Delete); e.Handled = true; }
        else if (e.Key == Key.Enter && List.SelectedItem is ThumbnailItem it) { _view?.GoToPage(it.PageIndex); e.Handled = true; }
    }

    // ------------------------------------------------------------------ przeciąganie (zmiana kolejności)

    private ListBoxItem? _deferredSingleSelect;

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(List);
        var container = ContainerAt(_dragStart);
        _dragCandidate = container is not null;
        _deferredSingleSelect = null;
        // klik w już zaznaczoną miniaturę przy zaznaczeniu wielu: nie gubimy zaznaczenia (może zacząć się przeciąganie)
        if (container is { IsSelected: true } && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferredSingleSelect = container;
            container.Focus();
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_deferredSingleSelect is { } c && c.DataContext is ThumbnailItem item)
        {
            _deferredSingleSelect = null;
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        }
        _dragCandidate = false;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed || _view is null) return;
        var d = e.GetPosition(List) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = false;
        _deferredSingleSelect = null;
        var pages = SelectedPageIndices().ToArray();
        if (pages.Length == 0) return;
        DragDrop.DoDragDrop(List, new DataObject(DragFormat, pages), DragDropEffects.Move);
        DropLine.Visibility = Visibility.Collapsed;
    }

    private ListBoxItem? ContainerAt(Point p)
    {
        var hit = List.InputHitTest(p) as DependencyObject;
        while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return hit as ListBoxItem;
    }

    /// <summary>Indeks „wstaw przed” dla punktu (semantyka IPageOperations.MoveAsync).</summary>
    private int InsertionIndex(Point p, out double lineY)
    {
        lineY = 0;
        var container = ContainerAt(p);
        if (container is null || container.DataContext is not ThumbnailItem item)
        {
            if (_items.Count > 0 && List.ItemContainerGenerator.ContainerFromIndex(_items.Count - 1) is ListBoxItem last)
                lineY = last.TranslatePoint(new Point(0, last.ActualHeight), DropLayer).Y;
            return _items.Count;
        }
        var top = container.TranslatePoint(new Point(0, 0), DropLayer).Y;
        bool after = p.Y - container.TranslatePoint(new Point(0, 0), List).Y > container.ActualHeight / 2;
        lineY = after ? top + container.ActualHeight : top;
        return item.PageIndex + (after ? 1 : 0);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) || _view is null || !_view.Session.Can(DocumentPermissions.AssembleDocument))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        InsertionIndex(e.GetPosition(List), out double y);
        DropLine.Width = Math.Max(0, List.ActualWidth - 24);
        Canvas.SetLeft(DropLine, 8);
        Canvas.SetTop(DropLine, y - 1.5);
        DropLine.Visibility = Visibility.Visible;
        AutoScroll(e.GetPosition(List));
        e.Handled = true;
    }

    private void AutoScroll(Point p)
    {
        if (FindScrollViewer(List) is not { } sv) return;
        if (p.Y < 24) sv.ScrollToVerticalOffset(sv.VerticalOffset - 12);
        else if (p.Y > List.ActualHeight - 24) sv.ScrollToVerticalOffset(sv.VerticalOffset + 12);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        if (_view is null || e.Data.GetData(DragFormat) is not int[] pages || pages.Length == 0) return;
        int target = InsertionIndex(e.GetPosition(List), out _);
        e.Handled = true;
        await MovePagesAsync(_view, pages, target);
    }

    /// <summary>Przenosi strony (polecenie „Viewer.Undo.MovePages”). Brak zmiany, gdy upuszczono w miejscu.</summary>
    internal async Task<bool> MovePagesAsync(DocumentView view, IReadOnlyList<int> pages, int target)
    {
        var sorted = pages.Distinct().OrderBy(i => i).ToArray();
        bool contiguous = sorted[^1] - sorted[0] == sorted.Length - 1;
        if (contiguous && target >= sorted[0] && target <= sorted[^1] + 1) return false;
        var ops = _services.GetService<IPageOperations>();
        var dialogs = _services.GetService<IDialogService>();
        if (ops is null) return false;
        int newStart = target - sorted.Count(p => p < target);
        _selectAfterReload = Enumerable.Range(newStart, sorted.Length).ToArray();
        try
        {
            var cmd = new DelegateDocumentCommand("Viewer.Undo.MovePages",
                (pdf, _, ct) => ops.MoveAsync(pdf, sorted, target, ct),
                DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);
            return await view.Session.ExecuteAsync(cmd);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _selectAfterReload = null;
            dialogs?.ShowError(ex);
            return false;
        }
    }
}

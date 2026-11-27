using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Viewer.Bookmarks;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Panels;

/// <summary>Węzeł drzewa zakładek w panelu.</summary>
public sealed class BookmarkItem : INotifyPropertyChanged
{
    private bool _isExpanded, _isSelected, _isEditing;
    private string _editText = string.Empty;

    public BookmarkItem(BookmarkNode node, IReadOnlyList<int> path)
    {
        Node = node;
        Path = path;
        _isExpanded = node.IsOpen;
        Children = new ObservableCollection<BookmarkItem>(node.Children.Select((c, i) => new BookmarkItem(c, path.Append(i).ToArray())));
    }

    public BookmarkNode Node { get; }
    public IReadOnlyList<int> Path { get; }
    public ObservableCollection<BookmarkItem> Children { get; }
    public string Title => Node.Title;
    public FontWeight Weight => Node.Bold ? FontWeights.Bold : FontWeights.Normal;
    public FontStyle Style => Node.Italic ? FontStyles.Italic : FontStyles.Normal;
    public bool HasColor => Node.Color is not null;
    public System.Windows.Media.Brush? ColorBrush => Node.Color is { } c ? BookmarkColors.Brush(c) : null;

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }

    /// <summary>Węzeł z bieżącym stanem rozwinięcia (zapisywany w PDF jako /Count &gt; 0).</summary>
    public BookmarkNode ToNode() => Node with { IsOpen = IsExpanded && Children.Count > 0, Children = Children.Select(c => c.ToNode()).ToArray() };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Collapsed;
}

/// <summary>
/// Panel zakładek: drzewo z dokumentu (PDFsharp, <see cref="IBookmarkService"/>), kliknięcie przechodzi do celu,
/// dodawanie (Ctrl+B – bieżąca strona), zmiana nazwy (F2), usuwanie (Del), zagnieżdżanie i przesuwanie przeciąganiem
/// (górna/dolna ćwiartka wiersza = przed/za, środek = jako dziecko). Każda zmiana to polecenie z Cofnij/Ponów.
/// </summary>
public partial class BookmarksPanel : UserControl
{
    private const string DragFormat = "ZetPDF.Bookmark";

    private readonly IServiceProvider _services;
    private readonly IViewerService _viewer;
    private readonly IBookmarkService _bookmarks;
    private readonly ILocalizationService _loc;
    private readonly ObservableCollection<BookmarkItem> _items = new();
    private DocumentView? _view;
    private int _loadVersion;
    private IReadOnlyList<int>? _selectAfterLoad;
    private bool _editAfterLoad;
    private Point _dragStart;
    private BookmarkItem? _dragItem;

    public BookmarksPanel(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _viewer = services.GetRequiredService<IViewerService>();
        _bookmarks = services.GetRequiredService<IBookmarkService>();
        _loc = services.GetRequiredService<ILocalizationService>();
        Tree.ItemsSource = _items;
        Tree.ItemContainerStyle = CreateContainerStyle();
        Tree.PreviewMouseLeftButtonDown += OnTreeMouseDown;
        Tree.PreviewMouseMove += OnTreeMouseMove;
        Tree.MouseLeftButtonUp += OnTreeMouseUp;
        Tree.KeyDown += OnTreeKeyDown;
        Tree.DragOver += OnDragOver;
        Tree.DragLeave += (_, _) => HideDrop();
        Tree.Drop += OnDrop;
        _viewer.ActiveViewChanged += (_, v) => Attach(v as DocumentView);
        Instance = new WeakReference<BookmarksPanel>(this);
        Attach(_viewer.ActiveView as DocumentView);
    }

    /// <summary>Ostatnio utworzony panel (polecenie Ctrl+B wstawia za zaznaczoną zakładką i zaczyna edycję nazwy).</summary>
    internal static WeakReference<BookmarksPanel>? Instance { get; private set; }

    internal IReadOnlyList<BookmarkItem> Items => _items;

    private Style CreateContainerStyle()
    {
        var baseStyle = TryFindResource(typeof(TreeViewItem)) as Style;
        var style = new Style(typeof(TreeViewItem), baseStyle);
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(BookmarkItem.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(BookmarkItem.IsSelected)) { Mode = BindingMode.TwoWay }));
        return style;
    }

    private void Attach(DocumentView? view)
    {
        if (ReferenceEquals(view, _view)) return;
        if (_view is not null) _view.DocumentReloaded -= OnReloaded;
        _view = view;
        if (view is not null) view.DocumentReloaded += OnReloaded;
        _ = LoadAsync();
    }

    private void OnReloaded(object? sender, DocumentReloadedEventArgs e)
    {
        if ((e.Kind & (DocumentChangeKind.Bookmarks | DocumentChangeKind.PageStructure)) != 0) _ = LoadAsync();
    }

    private bool CanEdit => _view?.Session.Can(DocumentPermissions.AssembleDocument) == true;

    /// <summary>Wczytuje drzewo z bieżącego zrzutu sesji.</summary>
    internal async Task LoadAsync()
    {
        int version = ++_loadVersion;
        var view = _view;
        if (view is null)
        {
            _items.Clear();
            EmptyText.Text = _loc["Viewer.Panel.NoDocument"];
            EmptyText.Visibility = Visibility.Visible;
            UpdateButtons();
            return;
        }
        IReadOnlyList<BookmarkNode> tree;
        try { tree = await _bookmarks.GetBookmarksAsync(view.Session.Current.GetSharedBytes()); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            tree = Array.Empty<BookmarkNode>();
            _services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(typeof(BookmarksPanel).FullName!).LogWarningSafe(ex);
        }
        if (version != _loadVersion) return;
        _items.Clear();
        for (int i = 0; i < tree.Count; i++) _items.Add(new BookmarkItem(tree[i], new[] { i }));
        EmptyText.Text = _loc["Viewer.Bookmarks.Empty"];
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selectAfterLoad is { } path && Find(path) is { } item)
        {
            ExpandTo(path);
            item.IsSelected = true;
            if (_editAfterLoad) BeginEdit(item);
        }
        _selectAfterLoad = null;
        _editAfterLoad = false;
        UpdateButtons();
    }

    private IReadOnlyList<BookmarkNode> CurrentTree() => _items.Select(i => i.ToNode()).ToArray();

    private BookmarkItem? Selected => Tree.SelectedItem as BookmarkItem;

    private BookmarkItem? Find(IReadOnlyList<int> path)
    {
        IList<BookmarkItem> list = _items;
        BookmarkItem? node = null;
        foreach (int i in path)
        {
            if ((uint)i >= (uint)list.Count) return null;
            node = list[i];
            list = node.Children;
        }
        return node;
    }

    private void ExpandTo(IReadOnlyList<int> path)
    {
        for (int k = 1; k < path.Count; k++) if (Find(path.Take(k).ToArray()) is { } p) p.IsExpanded = true;
    }

    private void UpdateButtons()
    {
        bool doc = _view is not null, edit = CanEdit, sel = Selected is not null;
        AddButton.IsEnabled = doc && edit;
        AddChildButton.IsEnabled = doc && edit && sel;
        RenameButton.IsEnabled = edit && sel;
        DeleteButton.IsEnabled = edit && sel;
        ExpandButton.IsEnabled = CollapseButton.IsEnabled = _items.Count > 0;
    }

    private async Task<bool> CommitAsync(IReadOnlyList<BookmarkNode> tree, string undoKey, IReadOnlyList<int>? select, bool edit = false)
    {
        if (_view is null) return false;
        _selectAfterLoad = select;
        _editAfterLoad = edit;
        try
        {
            bool changed = await BookmarkTree.CommitAsync(_view.Session, _bookmarks, tree, undoKey);
            if (!changed) { _selectAfterLoad = null; _editAfterLoad = false; }
            return changed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _selectAfterLoad = null;
            _services.GetService<IDialogService>()?.ShowError(ex);
            await LoadAsync();
            return false;
        }
    }

    // ------------------------------------------------------------------ dodawanie / zmiana nazwy / usuwanie

    /// <summary>Tytuł nowej zakładki: zaznaczony tekst (skrócony) albo „Strona N”.</summary>
    internal static string DefaultTitle(IDocumentView view, ILocalizationService loc)
    {
        var text = view.TextSelection?.Text?.Replace("\r", " ").Replace("\n", " ").Trim();
        if (!string.IsNullOrEmpty(text)) return text.Length > 80 ? text[..80].TrimEnd() + "…" : text;
        return loc.Format("Viewer.Bookmarks.DefaultTitle", view.CurrentPageIndex + 1);
    }

    /// <summary>Dodaje zakładkę do bieżącej strony (za zaznaczoną albo na końcu) i zaczyna edycję nazwy.</summary>
    internal async Task AddBookmarkAsync(bool asChild = false)
    {
        if (_view is null || !CanEdit) return;
        var node = new BookmarkNode(DefaultTitle(_view, _loc), _view.CurrentPageIndex, BookmarkNode.NoChildren);
        var tree = CurrentTree();
        IReadOnlyList<int> parent;
        int index;
        if (Selected is { } sel)
        {
            if (asChild) { parent = sel.Path; index = sel.Children.Count; }
            else { parent = sel.Path.Take(sel.Path.Count - 1).ToArray(); index = sel.Path[^1] + 1; }
        }
        else { parent = Array.Empty<int>(); index = tree.Count; }
        var newTree = BookmarkTree.Insert(tree, parent, index, node);
        await CommitAsync(newTree, "Viewer.Undo.AddBookmark", parent.Append(index).ToArray(), edit: true);
    }

    private async void OnAdd(object sender, RoutedEventArgs e) => await AddBookmarkAsync();
    private async void OnAddChild(object sender, RoutedEventArgs e) => await AddBookmarkAsync(asChild: true);

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item && CanEdit) BeginEdit(item);
    }

    private static void BeginEdit(BookmarkItem item)
    {
        item.EditText = item.Title;
        item.IsEditing = true;
    }

    private void OnEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
        {
            tb.Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private async void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BookmarkItem item) return;
        if (e.Key == Key.Enter) { e.Handled = true; await EndEdit(item, commit: true); Tree.Focus(); }
        else if (e.Key == Key.Escape) { e.Handled = true; await EndEdit(item, commit: false); Tree.Focus(); }
    }

    private async void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookmarkItem { IsEditing: true } item) await EndEdit(item, commit: true);
    }

    private async Task EndEdit(BookmarkItem item, bool commit)
    {
        if (!item.IsEditing) return;
        item.IsEditing = false;
        var title = item.EditText.Trim();
        if (!commit || title.Length == 0 || title == item.Title) return;
        await CommitAsync(BookmarkTree.Rename(CurrentTree(), item.Path, title), "Viewer.Undo.RenameBookmark", item.Path);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || !CanEdit) return;
        var tree = BookmarkTree.Remove(CurrentTree(), item.Path);
        IReadOnlyList<int>? select = null;
        if (item.Path[^1] > 0) select = item.Path.Take(item.Path.Count - 1).Append(item.Path[^1] - 1).ToArray();
        else if (item.Path.Count > 1) select = item.Path.Take(item.Path.Count - 1).ToArray();
        await CommitAsync(tree, "Viewer.Undo.DeleteBookmark", select);
    }

    private async void OnSetTarget(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || _view is null || !CanEdit) return;
        int page = _view.CurrentPageIndex;
        var tree = BookmarkTree.Replace(CurrentTree(), item.Path, n => n with { PageIndex = page, Target = null });
        await CommitAsync(tree, "Viewer.Undo.RenameBookmark", item.Path);
    }

    private void OnExpandAll(object sender, RoutedEventArgs e) => SetExpanded(_items, true);
    private void OnCollapseAll(object sender, RoutedEventArgs e) => SetExpanded(_items, false);

    private static void SetExpanded(IEnumerable<BookmarkItem> items, bool value)
    {
        foreach (var i in items) { i.IsExpanded = value && i.Children.Count > 0; SetExpanded(i.Children, value); }
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        bool sel = Selected is not null, edit = CanEdit;
        MenuGo.IsEnabled = sel && Selected!.Node.PageIndex >= 0;
        MenuAdd.IsEnabled = _view is not null && edit;
        MenuAddChild.IsEnabled = MenuSetTarget.IsEnabled = MenuRename.IsEnabled = MenuDelete.IsEnabled = sel && edit;
        MenuBold.IsEnabled = MenuItalic.IsEnabled = MenuColor.IsEnabled = sel && edit;
        MenuBold.IsChecked = Selected?.Node.Bold == true;
        MenuItalic.IsChecked = Selected?.Node.Italic == true;
        BuildColorMenu();
    }

    // ------------------------------------------------------------------ styl i kolor

    private void BuildColorMenu()
    {
        MenuColor.Items.Clear();
        var current = Selected?.Node.Color;
        foreach (var (key, color) in BookmarkColors.Palette)
        {
            var mi = new MenuItem
            {
                Header = _loc[key],
                IsChecked = Nullable.Equals(current, color),
                Icon = new System.Windows.Shapes.Rectangle
                {
                    Width = 12, Height = 12,
                    Fill = color is { } c ? BookmarkColors.Brush(c) : null,
                    Stroke = (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary"),
                },
            };
            mi.Click += async (_, _) => await ChangeStyleAsync(n => n with { Color = color });
            MenuColor.Items.Add(mi);
        }
    }

    private async void OnToggleBold(object sender, RoutedEventArgs e) => await ChangeStyleAsync(n => n with { Bold = !n.Bold });
    private async void OnToggleItalic(object sender, RoutedEventArgs e) => await ChangeStyleAsync(n => n with { Italic = !n.Italic });

    private async Task ChangeStyleAsync(Func<BookmarkNode, BookmarkNode> change)
    {
        if (Selected is not { } item || !CanEdit) return;
        await CommitAsync(BookmarkTree.Replace(CurrentTree(), item.Path, change), "Viewer.Undo.BookmarkStyle", item.Path);
    }

    // ------------------------------------------------------------------ nawigacja

    private void OnGo(object sender, RoutedEventArgs e) => Go(Selected);

    private void Go(BookmarkItem? item)
    {
        if (item is null || _view is null || item.Node.PageIndex < 0) return;
        _view.GoToPage(item.Node.PageIndex, item.Node.Target);
    }

    private void OnTreeMouseUp(object sender, MouseButtonEventArgs e)
    {
        UpdateButtons();
        if (ItemAt(e.GetPosition(Tree)) is { IsEditing: false } item && ReferenceEquals(item, Selected)) Go(item);
    }

    private async void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.Enter: Go(Selected); e.Handled = true; break;
            case Key.F2: OnRename(sender, e); e.Handled = true; break;
            case Key.Delete: OnDelete(sender, e); e.Handled = true; break;
            case Key.Up or Key.Down when Keyboard.Modifiers == ModifierKeys.Alt && Selected is { } s && CanEdit:
                int target = s.Path[^1] + (e.Key == Key.Down ? 2 : -1);
                var parent = s.Path.Take(s.Path.Count - 1).ToArray();
                int count = parent.Length == 0 ? _items.Count : Find(parent)!.Children.Count;
                if (target >= 0 && target <= count && BookmarkTree.Move(CurrentTree(), s.Path, parent, target) is { } moved)
                    await CommitAsync(moved.Tree, "Viewer.Undo.MoveBookmark", moved.NewPath);
                e.Handled = true;
                break;
        }
        UpdateButtons();
    }

    // ------------------------------------------------------------------ przeciąganie

    private TreeViewItem? ContainerAt(Point p)
    {
        var hit = Tree.InputHitTest(p) as DependencyObject;
        while (hit is not null and not TreeViewItem) hit = VisualTreeHelper.GetParent(hit);
        return hit as TreeViewItem;
    }

    private BookmarkItem? ItemAt(Point p) => ContainerAt(p)?.DataContext as BookmarkItem;

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragItem = ItemAt(_dragStart);
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed || _dragItem.IsEditing || !CanEdit) return;
        var d = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, item.Path.ToArray()), DragDropEffects.Move);
        HideDrop();
    }

    private enum DropPosition { Before, Inside, After }

    private (BookmarkItem? Item, DropPosition Position, TreeViewItem? Container) DropTarget(Point p)
    {
        var container = ContainerAt(p);
        if (container?.DataContext is not BookmarkItem item) return (null, DropPosition.After, null);
        // wysokość samego wiersza (nagłówka), bez rozwiniętych dzieci
        double rowHeight = container.ActualHeight;
        if (container.IsExpanded && container.Items.Count > 0 && container.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first)
            rowHeight = first.TranslatePoint(new Point(0, 0), container).Y;
        double y = p.Y - container.TranslatePoint(new Point(0, 0), Tree).Y;
        var pos = y < rowHeight * 0.25 ? DropPosition.Before : y > rowHeight * 0.75 ? DropPosition.After : DropPosition.Inside;
        return (item, pos, container);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat) || e.Data.GetData(DragFormat) is not int[] from || !CanEdit) { e.Effects = DragDropEffects.None; HideDrop(); return; }
        var (item, pos, container) = DropTarget(e.GetPosition(Tree));
        if (item is null || container is null || BookmarkTree.IsSameOrDescendant(from, item.Path)) { e.Effects = DragDropEffects.None; HideDrop(); return; }
        e.Effects = DragDropEffects.Move;
        var origin = container.TranslatePoint(new Point(0, 0), DropLayer);
        double rowHeight = container.ActualHeight;
        if (container.IsExpanded && container.Items.Count > 0 && container.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first)
            rowHeight = first.TranslatePoint(new Point(0, 0), container).Y;
        if (pos == DropPosition.Inside)
        {
            DropLine.Visibility = Visibility.Collapsed;
            DropBox.Width = Math.Max(0, Tree.ActualWidth - origin.X - 8);
            DropBox.Height = rowHeight;
            Canvas.SetLeft(DropBox, origin.X);
            Canvas.SetTop(DropBox, origin.Y);
            DropBox.Visibility = Visibility.Visible;
        }
        else
        {
            DropBox.Visibility = Visibility.Collapsed;
            DropLine.Width = Math.Max(0, Tree.ActualWidth - origin.X - 8);
            Canvas.SetLeft(DropLine, origin.X);
            Canvas.SetTop(DropLine, origin.Y + (pos == DropPosition.Before ? 0 : rowHeight) - 1);
            DropLine.Visibility = Visibility.Visible;
        }
    }

    private void HideDrop()
    {
        DropLine.Visibility = Visibility.Collapsed;
        DropBox.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        HideDrop();
        if (e.Data.GetData(DragFormat) is not int[] from) return;
        var (item, pos, _) = DropTarget(e.GetPosition(Tree));
        if (item is null) return;
        e.Handled = true;
        IReadOnlyList<int> parent;
        int index;
        switch (pos)
        {
            case DropPosition.Inside: parent = item.Path; index = item.Children.Count; break;
            case DropPosition.Before: parent = item.Path.Take(item.Path.Count - 1).ToArray(); index = item.Path[^1]; break;
            default: parent = item.Path.Take(item.Path.Count - 1).ToArray(); index = item.Path[^1] + 1; break;
        }
        if (BookmarkTree.Move(CurrentTree(), from, parent, index) is { } moved)
            await CommitAsync(moved.Tree, "Viewer.Undo.MoveBookmark", moved.NewPath);
    }
}

internal static class LoggerExtensions
{
    public static void LogWarningSafe(this Microsoft.Extensions.Logging.ILogger logger, Exception ex) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Bookmarks could not be read");
}

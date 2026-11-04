using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Panels;

/// <summary>
/// Panel Komentarze: lista pogrupowana po stronach, filtr (autor, typ, stan), sortowanie, przejście do komentarza,
/// odpowiedzi (Ctrl+Enter wysyła), stan recenzji, usuwanie, pokaż/ukryj wszystkie, eksport/import.
/// </summary>
internal sealed class CommentsPanel : UserControl
{
    private readonly AnnotContext _ctx;
    private readonly ILocalizationService _loc;
    private readonly ComboBox _author, _kind, _state, _sort;
    private readonly TreeView _tree;
    private readonly TextBox _reply;
    private readonly ComboBox _setState;
    private readonly TextBlock _detail, _empty;
    private readonly Grid _detailPanel;
    private IPdfDocumentSession? _session;
    private IReadOnlyList<AnnotationInfo> _all = Array.Empty<AnnotationInfo>();
    private Dictionary<AnnotationId, Measure.MeasurementInfo> _measures = new();
    private int _refreshVersion;
    private bool _updatingFilters;

    private const string AllTag = "*";

    public CommentsPanel(AnnotContext ctx)
    {
        _ctx = ctx;
        _loc = ctx.Loc;
        var root = new DockPanel();

        // pasek narzędzi
        var bar = new WrapPanel { Margin = new Thickness(4) };
        bar.Children.Add(Ui.IconButton("", _loc, "Cmd.Comment.ShowAll", (_, _) => _ctx.Registry?.TryExecute(CommandIds.Comment.ShowAll)));
        bar.Children.Add(Ui.IconButton("", _loc, "Cmd.Comment.HideAll", (_, _) => _ctx.Registry?.TryExecute(CommandIds.Comment.HideAll)));
        bar.Children.Add(Ui.IconButton("", _loc, "Cmd.Comment.DeleteAll", (_, _) => _ctx.Registry?.TryExecute(CommandIds.Comment.DeleteAll)));
        bar.Children.Add(Ui.IconButton("", _loc, "Cmd.Comment.ExportAll", (_, _) => _ctx.Registry?.TryExecute(CommandIds.Comment.ExportAll)));
        bar.Children.Add(Ui.IconButton("", _loc, "Cmd.Comment.Import", (_, _) => _ctx.Registry?.TryExecute(CommandIds.Comment.Import)));
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var filters = new Grid { Margin = new Thickness(4, 0, 4, 4) };
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        filters.RowDefinitions.Add(new RowDefinition());
        filters.RowDefinitions.Add(new RowDefinition());
        ComboBox Filter(string tipKey, int row, int col)
        {
            var cb = new ComboBox { Margin = new Thickness(2) };
            cb.Tip(_loc, tipKey);
            cb.SelectionChanged += (_, _) => { if (!_updatingFilters) Rebuild(); };
            Grid.SetRow(cb, row);
            Grid.SetColumn(cb, col);
            filters.Children.Add(cb);
            return cb;
        }
        _author = Filter("Annot.Panel.FilterAuthor", 0, 0);
        _kind = Filter("Annot.Panel.FilterType", 0, 1);
        _state = Filter("Annot.Panel.FilterState", 1, 0);
        _sort = Filter("Annot.Panel.Sort", 1, 1);
        DockPanel.SetDock(filters, Dock.Top);
        root.Children.Add(filters);

        // szczegóły / odpowiedź
        _detailPanel = new Grid { Margin = new Thickness(4) };
        _detailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 120, Margin = new Thickness(0, 0, 0, 4) };
        _detail.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
        _detailPanel.Children.Add(_detail);
        var stateRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var stateLabel = Ui.Label(_loc, "Annot.Panel.State");
        stateRow.Children.Add(stateLabel);
        _setState = new ComboBox();
        foreach (var s in Enum.GetValues<AnnotationReviewState>()) _setState.Items.Add(new ComboBoxItem { Tag = s }.Loc(ContentControl.ContentProperty, _loc, "Annot.State." + s));
        _setState.Tip(_loc, "Annot.Panel.State");
        _setState.SelectionChanged += (_, _) => _ = OnSetState();
        stateRow.Children.Add(_setState);
        Grid.SetRow(stateRow, 1);
        _detailPanel.Children.Add(stateRow);
        _reply = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 54, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _reply.Tip(_loc, "Annot.Panel.ReplyHint");
        _reply.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; _ = SendReply(); }
        };
        Grid.SetRow(_reply, 2);
        _detailPanel.Children.Add(_reply);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(Ui.Button(_loc, "Annot.Panel.Reply", (_, _) => _ = SendReply()));
        actions.Children.Add(Ui.Button(_loc, "Annot.Panel.Properties", (_, _) => _ = OpenProperties()));
        actions.Children.Add(Ui.Button(_loc, "Annot.Panel.Delete", (_, _) => _ = DeleteSelected()));
        Grid.SetRow(actions, 3);
        _detailPanel.Children.Add(actions);
        _detailPanel.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_detailPanel, Dock.Bottom);
        root.Children.Add(_detailPanel);

        _empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _empty.Loc(TextBlock.TextProperty, _loc, "Annot.Panel.Empty");
        _tree = new TreeView { BorderThickness = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(_tree, _loc["Annot.Panel.Title"]);
        _tree.SelectedItemChanged += (_, _) => OnSelected();
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Delete) { e.Handled = true; _ = DeleteSelected(); } };
        var host = new Grid();
        host.Children.Add(_tree);
        host.Children.Add(_empty);
        root.Children.Add(host);
        Content = root;
        this.Themed(BackgroundProperty, ThemeKeys.Surface);

        FillStaticFilters();
        _ctx.Documents.ActiveDocumentChanged += (_, d) => Track(d);
        _loc.CultureChanged += (_, _) => { FillStaticFilters(); _ = RefreshAsync(); };
        _ctx.SelectionChanged += (_, _) => SyncSelection();
        Track(_ctx.Documents.ActiveDocument);
    }

    private void FillStaticFilters()
    {
        _updatingFilters = true;
        void Fill(ComboBox cb, IEnumerable<(object Tag, string Text)> items)
        {
            var sel = (cb.SelectedItem as ComboBoxItem)?.Tag;
            cb.Items.Clear();
            foreach (var (tag, text) in items) cb.Items.Add(new ComboBoxItem { Tag = tag, Content = text });
            cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, sel)) ?? cb.Items[0];
        }
        var kinds = new List<(object, string)> { (AllTag, _loc["Annot.Panel.AllTypes"]) };
        kinds.AddRange(Enum.GetValues<AnnotationKind>().Where(Pdf.AnnotModel.IsComment).Select(k => ((object)k, _loc["Annot.Kind." + k])));
        Fill(_kind, kinds);
        var states = new List<(object, string)> { (AllTag, _loc["Annot.Panel.AllStates"]) };
        states.AddRange(Enum.GetValues<AnnotationReviewState>().Select(s => ((object)s, _loc["Annot.State." + s])));
        Fill(_state, states);
        Fill(_sort, new (object, string)[] { ("page", _loc["Annot.Panel.SortPage"]), ("date", _loc["Annot.Panel.SortDate"]), ("author", _loc["Annot.Panel.SortAuthor"]), ("type", _loc["Annot.Panel.SortType"]) });
        _updatingFilters = false;
    }

    private bool _refreshPending;

    /// <summary>Ukryty panel nie parsuje dokumentu po każdej edycji; odświeża się, gdy znów będzie widoczny.</summary>
    private void RequestRefresh()
    {
        if (IsLoaded && !IsVisible) { _refreshPending = true; return; }
        _ = RefreshAsync();
    }

    private void OnPanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || !_refreshPending) return;
        _refreshPending = false;
        _ = RefreshAsync();
    }

    private void Track(IPdfDocumentSession? session)
    {
        IsVisibleChanged -= OnPanelVisibilityChanged;
        IsVisibleChanged += OnPanelVisibilityChanged;
        if (_session is not null) _session.Changed -= OnChanged;
        _session = session;
        if (_session is not null) _session.Changed += OnChanged;
        _ = RefreshAsync();
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if ((e.Kind & (DocumentChangeKind.Annotations | DocumentChangeKind.PageStructure)) != 0 || e.Source != DocumentChangeSource.Command) RequestRefresh();
    }

    private async Task RefreshAsync()
    {
        int v = ++_refreshVersion;
        var s = _session;
        IReadOnlyList<AnnotationInfo> list = Array.Empty<AnnotationInfo>();
        if (s is not null)
        {
            try { list = await _ctx.GetAnnotationsAsync(s); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { list = Array.Empty<AnnotationInfo>(); }
        }
        var measures = new Dictionary<AnnotationId, Measure.MeasurementInfo>();
        if (s is not null && list.Any(a => a.Kind is AnnotationKind.Line or AnnotationKind.Polyline or AnnotationKind.Polygon))
        {
            try { foreach (var m in await _ctx.Measures.GetMeasurementsAsync(s.Current.GetSharedBytes())) measures[m.Id] = m; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        if (v != _refreshVersion) return;
        _measures = measures;
        _all = list.Where(a => Pdf.AnnotModel.IsComment(a.Kind)).ToList();
        _updatingFilters = true;
        var selAuthor = (_author.SelectedItem as ComboBoxItem)?.Tag as string;
        _author.Items.Clear();
        _author.Items.Add(new ComboBoxItem { Tag = AllTag, Content = _loc["Annot.Panel.AllAuthors"] });
        foreach (var a in _all.Select(x => x.Author).Where(x => x.Length > 0).Distinct().OrderBy(x => x)) _author.Items.Add(new ComboBoxItem { Tag = a, Content = a });
        _author.SelectedItem = _author.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selAuthor) ?? _author.Items[0];
        _updatingFilters = false;
        Rebuild();
    }

    private void Rebuild()
    {
        var keep = (_tree.SelectedItem as TreeViewItem)?.Tag as AnnotationInfo;
        _tree.Items.Clear();
        var author = (_author.SelectedItem as ComboBoxItem)?.Tag;
        var kind = (_kind.SelectedItem as ComboBoxItem)?.Tag;
        var state = (_state.SelectedItem as ComboBoxItem)?.Tag;
        var sort = (_sort.SelectedItem as ComboBoxItem)?.Tag as string ?? "page";
        var replies = _all.Where(a => a.InReplyTo is not null).ToLookup(a => a.InReplyTo!.Value.Name);
        var top = _all.Where(a => a.InReplyTo is null)
            .Where(a => author is null or AllTag || Equals(a.Author, author))
            .Where(a => kind is null or AllTag || Equals(a.Kind, kind) || kind is AnnotationKind.Highlight && a.Kind == AnnotationKind.AreaHighlight)
            .Where(a => state is null or AllTag || Equals(a.State, state));
        top = sort switch
        {
            "date" => top.OrderByDescending(a => a.Modified ?? a.Created),
            "author" => top.OrderBy(a => a.Author).ThenBy(a => a.Id.PageIndex),
            "type" => top.OrderBy(a => a.Kind).ThenBy(a => a.Id.PageIndex),
            _ => top.OrderBy(a => a.Id.PageIndex).ThenByDescending(a => a.Rect.Top).ThenBy(a => a.Rect.Left),
        };
        TreeViewItem? toSelect = null;
        foreach (var group in top.GroupBy(a => a.Id.PageIndex).OrderBy(g => sort == "page" ? g.Key : 0))
        {
            var pageItem = new TreeViewItem { IsExpanded = true, Header = _loc.Format("Annot.Panel.Page", group.Key + 1, group.Count()) };
            foreach (var a in group)
            {
                var item = Item(a);
                foreach (var r in replies[a.Id.Name].OrderBy(r => r.Created)) item.Items.Add(Item(r));
                item.IsExpanded = true;
                pageItem.Items.Add(item);
                if (keep is not null && a.Id == keep.Id) toSelect = item;
            }
            _tree.Items.Add(pageItem);
        }
        _empty.Visibility = _tree.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (toSelect is not null) toSelect.IsSelected = true;
        else _detailPanel.Visibility = Visibility.Collapsed;
    }

    private TreeViewItem Item(AnnotationInfo a)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var head = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var when = (a.Modified ?? a.Created)?.LocalDateTime.ToString("g", _loc.CurrentCulture) ?? string.Empty;
        string kindText = a.InReplyTo is not null ? _loc["Annot.Kind.Reply"]
            : _measures.TryGetValue(a.Id, out var measure) ? _loc["Annot.Measure.Kind." + measure.Type] : _loc["Annot.Kind." + a.Kind];
        head.Text = $"{kindText} · {a.Author}  {when}";
        head.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
        panel.Children.Add(head);
        if (a.Contents.Length > 0)
        {
            var body = new TextBlock { Text = a.Contents, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis };
            body.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
            panel.Children.Add(body);
        }
        if (a.State != AnnotationReviewState.None || a.IsHidden)
        {
            var st = new TextBlock { Text = string.Join(" · ", new[] { a.State != AnnotationReviewState.None ? _loc["Annot.State." + a.State] : null, a.IsHidden ? _loc["Annot.Panel.Hidden"] : null }.Where(x => x is not null)) };
            st.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusInfo);
            panel.Children.Add(st);
        }
        var item = new TreeViewItem { Header = panel, Tag = a };
        item.MouseDoubleClick += (_, e) => { if (ReferenceEquals(_tree.SelectedItem, item)) { e.Handled = true; _ = OpenProperties(); } };
        return item;
    }

    private AnnotationInfo? Selected => (_tree.SelectedItem as TreeViewItem)?.Tag as AnnotationInfo;

    private bool _syncing;

    private void OnSelected()
    {
        var a = Selected;
        if (a is null) { _detailPanel.Visibility = Visibility.Collapsed; return; }
        _detailPanel.Visibility = Visibility.Visible;
        _detail.Text = a.Contents.Length > 0 ? a.Contents : _loc["Annot.Kind." + a.Kind];
        _syncing = true;
        _setState.SelectedItem = _setState.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, a.State));
        _syncing = false;
        var parent = a.InReplyTo is { } pid ? _all.FirstOrDefault(x => x.Id == pid) ?? a : a;
        if (_session is { } s && _ctx.Viewer?.GetView(s) is { } view)
        {
            view.GoToPage(parent.Id.PageIndex);
            view.ScrollIntoView(parent.Id.PageIndex, parent.Rect.Inflate(20));
            _ctx.Selection = new SelectedItem(s, parent.Id, null, parent.Id.PageIndex, parent.Rect);
        }
    }

    private void SyncSelection()
    {
        if (_ctx.Selection?.Annotation is not { } id || Selected?.Id == id) return;
        foreach (TreeViewItem page in _tree.Items)
            foreach (TreeViewItem item in page.Items)
                if (item.Tag is AnnotationInfo a && a.Id == id) { item.IsSelected = true; item.BringIntoView(); return; }
    }

    private async Task SendReply()
    {
        var a = Selected;
        var text = _reply.Text.Trim();
        if (a is null || _session is null || text.Length == 0) return;
        var parentId = a.InReplyTo ?? a.Id;
        if (await _ctx.RunAsync(_session, "Annot.Undo.Reply", async (pdf, ct) => (await _ctx.Annotations.AddReplyAsync(pdf, parentId, text, _ctx.Author, ct)).Pdf))
            _reply.Clear();
    }

    private async Task OnSetState()
    {
        if (_syncing || Selected is not { } a || _session is null || _setState.SelectedItem is not ComboBoxItem { Tag: AnnotationReviewState st } || st == a.State) return;
        await _ctx.RunAsync(_session, "Annot.Undo.State", (pdf, ct) => _ctx.Annotations.UpdateAsync(pdf, a.Id, new AnnotationUpdate { State = st }, ct));
    }

    private Task DeleteSelected()
    {
        if (Selected is not { } a || _session is null) return Task.CompletedTask;
        return _ctx.RunAsync(_session, "Annot.Undo.Delete", (pdf, ct) => _ctx.Annotations.DeleteAsync(pdf, new[] { a.Id }, ct), pages: new[] { a.Id.PageIndex });
    }

    private Task OpenProperties()
    {
        if (Selected is not { } a || _session is null) return Task.CompletedTask;
        return AnnotationActions.EditPropertiesAsync(_ctx, _session, a);
    }
}

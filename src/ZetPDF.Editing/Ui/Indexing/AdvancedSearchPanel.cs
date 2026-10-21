using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Editing.Indexing;

namespace ZetPDF.Editing.Ui.Indexing;

/// <summary>
/// Panel wyszukiwania zaawansowanego (Ctrl+Shift+F; panel boczny – niemodalny, bez osobnego okna): bieżący dokument, otwarte dokumenty lub indeks folderu;
/// wyniki z kontekstem i numerem strony; dwuklik/Enter przechodzi do trafienia; eksport do CSV.
/// </summary>
internal sealed class AdvancedSearchPanel : UserControl
{
    private const int MaxHits = 2000;
    private readonly ILocalizationService _loc;
    private readonly IndexController _controller;
    private readonly TextBox _query;
    private readonly RadioButton _current;
    private readonly RadioButton _open;
    private readonly RadioButton _folder;
    private readonly ComboBox _indexes;
    private readonly CheckBox _matchCase;
    private readonly CheckBox _diacritics;
    private readonly CheckBox _whole;
    private readonly ListBox _results;
    private readonly TextBlock _status;
    private readonly Button _export;
    private IReadOnlyList<FullTextHit> _hits = Array.Empty<FullTextHit>();
    private string _lastQuery = string.Empty;
    private TextSearchOptionsEx _lastOptions = new();
    private CancellationTokenSource? _cts;

    public AdvancedSearchPanel(ILocalizationService loc, IndexController controller)
    {
        _loc = loc;
        _controller = controller;
        this.Themed(BackgroundProperty, ThemeKeys.Surface).Themed(ForegroundProperty, ThemeKeys.TextPrimary);

        _query = Ui.Box();
        System.Windows.Automation.AutomationProperties.SetName(_query, loc["Edit.Search.Query"]);
        var search = Ui.Button(loc, "Edit.Search.Find", async (_, _) => await RunSearchAsync(), isDefault: true);
        var queryRow = new DockPanel();
        DockPanel.SetDock(search, Dock.Right);
        queryRow.Children.Add(search);
        queryRow.Children.Add(_query);

        _current = Ui.Radio(loc, "Edit.Search.Scope.Current", "scope", controller.HasActiveDocument);
        _open = Ui.Radio(loc, "Edit.Search.Scope.Open", "scope", false);
        _folder = Ui.Radio(loc, "Edit.Search.Scope.Folder", "scope", !controller.HasActiveDocument);
        _indexes = new ComboBox { MinWidth = 280, Margin = new Thickness(0, 3, 6, 3) };
        System.Windows.Automation.AutomationProperties.SetName(_indexes, loc["Edit.Search.Scope.Folder"]);
        _indexes.SelectionChanged += (_, _) => { if (_indexes.SelectedItem is not null) _folder.IsChecked = true; };
        var build = Ui.Button(loc, "Edit.Search.BuildIndex", async (_, _) =>
        {
            try { await _controller.BuildFolderAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _controller.ShowError(ex); }
        });
        build.Margin = new Thickness(0, 3, 0, 3);
        _indexes.HorizontalAlignment = HorizontalAlignment.Stretch;
        _indexes.MinWidth = 0;
        var folderRow = new StackPanel();
        folderRow.Children.Add(_folder);
        folderRow.Children.Add(_indexes);
        folderRow.Children.Add(build);
        var scope = new StackPanel();
        scope.Children.Add(_current);
        scope.Children.Add(_open);

        _matchCase = Ui.Check(loc, "Edit.Search.MatchCase", false);
        _diacritics = Ui.Check(loc, "Edit.Search.IgnoreDiacritics", false);
        _whole = Ui.Check(loc, "Edit.Search.WholeWords", false);
        foreach (var c in new[] { _matchCase, _diacritics }) c.Margin = new Thickness(0, 4, 16, 4);
        var options = new WrapPanel();
        options.Children.Add(_matchCase);
        options.Children.Add(_diacritics);
        options.Children.Add(_whole);

        _results = new ListBox { Margin = new Thickness(0, 6, 0, 6) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_results, true);
        System.Windows.Automation.AutomationProperties.SetName(_results, loc["Edit.Search.Results"]);
        _results.MouseDoubleClick += async (_, _) => await GoToSelectedAsync();
        _results.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await GoToSelectedAsync(); } };

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _status.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _export = Ui.Button(loc, "Edit.Search.Export", async (_, _) =>
        {
            try { await _controller.ExportCsvAsync(_hits); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _controller.ShowError(ex); }
        });
        _export.IsEnabled = false;
        _export.Margin = new Thickness(0, 4, 0, 0);
        _export.HorizontalAlignment = HorizontalAlignment.Left;
        var bottom = new StackPanel();
        bottom.Children.Add(_status);
        bottom.Children.Add(_export);

        var top = new StackPanel();
        top.Children.Add(Ui.Label(loc, "Edit.Search.Query"));
        top.Children.Add(queryRow);
        top.Children.Add(Ui.Label(loc, "Edit.Search.Scope"));
        top.Children.Add(scope);
        top.Children.Add(folderRow);
        top.Children.Add(options);
        var root = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_results);
        Content = root;

        RefreshIndexes(null);
        Unloaded += (_, _) => _cts?.Cancel();
    }

    public void FocusQuery(string? text)
    {
        if (text is not null) _query.Text = text;
        _query.Focus();
        _query.SelectAll();
    }

    public void RefreshIndexes(string? select)
    {
        var previous = (_indexes.SelectedItem as ComboBoxItem)?.Tag as string;
        _indexes.Items.Clear();
        foreach (var i in _controller.Index.ListIndexes())
        {
            var label = i.Recursive ? _loc.Format("Edit.Search.IndexRecursive", i.Root) : i.Root;
            var item = new ComboBoxItem { Tag = i.Root, Content = label, ToolTip = _loc.Format("Edit.Search.IndexBuilt", i.BuiltUtc.ToLocalTime().ToString("g", _loc.CurrentCulture)) };
            _indexes.Items.Add(item);
            if (string.Equals(i.Root, select ?? previous, StringComparison.OrdinalIgnoreCase)) _indexes.SelectedItem = item;
        }
        if (_indexes.SelectedItem is null && _indexes.Items.Count > 0) _indexes.SelectedIndex = 0;
        if (select is not null) _folder.IsChecked = true;
    }

    private SearchScope Scope => _current.IsChecked == true ? SearchScope.CurrentDocument : _open.IsChecked == true ? SearchScope.OpenDocuments : SearchScope.FolderIndex;

    private async Task RunSearchAsync()
    {
        var query = _query.Text.Trim();
        if (query.Length == 0) { _query.Focus(); return; }
        var folder = (_indexes.SelectedItem as ComboBoxItem)?.Tag as string;
        if (Scope == SearchScope.FolderIndex && folder is null) { _status.Text = _loc["Edit.Search.NoIndex"]; return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var options = new TextSearchOptionsEx { MatchCase = _matchCase.IsChecked == true, IgnoreDiacritics = _diacritics.IsChecked == true, WholeWords = _whole.IsChecked == true, MaxHits = MaxHits };
        _status.Text = _loc["Edit.Search.Searching"];
        _results.Items.Clear();
        _export.IsEnabled = false;
        try
        {
            var (hits, skipped) = await _controller.SearchAsync(Scope, folder, query, options, cts.Token);
            if (cts.IsCancellationRequested) return;
            _hits = hits;
            _lastQuery = query;
            _lastOptions = options;
            foreach (var h in hits) _results.Items.Add(Row(h));
            int files = hits.Select(h => h.Source).Distinct().Count();
            var text = hits.Count == 0 ? _loc["Edit.Search.NoResults"] : _loc.Format("Edit.Search.Count", hits.Count, files);
            if (hits.Count >= MaxHits) text += " " + _loc.Format("Edit.Search.Limit", MaxHits);
            if (skipped > 0) text += " " + _loc.Format("Edit.Search.Skipped", skipped);
            _status.Text = text;
            _export.IsEnabled = hits.Count > 0;
            if (_results.Items.Count > 0) _results.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = string.Empty;
            _controller.ShowError(ex);
        }
    }

    private ListBoxItem Row(FullTextHit h)
    {
        var head = Ui.Text(_loc.Format("Edit.Search.HitHeader", Path.GetFileName(h.DisplayName), (h.PageIndex + 1).ToString(CultureInfo.CurrentCulture)));
        head.FontWeight = FontWeights.SemiBold;
        head.Margin = new Thickness(0);
        var context = new TextBlock { TextWrapping = TextWrapping.Wrap };
        context.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        context.Inlines.Add(new Run(h.Before));
        var match = new Run(h.Match) { FontWeight = FontWeights.Bold };
        match.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextPrimary);
        context.Inlines.Add(match);
        context.Inlines.Add(new Run(h.After));
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(head);
        panel.Children.Add(context);
        var item = new ListBoxItem { Tag = h, Content = panel, ToolTip = h.Source };
        System.Windows.Automation.AutomationProperties.SetName(item, head.Text + " " + h.Context);
        return item;
    }

    private async Task GoToSelectedAsync()
    {
        if ((_results.SelectedItem as ListBoxItem)?.Tag is not FullTextHit hit) return;
        try { await _controller.NavigateAsync(hit, _lastQuery, _lastOptions); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _controller.ShowError(ex); }
    }
}

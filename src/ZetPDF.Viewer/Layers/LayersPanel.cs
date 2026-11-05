using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Theming;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Layers;

/// <summary>
/// Panel Warstwy: drzewo z /Order, pokaż lub ukryj w widoku bez zmiany pliku i bez wpisu w historii, osobne polecenie zapisu do dokumentu.
/// Stan widoczności należy do widoku (<see cref="DocumentView.LayerOverrides"/>), więc każda karta ma własny.
/// </summary>
internal sealed class LayersPanel : UserControl
{
    private readonly ILocalizationService _loc;
    private readonly IViewerService? _viewer;
    private readonly IDialogService? _dialogs;
    private readonly TreeView _tree = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Button _save = new() { Margin = new Thickness(8, 4, 8, 4), Padding = new Thickness(8, 3, 8, 3) };
    private readonly Button _reset = new() { Margin = new Thickness(8, 0, 8, 8), Padding = new Thickness(8, 3, 8, 3) };
    private IPdfDocumentSession? _session;
    private int _generation;

    public LayersPanel(IServiceProvider services)
    {
        _loc = services.GetRequiredService<ILocalizationService>();
        _viewer = services.GetService<IViewerService>();
        _dialogs = services.GetService<IDialogService>();
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 4) }.Loc(TextBlock.TextProperty, _loc, "Viewer.Layers.Note");
        note.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _save.Loc(ContentControl.ContentProperty, _loc, "Viewer.Layers.Save");
        _reset.Loc(ContentControl.ContentProperty, _loc, "Viewer.Layers.Reset");
        _save.Click += async (_, _) => await SaveAsync();
        _reset.Click += async (_, _) => await ResetAsync();
        System.Windows.Automation.AutomationProperties.SetName(_tree, _loc["Viewer.Layers.Title"]);

        var bottom = new StackPanel();
        bottom.Children.Add(note);
        bottom.Children.Add(_save);
        bottom.Children.Add(_reset);
        var root = new DockPanel();
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(_info);
        root.Children.Add(bottom);
        root.Children.Add(_tree);
        Content = root;

        if (_viewer is not null) _viewer.ActiveViewChanged += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => _ = RefreshAsync();
    }

    private DocumentView? View => _viewer?.ActiveView as DocumentView;

    private void OnSessionChanged(object? sender, DocumentChangedEventArgs e) => _ = RefreshAsync();

    /// <summary>Czyta drzewo dla aktywnego dokumentu.</summary>
    public async Task RefreshAsync()
    {
        int generation = ++_generation;
        var view = View;
        var session = view?.Session;
        if (!ReferenceEquals(session, _session))
        {
            if (_session is not null) _session.Changed -= OnSessionChanged;
            _session = session;
            if (session is not null) session.Changed += OnSessionChanged;
        }
        _tree.Items.Clear();
        _save.IsEnabled = _reset.IsEnabled = false;
        if (view is null || session is null)
        {
            _info.Text = _loc["Viewer.Panel.NoDocument"];
            return;
        }
        LayerTree tree;
        try
        {
            tree = await LayerDocument.ReadAsync(session.Current.GetSharedBytes());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _generation) _info.Text = _loc.Describe(ex);
            return;
        }
        if (generation != _generation) return;
        _info.Text = tree.LayerCount == 0 ? _loc["Viewer.Layers.None"]
            : tree.Truncated ? _loc.Format("Viewer.Layers.Truncated", LayerDocument.MaxNodes)
            : _loc.Format("Viewer.Layers.Count", tree.LayerCount);
        foreach (var node in tree.Roots) _tree.Items.Add(Build(node, view));
        UpdateButtons(view, session);
    }

    private void UpdateButtons(DocumentView view, IPdfDocumentSession session)
    {
        bool changed = view.LayerOverrides.Count > 0;
        _save.IsEnabled = changed && session.Can(DocumentPermissions.ModifyContent);
        _reset.IsEnabled = changed;
    }

    private TreeViewItem Build(LayerNode node, DocumentView view)
    {
        TreeViewItem item;
        if (node.OcgIndex < 0)
        {
            item = new TreeViewItem { Header = node.Title, IsExpanded = true };
        }
        else
        {
            bool visible = view.LayerOverrides.TryGetValue(node.OcgIndex, out var chosen) ? chosen : node.Visible;
            var check = new CheckBox { Content = node.Title, IsChecked = visible, IsEnabled = !node.Locked };
            System.Windows.Automation.AutomationProperties.SetName(check, node.Title);
            check.Click += async (_, _) => await ToggleAsync(view, node.OcgIndex, check.IsChecked == true);
            item = new TreeViewItem { Header = check, IsExpanded = true };
        }
        foreach (var child in node.Children) item.Items.Add(Build(child, view));
        return item;
    }

    private async Task ToggleAsync(DocumentView view, int index, bool visible)
    {
        var map = new Dictionary<int, bool>(view.LayerOverrides) { [index] = visible };
        await RunAsync(() => view.SetLayerOverridesAsync(map));
        if (_session is { } session) UpdateButtons(view, session);
    }

    private async Task ResetAsync()
    {
        if (View is not { } view) return;
        await RunAsync(() => view.SetLayerOverridesAsync(new Dictionary<int, bool>()));
        await RefreshAsync();
    }

    private async Task SaveAsync()
    {
        if (View is not { } view || _session is not { } session) return;
        await RunAsync(() => SaveVisibilityAsync(session, view));
        await RefreshAsync();
    }

    /// <summary>„Zapisz widoczność w dokumencie”: polecenie z Cofnij/Ponów. Potem widok nie potrzebuje już osobnego stanu.</summary>
    internal static async Task SaveVisibilityAsync(IPdfDocumentSession session, DocumentView view)
    {
        var map = new Dictionary<int, bool>(view.LayerOverrides);
        if (map.Count == 0) return;
        var command = new DelegateDocumentCommand("Viewer.Undo.LayerVisibility",
            (pdf, _, ct) => PdfSharpEdit.ModifyAsync(pdf, doc => LayerDocument.ApplyVisibility(doc, map), ct),
            DocumentChangeKind.PageContent, DocumentPermissions.ModifyContent, null);
        view.ForgetLayerOverrides();
        await session.ExecuteAsync(command);
    }

    private async Task RunAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_dialogs is { } d) d.ShowError(ex);
            else _info.Text = _loc.Describe(ex);
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Indexing;

namespace ZetPDF.Editing.Ui.Indexing;

/// <summary>Zarządzanie indeksami pełnotekstowymi: lista zindeksowanych folderów z rozmiarem i datą, Usuń wybrany, Usuń wszystkie, Aktualizuj.</summary>
internal sealed class IndexManageWindow : DialogBase
{
    /// <summary>Wiersz listy (wiązania DataGrid).</summary>
    internal sealed record Row(IndexEntry Entry, string Folder, string Documents, string Size, string Built);

    private readonly IndexController _controller;
    private readonly DataGrid _grid;
    private readonly Button _delete, _deleteAll, _update;
    private readonly TextBlock _empty;

    public IndexManageWindow(ILocalizationService loc, IndexController controller) : base(loc, "Edit.Index.Manage.Title", 680)
    {
        _controller = controller;
        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Height = 240,
            Margin = new Thickness(0, 4, 0, 8),
        };
        System.Windows.Automation.AutomationProperties.SetName(_grid, loc["Edit.Index.Manage.Title"]);
        _grid.Columns.Add(Column("Edit.Index.Manage.Folder", nameof(Row.Folder), new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Column("Edit.Index.Manage.Documents", nameof(Row.Documents), DataGridLength.Auto));
        _grid.Columns.Add(Column("Edit.Index.Manage.Size", nameof(Row.Size), DataGridLength.Auto));
        _grid.Columns.Add(Column("Edit.Index.Manage.Built", nameof(Row.Built), DataGridLength.Auto));
        _grid.SelectionChanged += (_, _) => UpdateButtons();

        _empty = Ui.Label(loc, "Edit.Index.Manage.Empty", secondary: true);
        var body = new StackPanel();
        body.Children.Add(Ui.Label(loc, "Edit.Index.Privacy", secondary: true));
        body.Children.Add(_grid);
        body.Children.Add(_empty);
        SetBody(body);

        _update = Ui.Button(loc, "Edit.Index.Manage.Update", async (_, _) => await RunAsync(() => Selected is { } e ? _controller.UpdateAsync(e) : Task.CompletedTask));
        _delete = Ui.Button(loc, "Edit.Index.Manage.Delete", async (_, _) => await DeleteSelectedAsync());
        _deleteAll = Ui.Button(loc, "Edit.Index.Manage.DeleteAll", async (_, _) => await DeleteAllAsync());
        Buttons.Children.Add(_update);
        Buttons.Children.Add(_delete);
        Buttons.Children.Add(_deleteAll);
        Buttons.Children.Add(Ui.Button(loc, "Edit.Index.Manage.Close", (_, _) => Close(), isCancel: true));
        UpdateButtons();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private IndexEntry? Selected => (_grid.SelectedItem as Row)?.Entry;

    internal IReadOnlyList<Row> Rows => _grid.ItemsSource as IReadOnlyList<Row> ?? Array.Empty<Row>();

    internal void Select(int index) => _grid.SelectedIndex = index;

    private DataGridTextColumn Column(string key, string path, DataGridLength width) =>
        new() { Header = L[key], Binding = new System.Windows.Data.Binding(path), Width = width };

    /// <summary>Wczytuje listę w tle (nagłówki plików są odszyfrowywane DPAPI).</summary>
    internal async Task RefreshAsync()
    {
        var previous = Selected?.FilePath;
        var entries = await _controller.ListEntriesAsync();
        var rows = entries.Select(ToRow).ToList();
        _grid.ItemsSource = rows;
        _grid.SelectedItem = rows.FirstOrDefault(r => r.Entry.FilePath == previous) ?? rows.FirstOrDefault();
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private Row ToRow(IndexEntry e) => new(
        e,
        e.Root is null ? L["Edit.Index.Manage.Unreadable"] : e.Recursive ? L.Format("Edit.Search.IndexRecursive", e.Root) : e.Root,
        e.Root is null ? string.Empty : e.FileCount.ToString(L.CurrentCulture),
        FormatSize(e.SizeBytes, L),
        e.BuiltUtc?.ToLocalTime().ToString("g", L.CurrentCulture) ?? string.Empty);

    internal static string FormatSize(long bytes, ILocalizationService loc) =>
        bytes < 1024 * 1024
            ? loc.Format("Edit.Index.Manage.SizeKb", Math.Max(1, (long)Math.Ceiling(bytes / 1024.0)))
            : loc.Format("Edit.Index.Manage.SizeMb", Math.Round(bytes / (1024.0 * 1024.0), 1));

    private void UpdateButtons()
    {
        _update.IsEnabled = Selected?.Root is not null;
        _delete.IsEnabled = Selected is not null;
        _deleteAll.IsEnabled = Rows.Count > 0;
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsEnabled = false;
        try
        {
            await action();
            await RefreshAsync();
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_grid.SelectedItem is not Row row) return;
        if (!_controller.ConfirmDelete(L.Format("Edit.Index.Manage.DeleteConfirm", row.Folder))) return;
        await RunAsync(() => _controller.DeleteAsync(row.Entry));
    }

    private async Task DeleteAllAsync()
    {
        if (!_controller.ConfirmDelete(L["Edit.Index.Manage.DeleteAllConfirm"])) return;
        await RunAsync(() => _controller.DeleteAsync(null));
    }
}

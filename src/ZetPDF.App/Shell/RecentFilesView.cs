using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Shell;

/// <summary>
/// Lista ostatnich plików (karta "Ostatnie" w menu Plik oraz ekran startowy bez dokumentu).
/// Klik/Enter otwiera, menu kontekstowe: Otwórz, Pokaż w folderze, Usuń z listy. Brakujący plik – pytanie o usunięcie.
/// </summary>
internal sealed class RecentFilesView : UserControl
{
    private readonly IRecentFilesService _recent;
    private readonly ILocalizationService _loc;
    private readonly Func<string, Task> _open;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly TextBlock _empty = new() { Margin = new Thickness(4, 8, 0, 0) };
    private readonly int _maxItems;

    public RecentFilesView(IRecentFilesService recent, ILocalizationService loc, Func<string, Task> open, Func<Task>? browse, bool compact, int maxItems = 0)
    {
        _recent = recent;
        _loc = loc;
        _open = open;
        _maxItems = maxItems;

        var root = new DockPanel { Margin = compact ? new Thickness(0) : new Thickness(32, 24, 32, 24), MaxWidth = compact ? 460 : double.PositiveInfinity };
        if (!compact)
        {
            var heading = new TextBlock { FontSize = 22, FontWeight = FontWeights.Light, Margin = new Thickness(0, 0, 0, 16) };
            heading.SetBinding(TextBlock.TextProperty, Loc.Bind(loc, "Shell.Backstage.Recent"));
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            if (browse is not null)
            {
                var openOther = new Button { Margin = new Thickness(0, 0, 8, 0) };
                openOther.SetBinding(ContentProperty, Loc.Bind(loc, "Shell.Recent.Browse"));
                openOther.Click += async (_, _) => await browse();
                buttons.Children.Add(openOther);
            }
            var clear = new Button();
            clear.SetBinding(ContentProperty, Loc.Bind(loc, "Shell.Recent.Clear"));
            clear.Click += (_, _) => _recent.Clear();
            buttons.Children.Add(clear);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
        }
        else
        {
            var heading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 6) };
            heading.SetBinding(TextBlock.TextProperty, Loc.Bind(loc, "Shell.Backstage.Recent"));
            heading.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
        }
        _empty.SetBinding(TextBlock.TextProperty, Loc.Bind(loc, "Shell.Recent.Empty"));
        _empty.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        DockPanel.SetDock(_empty, Dock.Top);
        root.Children.Add(_empty);
        root.Children.Add(_list);
        Content = root;

        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem) OpenSelected();
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.Key == Key.Delete && _list.SelectedItem is ListBoxItem { Tag: string p }) { _recent.Remove(p); e.Handled = true; }
        };
        _list.ContextMenu = BuildContextMenu();

        _recent.Changed += (_, _) => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        _list.Items.Clear();
        var files = _recent.Files;
        IEnumerable<string> shown = _maxItems > 0 ? files.Take(_maxItems) : files;
        foreach (var path in shown) _list.Items.Add(CreateItem(path));
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = _list.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private ListBoxItem CreateItem(string path)
    {
        var icon = new TextBlock { Text = "", FontSize = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        icon.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.Accent);
        var name = new TextBlock { Text = System.IO.Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var folder = new TextBlock { Text = System.IO.Path.GetDirectoryName(path) ?? string.Empty, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        folder.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new StackPanel { Children = { name, folder }, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = path, ToolTip = path, Padding = new Thickness(8, 6, 8, 6) };
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: string path })
        {
            var p = path;
            Dispatcher.BeginInvoke(async () => await _open(p));
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string key, Action<string> action)
        {
            var mi = new MenuItem();
            mi.SetBinding(HeaderedItemsControl.HeaderProperty, Loc.Bind(_loc, key));
            mi.Click += (_, _) =>
            {
                if (_list.SelectedItem is ListBoxItem { Tag: string p }) action(p);
            };
            return mi;
        }
        menu.Items.Add(Item("Cmd.File.Open", p => Dispatcher.BeginInvoke(async () => await _open(p))));
        menu.Items.Add(Item("Shell.Recent.ShowInFolder", ShowInFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Shell.Recent.Remove", p => _recent.Remove(p)));
        return menu;
    }

    internal static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (System.IO.Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
        }
        catch (Exception)
        {
            // Eksplorator niedostępny – ignorujemy.
        }
    }
}

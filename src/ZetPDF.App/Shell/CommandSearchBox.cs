using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Shell;

/// <summary>
/// Pole szybkiego wyszukiwania poleceń (Alt+Q) na pasku wstążki. Filtruje polecenia po nazwie w bieżącym
/// języku (<see cref="CommandSearch"/>); Enter/klik wykonuje, Esc zamyka. Polecenia niedostępne są wyszarzone.
/// </summary>
internal sealed class CommandSearchBox : UserControl
{
    private readonly ICommandRegistry _registry;
    private readonly ILocalizationService _loc;
    private readonly IShellService _shell;
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly TextBlock _empty;

    public CommandSearchBox(ICommandRegistry registry, ILocalizationService loc, IShellService shell)
    {
        _registry = registry;
        _loc = loc;
        _shell = shell;
        Width = 250;
        Margin = new Thickness(4, 1, 8, 1);
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = false;

        var glyph = new TextBlock { Text = "", FontSize = 12, Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _box = new TextBox { MinHeight = 24, Padding = new Thickness(24, 1, 4, 1), FontSize = 12 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_box, "Shell.CommandSearch.Box");
        _box.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, Loc.Bind(loc, "Cmd.Shell.CommandSearch"));
        _placeholder = new TextBlock { Margin = new Thickness(30, 0, 0, 0), FontSize = 12, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _placeholder.SetBinding(TextBlock.TextProperty, Loc.Bind(loc, "Shell.Search.Placeholder"));
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);

        _list = new ListBox { BorderThickness = new Thickness(0), MaxHeight = 380, MinWidth = 320, Padding = new Thickness(2) };
        _list.SetResourceReference(Control.BackgroundProperty, ThemeKeys.SurfaceRaised);
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem { IsEnabled: true, Tag: string id }) Execute(id);
        };
        _empty = new TextBlock { Margin = new Thickness(10, 8, 10, 8), Visibility = Visibility.Collapsed };
        _empty.SetBinding(TextBlock.TextProperty, Loc.Bind(loc, "Shell.Search.NoResults"));
        _empty.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 2, 8, 8),
            Child = new StackPanel { Children = { _empty, _list } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.25 },
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceRaised);
        popupBorder.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.BorderStrong);
        _popup = new Popup
        {
            PlacementTarget = _box,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = popupBorder,
        };

        var grid = new Grid();
        grid.Children.Add(_box);
        grid.Children.Add(glyph);
        grid.Children.Add(_placeholder);
        grid.Children.Add(_popup);
        Content = grid;

        _box.TextChanged += (_, _) => UpdateResults();
        _box.GotKeyboardFocus += (_, _) => UpdatePlaceholder();
        _box.LostKeyboardFocus += (_, _) => UpdatePlaceholder();
        _box.PreviewKeyDown += OnBoxKeyDown;
        _loc.CultureChanged += (_, _) => UpdateResults();
        UpdatePlaceholder();
    }

    /// <summary>Alt+Q – fokus w polu wyszukiwania.</summary>
    public void Activate()
    {
        _box.Focus();
        Keyboard.Focus(_box);
        _box.SelectAll();
    }

    private void UpdatePlaceholder() =>
        _placeholder.Visibility = string.IsNullOrEmpty(_box.Text) && !_box.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateResults()
    {
        UpdatePlaceholder();
        _list.Items.Clear();
        var text = _box.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _popup.IsOpen = false;
            return;
        }
        var entries = _registry.Descriptors.Select(d => new CommandSearch.Entry(d.Id, _loc[d.TextKey], _loc.HasKey(d.TooltipKey) ? _loc[d.TooltipKey] : null));
        foreach (var m in CommandSearch.Find(text, entries))
        {
            var d = _registry.GetDescriptor(m.Entry.CommandId);
            if (d is null) continue;
            _list.Items.Add(CreateItem(d, m.Entry.Label));
        }
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = _list.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _list.SelectedIndex = _list.Items.Cast<ListBoxItem>().ToList().FindIndex(i => i.IsEnabled);
        _popup.IsOpen = true;
    }

    private ListBoxItem CreateItem(CommandDescriptor d, string label)
    {
        var icon = new TextBlock { Text = d.IconGlyph, FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        var keys = new TextBlock { Text = ShortcutMap.Describe(d, includeSingleKey: false), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        keys.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(keys, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(keys);
        row.Children.Add(name);
        var enabled = _registry.GetHandler(d.Id)?.CanExecute(null) ?? false;
        var item = new ListBoxItem { Content = row, Tag = d.Id, IsEnabled = enabled };
        if (!enabled)
            item.ToolTip = _loc[_registry.GetHandler(d.Id) is null ? "Shell.Tip.NotImplemented" : "Shell.Tip.Unavailable"];
        return item;
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_list.SelectedItem is ListBoxItem { IsEnabled: true, Tag: string id }) Execute(id);
                else if (_list.Items.Count > 0) _shell.SetStatus(_loc["Shell.Tip.Unavailable"]);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (!_popup.IsOpen || _list.Items.Count == 0) return;
        var items = _list.Items.Cast<ListBoxItem>().ToList();
        var i = _list.SelectedIndex;
        for (int step = 0; step < items.Count; step++)
        {
            i = (i + delta + items.Count) % items.Count;
            if (items[i].IsEnabled)
            {
                _list.SelectedIndex = i;
                _list.ScrollIntoView(items[i]);
                return;
            }
        }
    }

    private void Close()
    {
        _popup.IsOpen = false;
        _box.Clear();
        Keyboard.ClearFocus();
        Window.GetWindow(this)?.Focus();
    }

    private void Execute(string id)
    {
        Close();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_registry.TryExecute(id)) _shell.SetStatus(_loc["Shell.Tip.Unavailable"]);
        });
    }
}

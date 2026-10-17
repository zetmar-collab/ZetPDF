using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>Kolejność tabulacji pól strony: lista z przeciąganiem i przyciskami w górę/w dół, sortowanie wg wierszy/kolumn.</summary>
internal sealed class TabOrderDialog : Window
{
    private readonly ObservableCollection<string> _items;
    private readonly ListBox _list;
    private readonly IReadOnlyList<FormFieldInfo> _fields;
    private Point _dragStart;

    public TabOrderDialog(ILocalizationService loc, int pageIndex, IReadOnlyList<FormFieldInfo> fieldsOnPage)
    {
        _fields = fieldsOnPage;
        Ui.SetupDialog(this, loc, "Annot.TabOrder.Title");
        _items = new ObservableCollection<string>(fieldsOnPage.OrderBy(f => f.TabOrder).Select(f => f.FullName));
        var root = new DockPanel { Margin = new Thickness(16), Width = 380, Height = 420 };
        var info = new TextBlock { Text = loc.Format("Annot.TabOrder.Info", pageIndex + 1), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(info, Dock.Top);
        root.Children.Add(info);
        var buttons = Ui.ButtonRow(
            Ui.Button(loc, "Annot.Dialog.Ok", (_, _) => DialogResult = true, isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        side.Children.Add(Ui.Button(loc, "Annot.TabOrder.Up", (_, _) => Move(-1)));
        side.Children.Add(Ui.Button(loc, "Annot.TabOrder.Down", (_, _) => Move(+1)));
        side.Children.Add(Ui.Button(loc, "Annot.TabOrder.ByRows", (_, _) => Sort(rows: true)));
        side.Children.Add(Ui.Button(loc, "Annot.TabOrder.ByColumns", (_, _) => Sort(rows: false)));
        foreach (Button b in side.Children) b.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        _list = new ListBox { ItemsSource = _items, AllowDrop = true };
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Annot.TabOrder.Title"]);
        _list.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(_list);
        _list.PreviewMouseMove += OnDragMove;
        _list.Drop += OnDrop;
        root.Children.Add(_list);
        Content = root;
    }

    public IReadOnlyList<string> Order => _items.ToList();

    private void Move(int delta)
    {
        int i = _list.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        _list.SelectedIndex = j;
    }

    private void Sort(bool rows)
    {
        var sorted = _fields.OrderBy(f => rows ? -Math.Round(f.Rect.Top / 4) : Math.Round(f.Rect.Left / 4)).ThenBy(f => rows ? f.Rect.Left : -f.Rect.Top).Select(f => f.FullName).ToList();
        _items.Clear();
        foreach (var s in sorted) _items.Add(s);
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _list.SelectedItem is not string item) return;
        var d = e.GetPosition(_list) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(_list, item, DragDropEffects.Move);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is not string item) return;
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as string;
        int from = _items.IndexOf(item);
        int to = target is null ? _items.Count - 1 : _items.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        _items.Move(from, to);
        _list.SelectedIndex = to;
    }
}

using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.Dialogs;

/// <summary>Wybór drugiego dokumentu (spośród otwartych kart) i układu widoku równoległego.</summary>
internal sealed class ParallelViewDialog : Window
{
    private readonly ListBox _list;
    private readonly RadioButton _horizontal;

    public ParallelViewDialog(ILocalizationService loc, IReadOnlyList<IPdfDocumentSession> candidates)
    {
        this.Loc(TitleProperty, loc, "Viewer.Parallel.Title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);

        _list = new ListBox { Height = 160, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var s in candidates) _list.Items.Add(new ListBoxItem { Tag = s, Content = s.DisplayName, ToolTip = s.FilePath ?? s.DisplayName });
        _list.SelectedIndex = 0;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is not null) DialogResult = true; };
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Viewer.Parallel.Choose"]);

        _horizontal = new RadioButton { IsChecked = true, GroupName = "layout", Margin = new Thickness(0, 2, 16, 2) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Parallel.Horizontal");
        var vertical = new RadioButton { GroupName = "layout", Margin = new Thickness(0, 2, 0, 2) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Parallel.Vertical");
        var layout = new StackPanel { Orientation = Orientation.Horizontal };
        layout.Children.Add(_horizontal);
        layout.Children.Add(vertical);

        var ok = new Button { MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Dialog.Ok");
        var cancel = new Button { MinWidth = 80, IsCancel = true, Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Dialog.Cancel");
        ok.Click += (_, _) => { if (_list.SelectedItem is not null) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Label(loc, "Viewer.Parallel.Choose", 0));
        root.Children.Add(_list);
        root.Children.Add(Ui.Label(loc, "Viewer.Parallel.Layout", 0));
        root.Children.Add(layout);
        root.Children.Add(buttons);
        Content = root;
    }

    public IPdfDocumentSession? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as IPdfDocumentSession;
    public Orientation Orientation => _horizontal.IsChecked == true ? Orientation.Horizontal : Orientation.Vertical;
}

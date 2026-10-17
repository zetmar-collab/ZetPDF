using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Security.UI;

/// <summary>Pomocniki interfejsu budowanego w kodzie: teksty z lokalizacji (na żywo), kolory tylko z motywu.</summary>
internal static class Ui
{
    public static T Loc<T>(this T element, DependencyProperty property, ILocalizationService loc, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(element, property, new Binding($"[{key}]") { Source = loc, Mode = BindingMode.OneWay });
        return element;
    }

    public static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static T Tip<T>(this T element, ILocalizationService loc, string key) where T : FrameworkElement => element.Loc(FrameworkElement.ToolTipProperty, loc, key);

    public static TextBlock Text(ILocalizationService loc, string key, bool secondary = false, bool wrap = false)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? ThemeKeys.TextSecondary : ThemeKeys.TextPrimary);
        return t.Loc(TextBlock.TextProperty, loc, key);
    }

    public static TextBlock Plain(string text = "", bool secondary = false, bool wrap = true)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? ThemeKeys.TextSecondary : ThemeKeys.TextPrimary);
        return t;
    }

    public static Button Button(ILocalizationService loc, string key, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { MinWidth = 84, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        b.Loc(ContentControl.ContentProperty, loc, key);
        b.Click += click;
        return b;
    }

    public static CheckBox Check(ILocalizationService loc, string key, bool isChecked = false)
    {
        var c = new CheckBox { IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center };
        c.Loc(ContentControl.ContentProperty, loc, key);
        return c;
    }

    public static GroupBox Group(ILocalizationService loc, string key, UIElement content)
    {
        var g = new GroupBox { Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(8), Content = content };
        g.Loc(HeaderedContentControl.HeaderProperty, loc, key);
        return g;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }
}

/// <summary>Bazowe okno dialogowe modułu: motyw, przyciski OK/Anuluj, pole błędu.</summary>
internal class SecurityDialog : Window
{
    protected readonly ILocalizationService Loc;
    private readonly TextBlock _error;
    protected readonly DockPanel Root;
    protected readonly StackPanel ButtonsPanel;

    protected SecurityDialog(ILocalizationService loc, string titleKey, double width = 460)
    {
        Loc = loc;
        this.Loc(TitleProperty, loc, titleKey);
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontUi);
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _error.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusError);
        ButtonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(ButtonsPanel, Dock.Bottom);
        DockPanel.SetDock(_error, Dock.Bottom);
        Root.Children.Add(ButtonsPanel);
        Root.Children.Add(_error);
        Content = Root;
    }

    protected void SetBody(UIElement body) => Root.Children.Add(body);

    protected void AddOkCancel(string okKey = "Core.Button.Ok")
    {
        ButtonsPanel.Children.Add(Ui.Button(Loc, okKey, (_, _) => { if (Validate()) DialogResult = true; }, isDefault: true));
        ButtonsPanel.Children.Add(Ui.Button(Loc, "Core.Button.Cancel", (_, _) => DialogResult = false, isCancel: true));
    }

    protected void AddClose() => ButtonsPanel.Children.Add(Ui.Button(Loc, "Core.Button.Close", (_, _) => Close(), isDefault: true, isCancel: true));

    /// <summary>Zwraca true, gdy dane poprawne; inaczej ustaw <see cref="ShowError"/>.</summary>
    protected virtual bool Validate() => true;

    protected void ShowError(string? text)
    {
        _error.Text = text ?? string.Empty;
        _error.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected static Grid Form(params (UIElement Label, UIElement Control)[] rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (rows[i].Label is FrameworkElement l) { l.Margin = new Thickness(0, 3, 10, 3); Grid.SetRow(l, i); g.Children.Add(l); }
            if (rows[i].Control is FrameworkElement c) { c.Margin = new Thickness(0, 3, 0, 3); Grid.SetRow(c, i); Grid.SetColumn(c, 1); g.Children.Add(c); }
        }
        return g;
    }
}

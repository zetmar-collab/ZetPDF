using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Editing.Ui;

/// <summary>Pomocniki budowania interfejsu w kodzie: teksty z lokalizacji (na żywo), kolory wyłącznie z motywu.</summary>
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

    public static TextBlock Label(ILocalizationService loc, string key, bool secondary = false)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? ThemeKeys.TextSecondary : ThemeKeys.TextPrimary);
        return t.Loc(TextBlock.TextProperty, loc, key);
    }

    public static TextBlock Text(string text, bool secondary = false)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? ThemeKeys.TextSecondary : ThemeKeys.TextPrimary);
        return t;
    }

    public static Button Button(ILocalizationService loc, string key, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { MinWidth = 88, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        b.Loc(ContentControl.ContentProperty, loc, key);
        System.Windows.Automation.AutomationProperties.SetName(b, loc[key]);
        b.Click += click;
        return b;
    }

    public static CheckBox Check(ILocalizationService loc, string key, bool value)
    {
        var c = new CheckBox { IsChecked = value, Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        c.Loc(ContentControl.ContentProperty, loc, key);
        return c;
    }

    public static RadioButton Radio(ILocalizationService loc, string key, string group, bool value)
    {
        var r = new RadioButton { IsChecked = value, GroupName = group, Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };
        r.Loc(ContentControl.ContentProperty, loc, key);
        return r;
    }

    public static TextBox Box(string text = "", double width = double.NaN, bool multiline = false)
    {
        var t = new TextBox { Text = text, Width = width, Margin = new Thickness(0, 3, 0, 3), VerticalContentAlignment = VerticalAlignment.Center };
        if (multiline)
        {
            t.AcceptsReturn = true;
            t.TextWrapping = TextWrapping.Wrap;
            t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            t.MinHeight = 60;
            t.VerticalContentAlignment = VerticalAlignment.Top;
        }
        return t;
    }

    /// <summary>ComboBox z pozycjami (wartość, klucz tekstu).</summary>
    public static ComboBox Combo<T>(ILocalizationService loc, IEnumerable<(T Value, string Key)> items, T selected)
    {
        var c = new ComboBox { Margin = new Thickness(0, 3, 0, 3), MinWidth = 140 };
        foreach (var (value, key) in items)
        {
            var item = new ComboBoxItem { Tag = value };
            item.Loc(ContentControl.ContentProperty, loc, key);
            c.Items.Add(item);
            if (Equals(value, selected)) c.SelectedItem = item;
        }
        if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    public static T Selected<T>(ComboBox c) => c.SelectedItem is ComboBoxItem { Tag: T v } ? v : default!;

    /// <summary>Siatka formularza: etykieta | kontrolka.</summary>
    public static Grid Form(params (UIElement? Label, UIElement Control)[] rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (label, control) = rows[i];
            if (label is not null)
            {
                Grid.SetRow(label, i);
                g.Children.Add(label);
            }
            Grid.SetRow(control, i);
            Grid.SetColumn(control, label is null ? 0 : 1);
            if (label is null) Grid.SetColumnSpan(control, 2);
            g.Children.Add(control);
        }
        return g;
    }

    public static Brush? ThemeBrush(string key) => Application.Current?.TryFindResource(key) as Brush;

    public static Color ToColor(PdfColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public static PdfColor ToPdf(Color c) => new(255, c.R, c.G, c.B);
}

/// <summary>Okno dialogowe modułu: tytuł z zasobów, treść, przyciski OK/Anuluj; kolory z motywu.</summary>
internal class DialogBase : Window
{
    private readonly DockPanel _root;
    protected readonly ILocalizationService L;
    protected readonly StackPanel Buttons;
    protected readonly TextBlock Error;

    protected DialogBase(ILocalizationService loc, string titleKey, double width = 440)
    {
        L = loc;
        this.Loc(TitleProperty, loc, titleKey);
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        _root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        Buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        Error.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusError);
        DockPanel.SetDock(Buttons, Dock.Bottom);
        DockPanel.SetDock(Error, Dock.Bottom);
        _root.Children.Add(Buttons);
        _root.Children.Add(Error);
        Content = _root;
    }

    protected void SetBody(UIElement body) => _root.Children.Add(body);

    protected void AddOkCancel(string okKey = "Edit.Dialog.Ok")
    {
        Buttons.Children.Add(Ui.Button(L, okKey, (_, _) => { if (Validate()) DialogResult = true; }, isDefault: true));
        Buttons.Children.Add(Ui.Button(L, "Edit.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true));
    }

    protected void ShowError(string? text)
    {
        Error.Text = text ?? string.Empty;
        Error.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Walidacja przed zamknięciem z OK (komunikat przez ShowError).</summary>
    protected virtual bool Validate() => true;
}

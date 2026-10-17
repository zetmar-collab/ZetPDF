using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI;

/// <summary>Pomocniki interfejsu budowanego w kodzie: teksty z lokalizacji (na żywo), kolory wyłącznie z motywu.</summary>
internal static class Ui
{
    public static T Loc<T>(this T element, DependencyProperty property, ILocalizationService loc, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(element, property, new Binding($"[{key}]") { Source = loc, Mode = BindingMode.OneWay });
        return element;
    }

    public static T Tip<T>(this T element, ILocalizationService loc, string key) where T : FrameworkElement
    {
        element.Loc(FrameworkElement.ToolTipProperty, loc, key);
        System.Windows.Automation.AutomationProperties.SetName(element, loc[key]);
        return element;
    }

    public static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static TextBlock Label(ILocalizationService loc, string key, Thickness? margin = null)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
        return t.Loc(TextBlock.TextProperty, loc, key);
    }

    public static Button Button(ILocalizationService loc, string key, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { MinWidth = 84, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        b.Loc(ContentControl.ContentProperty, loc, key);
        b.Click += click;
        return b;
    }

    public static Button IconButton(string glyph, ILocalizationService loc, string tipKey, RoutedEventHandler click)
    {
        var b = new Button { Content = glyph, MinWidth = 28, Height = 26, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(1, 0, 1, 0) };
        b.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontIcons);
        b.Tip(loc, tipKey);
        b.Click += click;
        return b;
    }

    /// <summary>Okno dialogowe modułu w stylu powłoki (tło/tekst z motywu).</summary>
    public static void SetupDialog(Window w, ILocalizationService loc, string titleKey)
    {
        w.Loc(Window.TitleProperty, loc, titleKey);
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.ShowInTaskbar = false;
        w.ResizeMode = ResizeMode.NoResize;
        w.SizeToContent = SizeToContent.WidthAndHeight;
        w.SetResourceReference(Control.BackgroundProperty, ThemeKeys.SurfaceRaised);
        w.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextPrimary);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, w)) w.Owner = owner;
    }

    public static StackPanel ButtonRow(params UIElement[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static Brush? Resource(string key) => Application.Current?.TryFindResource(key) as Brush;

    /// <summary>Pędzel koloru treści PDF (wybór użytkownika – dozwolony wyjątek od zakazu kolorów).</summary>
    public static SolidColorBrush ContentBrush(PdfColor c, double opacity = 1)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(c.A * opacity, 0, 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public static bool? Show(IDialogService? dialogs, Window w) => dialogs is not null ? dialogs.ShowDialog(w) : w.ShowDialog();
}

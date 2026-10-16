using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer;

/// <summary>Pomocniki budowania interfejsu w kodzie: teksty przez wiązanie do lokalizacji (na żywo), kolory z motywu.</summary>
internal static class Ui
{
    /// <summary>Wiąże właściwość z tekstem zlokalizowanym (odświeża się po zmianie języka).</summary>
    public static T Loc<T>(this T element, DependencyProperty property, ILocalizationService loc, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(element, property, new Binding($"[{key}]") { Source = loc, Mode = BindingMode.OneWay });
        return element;
    }

    public static T Tip<T>(this T element, ILocalizationService loc, string key) where T : FrameworkElement
        => element.Loc(FrameworkElement.ToolTipProperty, loc, key);

    /// <summary>Przycisk z ikoną z czcionki ikon motywu (Segoe Fluent Icons / MDL2).</summary>
    public static Button IconButton(string glyph, ILocalizationService loc, string tipKey, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = glyph,
            MinWidth = 28,
            Height = 26,
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontIcons);
        b.Tip(loc, tipKey);
        System.Windows.Automation.AutomationProperties.SetName(b, loc[tipKey]);
        b.Click += click;
        return b;
    }

    public static ToggleButton IconToggle(string glyph, ILocalizationService loc, string tipKey)
    {
        var b = new ToggleButton
        {
            Content = glyph,
            MinWidth = 28,
            Height = 26,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontIcons);
        b.Tip(loc, tipKey);
        return b;
    }

    public static TextBlock Label(ILocalizationService loc, string key, double margin = 4)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(margin, 0, margin, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
        return t.Loc(TextBlock.TextProperty, loc, key);
    }

    public static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    /// <summary>Bitmapa BGRA (premultiplied) z renderera → zamrożony BitmapSource (bezpieczny między wątkami).</summary>
    public static BitmapSource ToBitmapSource(RenderedBitmap bmp, double dpi = 96)
    {
        var src = BitmapSource.Create(bmp.Width, bmp.Height, dpi, dpi, PixelFormats.Pbgra32, null, bmp.Bgra, bmp.Stride);
        src.Freeze();
        return src;
    }

    /// <summary>Czy fokus klawiatury jest w polu tekstowym (wtedy nie przechwytujemy liter/spacji).</summary>
    public static bool IsTextInputFocused(IInputElement? focused) =>
        focused is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };
}

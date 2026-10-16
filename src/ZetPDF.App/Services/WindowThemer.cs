using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ZetPDF.App.Services;

/// <summary>
/// Stosuje motyw do WSZYSTKICH okien aplikacji, także okien modułów (klasy pochodne od Window, które nie
/// dostają stylu niejawnego "Window"): przy Loaded przypina styl z kluczem typeof(Window) (DynamicResource),
/// ikonę aplikacji i ciemny/jasny pasek tytułu (DWM, Windows 10 20H1+ / 11).
/// </summary>
public static class WindowThemer
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19;
    private static bool _registered;
    private static bool _dark;
    private static BitmapFrame? _icon;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Ikona aplikacji (Assets/ZetPDF.ico) lub null, gdy nie da się jej wczytać.</summary>
    public static BitmapFrame? AppIcon
    {
        get
        {
            if (_icon is not null) return _icon;
            try
            {
                _icon = BitmapFrame.Create(new Uri("pack://application:,,,/ZetPDF;component/Assets/ZetPDF.ico"));
            }
            catch (Exception)
            {
                _icon = null;
            }
            return _icon;
        }
    }

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
    }

    /// <summary>Przygotowuje okno przed pokazaniem (styl, ikona); pasek tytułu po utworzeniu uchwytu.</summary>
    public static void Prepare(Window window)
    {
        if (window.Style is null && window.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        if (window.Icon is null && AppIcon is { } icon) window.Icon = icon;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyTitleBar(window, _dark);
        else window.SourceInitialized += (_, _) => ApplyTitleBar(window, _dark);
    }

    public static void ApplyToAllWindows(bool dark)
    {
        _dark = dark;
        if (Application.Current is null) return;
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w, dark);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w) return;
        if (w is not Fluent.RibbonWindow && w.Style is null && w.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
            w.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        if (w.Icon is null && AppIcon is { } icon) w.Icon = icon;
        ApplyTitleBar(w, _dark);
    }

    private static void ApplyTitleBar(Window w, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref value, sizeof(int));
        }
        catch (Exception)
        {
            // Starsze systemy bez DWM dark mode – pasek tytułu pozostaje systemowy.
        }
    }
}

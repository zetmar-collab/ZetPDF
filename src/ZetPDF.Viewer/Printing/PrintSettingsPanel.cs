using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.Printing;

/// <summary>Wspólne ustawienia druku: drukarka, kopie, skalowanie, strony na arkuszu, broszura, opcje. Używają okno druku i druk wsadowy.</summary>
internal sealed class PrintSettingsPanel : StackPanel
{
    private readonly ILocalizationService _loc;
    private readonly ComboBox _printer = new() { MinWidth = 260 };
    private readonly TextBox _copies = new() { Width = 50, Text = "1" };
    private readonly CheckBox _collate = new() { IsChecked = true };
    private readonly RadioButton _fit = new() { IsChecked = true };
    private readonly RadioButton _actual = new();
    private readonly RadioButton _percent = new();
    private readonly TextBox _percentText = new() { Width = 50, Text = "100" };
    private readonly ComboBox _perSheet = new() { Width = 70 };
    private readonly CheckBox _booklet = new();
    private readonly TextBlock _bookletInfo = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 2) };
    private readonly CheckBox _autoOrient = new() { IsChecked = true };
    private readonly CheckBox _annotations = new() { IsChecked = true };
    private readonly CheckBox _gray = new() { IsChecked = false };

    public PrintSettingsPanel(ILocalizationService loc, IReadOnlyList<string> printers, string? defaultPrinter, string groupPrefix)
    {
        _loc = loc;
        _fit.GroupName = _actual.GroupName = _percent.GroupName = groupPrefix + "scale";
        foreach (var p in printers) _printer.Items.Add(p);
        _printer.SelectedItem = defaultPrinter ?? printers.FirstOrDefault();
        foreach (int n in PrintLayout.SupportedPagesPerSheet) _perSheet.Items.Add(n);
        _perSheet.SelectedItem = 1;

        _collate.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.Collate");
        _fit.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.ScaleFit");
        _actual.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.ScaleActual");
        _percent.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.ScaleCustom");
        _autoOrient.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.AutoOrientation");
        _annotations.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.Annotations");
        _gray.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.Grayscale");
        _booklet.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.Booklet");
        _bookletInfo.Loc(TextBlock.TextProperty, loc, "Viewer.Print.Booklet.Duplex");
        _bookletInfo.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        _percentText.GotKeyboardFocus += (_, _) => _percent.IsChecked = true;

        Children.Add(Section("Viewer.Print.Printer", _printer));
        Children.Add(Section("Viewer.Print.Copies", Row(_copies, _collate)));
        Children.Add(Section("Viewer.Print.Scaling", Stack(_fit, _actual, Row(_percent, _percentText, new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center }))));
        Children.Add(Section("Viewer.Print.PagesPerSheet", Stack(_perSheet, _booklet, _bookletInfo)));
        Children.Add(Section("Viewer.Print.Options", Stack(_autoOrient, _annotations, _gray)));

        foreach (var t in new ToggleButton[] { _fit, _actual, _percent, _autoOrient, _annotations, _gray, _booklet })
        {
            t.Checked += (_, _) => OnChanged();
            t.Unchecked += (_, _) => OnChanged();
        }
        _percentText.TextChanged += (_, _) => OnChanged();
        _perSheet.SelectionChanged += (_, _) => OnChanged();
    }

    /// <summary>Zmiana ustawień wpływających na układ arkusza.</summary>
    public event EventHandler? Changed;

    public bool HasPrinters => _printer.Items.Count > 0;

    public bool IsBooklet
    {
        get => _booklet.IsChecked == true;
        set => _booklet.IsChecked = value;
    }

    private void OnChanged()
    {
        _perSheet.IsEnabled = !IsBooklet;
        _autoOrient.IsEnabled = !IsBooklet;
        _bookletInfo.Visibility = IsBooklet ? Visibility.Visible : Visibility.Collapsed;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public FrameworkElement Section(string key, UIElement content)
    {
        var header = Ui.Label(_loc, key, 0);
        header.FontWeight = FontWeights.SemiBold;
        header.Margin = new Thickness(0, 8, 0, 3);
        var p = new StackPanel();
        p.Children.Add(header);
        p.Children.Add(content);
        return p;
    }

    public static StackPanel Stack(params UIElement[] items)
    {
        var p = new StackPanel();
        foreach (var i in items)
        {
            if (i is FrameworkElement f) f.Margin = new Thickness(0, 2, 0, 2);
            p.Children.Add(i);
        }
        return p;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
        {
            if (i is FrameworkElement f)
            {
                f.Margin = new Thickness(0, 0, 8, 0);
                f.VerticalAlignment = VerticalAlignment.Center;
            }
            p.Children.Add(i);
        }
        return p;
    }

    /// <summary>Opcje dla zakresu stron; <paramref name="error"/> to przetłumaczony komunikat przy błędnych wartościach.</summary>
    public bool TryBuild(string range, out PrintOptions options, out string? error)
    {
        options = null!;
        error = null;
        if (!int.TryParse(_copies.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int copies) || copies < 1 || copies > 999)
        {
            error = _loc["Viewer.Print.InvalidCopies"];
            return false;
        }
        double pct = 100;
        if (_percent.IsChecked == true && (!double.TryParse(_percentText.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out pct) || pct < 1 || pct > 1000))
        {
            error = _loc["Viewer.Print.InvalidScale"];
            return false;
        }
        options = new PrintOptions(
            _printer.SelectedItem as string,
            range,
            copies,
            _collate.IsChecked == true,
            _fit.IsChecked == true ? PrintScaling.FitToPaper : _actual.IsChecked == true ? PrintScaling.ActualSize : PrintScaling.Custom,
            pct,
            _perSheet.SelectedItem is int n ? n : 1,
            _autoOrient.IsChecked == true,
            _annotations.IsChecked == true,
            _gray.IsChecked == true,
            IsBooklet);
        return true;
    }
}

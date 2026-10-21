using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Annotations.Measure;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>
/// Skala i kalibracja: „[1] [cm] na stronie = [2] [m] w rzeczywistości”, dokładność, zakres stron, przeliczenie
/// istniejących pomiarów. Przycisk Kalibruj zamyka okno z <see cref="CalibrateRequested"/> – polecenie uruchamia wtedy
/// narzędzie zmierzenia znanego odcinka.
/// </summary>
internal sealed class ScaleDialog : Window
{
    private readonly TextBox _pageValue, _realValue;
    private readonly ComboBox _pageUnit, _realUnit, _decimals;
    private readonly RadioButton _allPages;
    private readonly CheckBox _updateExisting;
    private readonly TextBlock _error;
    private readonly ILocalizationService _loc;

    public ScaleDialog(ILocalizationService loc, MeasureScale current, int pageIndex, int pageCount)
    {
        _loc = loc;
        Ui.SetupDialog(this, loc, "Annot.Measure.Scale.Title");
        var c = loc.CurrentCulture;
        var root = new StackPanel { Margin = new Thickness(16), MinWidth = 380 };
        root.Children.Add(Ui.Label(loc, "Annot.Measure.Scale.Info"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        _pageValue = new TextBox { Width = 70, Text = current.PageValue.ToString("0.######", c) };
        _pageUnit = Units(current.PageUnit);
        _realValue = new TextBox { Width = 70, Text = current.RealValue.ToString("0.######", c) };
        _realUnit = Units(current.RealUnit);
        System.Windows.Automation.AutomationProperties.SetName(_pageValue, loc["Annot.Measure.Scale.PageValue"]);
        System.Windows.Automation.AutomationProperties.SetName(_realValue, loc["Annot.Measure.Scale.RealValue"]);
        System.Windows.Automation.AutomationProperties.SetName(_pageUnit, loc["Annot.Measure.Scale.PageUnit"]);
        System.Windows.Automation.AutomationProperties.SetName(_realUnit, loc["Annot.Measure.Scale.RealUnit"]);
        row.Children.Add(_pageValue);
        row.Children.Add(_pageUnit);
        row.Children.Add(new TextBlock { Text = "=", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_realValue);
        row.Children.Add(_realUnit);
        root.Children.Add(row);

        var dec = new StackPanel { Orientation = Orientation.Horizontal };
        dec.Children.Add(Ui.Label(loc, "Annot.Measure.Scale.Decimals"));
        _decimals = new ComboBox { Width = 60 };
        for (int i = 0; i <= 4; i++) _decimals.Items.Add(i);
        _decimals.SelectedItem = Math.Clamp(current.Decimals, 0, 4);
        System.Windows.Automation.AutomationProperties.SetName(_decimals, loc["Annot.Measure.Scale.Decimals"]);
        dec.Children.Add(_decimals);
        root.Children.Add(dec);

        var current1 = new RadioButton { IsChecked = true, Margin = new Thickness(0, 8, 0, 2) };
        current1.Content = loc.Format("Annot.Measure.Scale.CurrentPage", pageIndex + 1);
        _allPages = new RadioButton { Margin = new Thickness(0, 2, 0, 2) };
        _allPages.Content = loc.Format("Annot.Measure.Scale.AllPages", pageCount);
        root.Children.Add(current1);
        root.Children.Add(_allPages);
        _updateExisting = new CheckBox { IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        _updateExisting.Loc(ContentControl.ContentProperty, loc, "Annot.Measure.Scale.UpdateExisting");
        root.Children.Add(_updateExisting);
        _error = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _error.SetResourceReference(TextBlock.ForegroundProperty, Core.Theming.ThemeKeys.StatusError);
        root.Children.Add(_error);

        var calibrate = Ui.Button(loc, "Annot.Measure.Scale.Calibrate", (_, _) => { CalibrateRequested = true; DialogResult = false; });
        calibrate.Margin = new Thickness(0);
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        buttons.Children.Add(calibrate);
        var ok = Ui.ButtonRow(
            Ui.Button(loc, "Annot.Dialog.Ok", (_, _) => Accept(), isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true));
        ok.Margin = new Thickness(0);
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
    }

    private static ComboBox Units(string selected)
    {
        var cb = new ComboBox { Width = 64, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var u in MeasureScale.Units) cb.Items.Add(u);
        cb.SelectedItem = MeasureScale.Units.Contains(selected) ? selected : "cm";
        return cb;
    }

    public MeasureScale? Result { get; private set; }
    public bool AllPages => _allPages.IsChecked == true;
    public bool UpdateExisting => _updateExisting.IsChecked == true;
    public bool CalibrateRequested { get; private set; }

    private void Accept()
    {
        var c = _loc.CurrentCulture;
        if (TryNumber(_pageValue.Text, c, out var pv) && TryNumber(_realValue.Text, c, out var rv))
        {
            var s = new MeasureScale(pv, (string)_pageUnit.SelectedItem, rv, (string)_realUnit.SelectedItem, (int)_decimals.SelectedItem);
            if (s.IsValid) { Result = s; DialogResult = true; return; }
        }
        _error.Text = _loc["Annot.Measure.Error.Scale"];
        _error.Visibility = Visibility.Visible;
    }

    /// <summary>Liczba dodatnia w kulturze interfejsu (akceptuje też kropkę).</summary>
    public static bool TryNumber(string text, CultureInfo culture, out double value)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, culture, out value) && value > 0 && double.IsFinite(value)) return true;
        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && double.IsFinite(value);
    }
}

/// <summary>Kalibracja: zmierzony odcinek (długość na stronie) → rzeczywista długość i jednostka.</summary>
internal sealed class CalibrateDialog : Window
{
    private readonly TextBox _length;
    private readonly ComboBox _unit;
    private readonly TextBlock _error;
    private readonly ILocalizationService _loc;
    private readonly double _points;

    public CalibrateDialog(ILocalizationService loc, double measuredPoints, string defaultUnit)
    {
        _loc = loc;
        _points = measuredPoints;
        Ui.SetupDialog(this, loc, "Annot.Measure.Calibrate.Title");
        var root = new StackPanel { Margin = new Thickness(16), MinWidth = 340 };
        var cm = measuredPoints / MeasureScale.PointsPer("cm");
        root.Children.Add(new TextBlock { Text = loc.Format("Annot.Measure.Calibrate.Info", cm.ToString("0.##", loc.CurrentCulture)), TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        root.Children.Add(Ui.Label(loc, "Annot.Measure.Calibrate.RealLength", new Thickness(0, 10, 0, 4)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _length = new TextBox { Width = 90 };
        System.Windows.Automation.AutomationProperties.SetName(_length, loc["Annot.Measure.Calibrate.RealLength"]);
        _unit = new ComboBox { Width = 64, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var u in MeasureScale.Units) _unit.Items.Add(u);
        _unit.SelectedItem = MeasureScale.Units.Contains(defaultUnit) ? defaultUnit : "m";
        System.Windows.Automation.AutomationProperties.SetName(_unit, loc["Annot.Measure.Scale.RealUnit"]);
        row.Children.Add(_length);
        row.Children.Add(_unit);
        root.Children.Add(row);
        _error = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        _error.SetResourceReference(TextBlock.ForegroundProperty, Core.Theming.ThemeKeys.StatusError);
        root.Children.Add(_error);
        root.Children.Add(Ui.ButtonRow(
            Ui.Button(loc, "Annot.Dialog.Ok", (_, _) => Accept(), isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true)));
        Content = root;
        Loaded += (_, _) => _length.Focus();
    }

    public MeasureScale? Result { get; private set; }

    private void Accept()
    {
        if (ScaleDialog.TryNumber(_length.Text, _loc.CurrentCulture, out var len) && _points > 0)
        {
            Result = MeasureScale.Calibrate(_points, len, (string)_unit.SelectedItem);
            DialogResult = true;
            return;
        }
        _error.Text = _loc["Annot.Measure.Error.Scale"];
        _error.Visibility = Visibility.Visible;
    }
}

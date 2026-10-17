using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>Które sekcje pokazać w oknie stylu/właściwości.</summary>
[Flags]
internal enum StyleParts { Color = 1, Fill = 2, Opacity = 4, LineWidth = 8, Font = 16, Contents = 32 }

/// <summary>
/// Właściwości komentarza (treść, kolor, krycie, grubość) albo domyślny styl narzędzia (także wypełnienie i czcionka).
/// </summary>
internal sealed class StyleDialog : Window
{
    private readonly ColorPicker? _color;
    private readonly CheckBox? _hasFill;
    private readonly ColorPicker? _fill;
    private readonly Slider? _opacity;
    private readonly TextBox? _width;
    private readonly ComboBox? _font;
    private readonly TextBox? _fontSize;
    private readonly TextBox? _contents;

    public StyleDialog(ILocalizationService loc, string titleKey, StyleParts parts, ToolStyle style, string? contents = null, string? info = null)
    {
        Ui.SetupDialog(this, loc, titleKey);
        var root = new StackPanel { Margin = new Thickness(16), MinWidth = 300 };
        if (info is not null) root.Children.Add(new TextBlock { Text = info, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        if (parts.HasFlag(StyleParts.Contents))
        {
            root.Children.Add(Ui.Label(loc, "Annot.Style.Contents"));
            _contents = new TextBox { Text = contents ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, Width = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(_contents, loc["Annot.Style.Contents"]);
            root.Children.Add(_contents);
        }
        if (parts.HasFlag(StyleParts.Color))
        {
            root.Children.Add(Ui.Label(loc, "Annot.Style.Color"));
            root.Children.Add(_color = new ColorPicker(loc, style.Color));
        }
        if (parts.HasFlag(StyleParts.Fill))
        {
            _hasFill = new CheckBox { IsChecked = style.HasFill, Margin = new Thickness(0, 8, 0, 4) };
            _hasFill.Loc(ContentControl.ContentProperty, loc, "Annot.Style.Fill");
            root.Children.Add(_hasFill);
            root.Children.Add(_fill = new ColorPicker(loc, style.Fill ?? PdfColor.White));
        }
        if (parts.HasFlag(StyleParts.Opacity))
        {
            root.Children.Add(Ui.Label(loc, "Annot.Style.Opacity"));
            _opacity = new Slider { Minimum = 10, Maximum = 100, Value = Math.Round(style.Opacity * 100), TickFrequency = 10, IsSnapToTickEnabled = false, Width = 240, AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft };
            System.Windows.Automation.AutomationProperties.SetName(_opacity, loc["Annot.Style.Opacity"]);
            root.Children.Add(_opacity);
        }
        if (parts.HasFlag(StyleParts.LineWidth))
        {
            root.Children.Add(Ui.Label(loc, "Annot.Style.LineWidth"));
            _width = new TextBox { Text = style.LineWidth.ToString("0.##", CultureInfo.CurrentCulture), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetName(_width, loc["Annot.Style.LineWidth"]);
            root.Children.Add(_width);
        }
        if (parts.HasFlag(StyleParts.Font))
        {
            root.Children.Add(Ui.Label(loc, "Annot.Style.Font"));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            _font = new ComboBox { Width = 180, IsEditable = true, Text = style.FontFamily };
            foreach (var f in new[] { "Arial", "Calibri", "Cambria", "Courier New", "Georgia", "Segoe UI", "Tahoma", "Times New Roman", "Verdana" }) _font.Items.Add(f);
            _fontSize = new TextBox { Width = 50, Margin = new Thickness(6, 0, 0, 0), Text = style.FontSize.ToString("0.#", CultureInfo.CurrentCulture) };
            System.Windows.Automation.AutomationProperties.SetName(_font, loc["Annot.Style.Font"]);
            System.Windows.Automation.AutomationProperties.SetName(_fontSize, loc["Annot.Style.FontSize"]);
            row.Children.Add(_font);
            row.Children.Add(_fontSize);
            root.Children.Add(row);
        }
        root.Children.Add(Ui.ButtonRow(
            Ui.Button(loc, "Annot.Dialog.Ok", (_, _) => DialogResult = true, isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true)));
        Content = root;
        Loaded += (_, _) => (_contents as UIElement ?? _opacity as UIElement)?.Focus();
        Result = style.Clone();
        Closing += (_, _) => { if (DialogResult == true) Collect(); };
    }

    public ToolStyle Result { get; }
    public string? Contents => _contents?.Text;

    private static double Parse(string? s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;

    private void Collect()
    {
        if (_color is not null) Result.Color = _color.Color;
        if (_hasFill is not null && _fill is not null) Result.Fill = _hasFill.IsChecked == true ? _fill.Color : null;
        if (_opacity is not null) Result.Opacity = Math.Clamp(_opacity.Value / 100, 0.1, 1);
        if (_width is not null) Result.LineWidth = Math.Clamp(Parse(_width.Text, Result.LineWidth), 0, 50);
        if (_font is not null && !string.IsNullOrWhiteSpace(_font.Text)) Result.FontFamily = _font.Text.Trim();
        if (_fontSize is not null) Result.FontSize = Math.Clamp(Parse(_fontSize.Text, Result.FontSize), 4, 144);
    }
}

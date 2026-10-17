using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Editing.Ui.Dialogs;

/// <summary>
/// Pole edycji tekstu wyświetlane nad stroną (w miejscu bloku): tekst, czcionka, rozmiar, kolor, pogrubienie, kursywa.
/// Kolory na liście to kolory TREŚCI PDF (wybór użytkownika) – nie kolory interfejsu.
/// </summary>
internal sealed class TextEditorPopup : Window
{
    private static readonly (string Key, PdfColor Color)[] Palette =
    {
        ("Edit.Color.Black", PdfColor.Black), ("Edit.Color.DarkGray", new PdfColor(255, 64, 64, 64)), ("Edit.Color.Red", new PdfColor(255, 200, 0, 0)),
        ("Edit.Color.Blue", new PdfColor(255, 0, 70, 180)), ("Edit.Color.Green", new PdfColor(255, 0, 128, 0)), ("Edit.Color.Orange", new PdfColor(255, 230, 120, 0)),
        ("Edit.Color.White", PdfColor.White),
    };

    private readonly TextBox _text;
    private readonly ComboBox _font;
    private readonly ComboBox _size;
    private readonly ComboBox _color;
    private readonly CheckBox _bold;
    private readonly CheckBox _italic;
    private PdfColor _custom;

    public TextEditorPopup(ILocalizationService loc, string titleKey, string text, TextStyle style, Point screenTopLeftDip, double minWidth)
    {
        this.Loc(TitleProperty, loc, titleKey);
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = Math.Max(380, minWidth + 40);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screenTopLeftDip.X;
        Top = screenTopLeftDip.Y;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        _custom = style.Color;

        _font = new ComboBox { IsEditable = true, Width = 170, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.CurrentCulture)) _font.Items.Add(f);
        _font.Text = style.FontFamily;
        System.Windows.Automation.AutomationProperties.SetName(_font, loc["Edit.Text.Font"]);
        _size = new ComboBox { IsEditable = true, Width = 64, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var s in new[] { 6, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 }) _size.Items.Add(s.ToString(CultureInfo.CurrentCulture));
        _size.Text = Math.Round(style.FontSize, 1).ToString(CultureInfo.CurrentCulture);
        System.Windows.Automation.AutomationProperties.SetName(_size, loc["Edit.Text.Size"]);
        _color = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 6, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_color, loc["Edit.Text.Color"]);
        int selected = -1;
        foreach (var (key, c) in Palette)
        {
            _color.Items.Add(ColorItem(loc[key], c));
            if (c == style.Color) selected = _color.Items.Count - 1;
        }
        if (selected < 0)
        {
            _color.Items.Add(ColorItem(loc["Edit.Color.Current"], style.Color));
            selected = _color.Items.Count - 1;
        }
        _color.SelectedIndex = selected;
        _bold = Ui.Check(loc, "Edit.Text.Bold", style.Bold);
        _bold.Margin = new Thickness(0, 0, 6, 0);
        _italic = Ui.Check(loc, "Edit.Text.Italic", style.Italic);

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_font);
        bar.Children.Add(_size);
        bar.Children.Add(_color);
        bar.Children.Add(_bold);
        bar.Children.Add(_italic);

        _text = Ui.Box(text, multiline: true);
        _text.MinHeight = 48;
        _text.FontSize = Math.Clamp(style.FontSize * 96 / 72, 11, 28);
        System.Windows.Automation.AutomationProperties.SetName(_text, loc["Edit.Text.Content"]);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Ui.Button(loc, "Edit.Dialog.Ok", (_, _) => DialogResult = true, isDefault: false));
        buttons.Children.Add(Ui.Button(loc, "Edit.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true));
        var hint = Ui.Label(loc, "Edit.Text.Hint", secondary: true);

        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(bar);
        root.Children.Add(_text);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _text.Focus(); _text.SelectAll(); EnsureOnScreen(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                DialogResult = true;
                e.Handled = true;
            }
        };
    }

    private static ComboBoxItem ColorItem(string name, PdfColor c)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Ui.ToColor(c)), // kolor treści PDF wybrany przez użytkownika
            BorderThickness = new Thickness(1),
        }.Themed(Border.BorderBrushProperty, ThemeKeys.BorderStrong));
        panel.Children.Add(new TextBlock { Text = name });
        return new ComboBoxItem { Content = panel, Tag = c };
    }

    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        if (Left + ActualWidth > wa.Right) Left = Math.Max(wa.Left, wa.Right - ActualWidth);
        if (Top + ActualHeight > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - ActualHeight);
    }

    public string ResultText => _text.Text;

    public TextStyle ResultStyle
    {
        get
        {
            var size = double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var s) && s > 0.5 && s < 500 ? s : 12;
            var color = _color.SelectedItem is ComboBoxItem { Tag: PdfColor c } ? c : _custom;
            var font = string.IsNullOrWhiteSpace(_font.Text) ? "Arial" : _font.Text.Trim();
            return new TextStyle { FontFamily = font, FontSize = size, Bold = _bold.IsChecked == true, Italic = _italic.IsChecked == true, Color = color };
        }
    }

    /// <summary>Lewy górny róg (DIP ekranu) dla punktu widoku.</summary>
    public static Point ScreenPoint(FrameworkElement element, Point viewPoint)
    {
        try
        {
            var px = element.PointToScreen(viewPoint);
            var source = PresentationSource.FromVisual(element);
            return source?.CompositionTarget?.TransformFromDevice.Transform(px) ?? px;
        }
        catch (InvalidOperationException)
        {
            return new Point(100, 100);
        }
    }
}

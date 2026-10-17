using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Ui;

/// <summary>
/// Baza okien modułu: teksty wiązane z lokalizacją (zmiana języka na żywo), style domyślne powłoki (bez własnych kolorów),
/// przycisk OK ze stylem Shell.Button.Accent, komunikat błędu stylem Shell.Text.Error. Okna pokazuje IDialogService.ShowDialog.
/// </summary>
internal abstract partial class PagesDialog : Window
{
    protected const double MmToPt = 72.0 / 25.4;

    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 520 };

    protected PagesDialog(ILocalizationService loc, IDialogService dialogs, string titleKey)
    {
        Loc = loc;
        Dialogs = dialogs;
        Bind(this, TitleProperty, titleKey);
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _error.SetResourceReference(StyleProperty, "Shell.Text.Error");

        var ok = new Button { MinWidth = 96, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
        ok.SetResourceReference(StyleProperty, "Shell.Button.Accent");
        Bind(ok, ContentControl.ContentProperty, OkKey);
        ok.Click += (_, _) =>
        {
            _error.Text = string.Empty;
            try
            {
                if (Accept()) DialogResult = true;
            }
            catch (FormatException ex) { _error.Text = ex.Message; }
            catch (ArgumentException ex) { _error.Text = ex.Message; }
        };
        var cancel = new Button { MinWidth = 96, IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        Bind(cancel, ContentControl.ContentProperty, "Pages.Dialog.Cancel");
        Buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Buttons.Children.Add(ok);
        Buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Body);
        root.Children.Add(_error);
        root.Children.Add(Buttons);
        Content = root;
    }

    protected ILocalizationService Loc { get; }
    protected IDialogService Dialogs { get; }
    protected StackPanel Body { get; } = new() { MinWidth = 400 };
    protected StackPanel Buttons { get; }
    protected virtual string OkKey => "Pages.Dialog.Ok";

    /// <summary>Walidacja i zbudowanie wyniku; false = zostań w oknie (komunikat przez <see cref="Error"/>).</summary>
    protected abstract bool Accept();

    protected bool Error(string key, params object?[] args)
    {
        _error.Text = args.Length == 0 ? Loc[key] : Loc.Format(key, args);
        return false;
    }

    protected string T(string key) => Loc[key];

    // ------------------------------------------------------------------ budowanie

    protected T Bind<T>(T element, DependencyProperty property, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(element, property, new Binding($"[{key}]") { Source = Loc, Mode = BindingMode.OneWay });
        return element;
    }

    protected TextBlock Label(string key, bool wrap = false)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxWidth = wrap ? 480 : double.PositiveInfinity };
        return Bind(t, TextBlock.TextProperty, key);
    }

    protected TextBlock Hint(string key)
    {
        var t = Label(key, wrap: true);
        t.SetResourceReference(StyleProperty, "Shell.Text.Secondary");
        t.Margin = new Thickness(0, 2, 0, 4);
        return t;
    }

    /// <summary>Wiersz: etykieta (stała szerokość) + kontrolka.</summary>
    protected FrameworkElement Row(string labelKey, UIElement control, double labelWidth = 170)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Label(labelKey);
        g.Children.Add(l);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    protected FrameworkElement Section(string headerKey, params UIElement[] children)
    {
        var p = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        var h = Label(headerKey);
        h.FontWeight = FontWeights.SemiBold;
        h.Margin = new Thickness(0, 4, 0, 4);
        p.Children.Add(h);
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    protected static StackPanel Horizontal(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
        {
            if (i is FrameworkElement f) { f.Margin = new Thickness(0, 0, 8, 0); f.VerticalAlignment = VerticalAlignment.Center; }
            p.Children.Add(i);
        }
        return p;
    }

    protected static TextBox Box(string text = "", double width = 220) => new() { Text = text, Width = width, VerticalContentAlignment = VerticalAlignment.Center };

    protected CheckBox Check(string key, bool value = false)
    {
        var c = new CheckBox { IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
        return Bind(c, ContentControl.ContentProperty, key);
    }

    protected RadioButton Radio(string key, string group, bool value = false)
    {
        var r = new RadioButton { GroupName = group + GetHashCode().ToString(CultureInfo.InvariantCulture), IsChecked = value, Margin = new Thickness(0, 3, 12, 3) };
        return Bind(r, ContentControl.ContentProperty, key);
    }

    /// <summary>Lista wyboru z pozycji (wartość, klucz tekstu).</summary>
    protected ComboBox Combo<TValue>(IEnumerable<(TValue Value, string Key)> items, TValue selected, double width = 220)
    {
        var c = new ComboBox { Width = width };
        foreach (var (v, k) in items)
        {
            var item = new ComboBoxItem { Tag = v };
            Bind(item, ContentControl.ContentProperty, k);
            c.Items.Add(item);
            if (Equals(v, selected)) c.SelectedItem = item;
        }
        if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    protected static TValue ComboValue<TValue>(ComboBox c) => c.SelectedItem is ComboBoxItem { Tag: TValue v } ? v : default!;

    /// <summary>Pole ścieżki z przyciskiem „Przeglądaj…”.</summary>
    protected FrameworkElement FilePicker(TextBox box, string titleKey, Func<IReadOnlyList<FileFilter>> filters)
    {
        var b = new Button { Padding = new Thickness(10, 2, 10, 2) };
        Bind(b, ContentControl.ContentProperty, "Pages.Dialog.Browse");
        b.Click += (_, _) =>
        {
            var files = Dialogs.OpenFiles(T(titleKey), filters());
            if (files is { Count: > 0 }) box.Text = files[0];
        };
        return Horizontal(box, b);
    }

    protected FrameworkElement FolderPicker(TextBox box, string titleKey)
    {
        var b = new Button { Padding = new Thickness(10, 2, 10, 2) };
        Bind(b, ContentControl.ContentProperty, "Pages.Dialog.Browse");
        b.Click += (_, _) =>
        {
            var f = Dialogs.SelectFolder(T(titleKey), string.IsNullOrWhiteSpace(box.Text) ? null : box.Text);
            if (f is not null) box.Text = f;
        };
        return Horizontal(box, b);
    }

    protected IReadOnlyList<FileFilter> PdfFilters() => new[] { FileFilter.Pdf(Loc), FileFilter.All(Loc) };

    protected IReadOnlyList<FileFilter> ImageFilters() => new[] { new FileFilter(T("Pages.Filter.Images"), "*.png", "*.jpg", "*.jpeg", "*.bmp"), FileFilter.All(Loc) };

    // ------------------------------------------------------------------ parsowanie

    protected static bool TryNumber(TextBox box, out double value, double min, double max)
    {
        var s = box.Text.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }

    protected static bool TryInt(TextBox box, out int value, int min, int max) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    protected static string Fmt(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.CurrentCulture);

    // ------------------------------------------------------------------ kolor i czcionka

    protected sealed class ColorField
    {
        public ColorField(PdfColor initial)
        {
            Box = new TextBox { Width = 90, Text = ToHex(initial) };
            Swatch = new Border { Width = 22, Height = 22, BorderThickness = new Thickness(1) };
            Swatch.SetResourceReference(Border.BorderBrushProperty, Core.Theming.ThemeKeys.Border);
            Box.TextChanged += (_, _) => Update();
            Update();
            Element = Horizontal(Box, Swatch);
        }

        public TextBox Box { get; }
        public Border Swatch { get; }
        public FrameworkElement Element { get; }

        public bool TryGet(out PdfColor color)
        {
            color = PdfColor.Black;
            var s = Box.Text.Trim().TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            color = new PdfColor(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        private void Update()
        {
            // kolor treści PDF wybrany przez użytkownika (wyjątek od zakazu kolorów w UI)
            Swatch.Background = TryGet(out var c) ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : null;
        }

        public static string ToHex(PdfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    protected static ComboBox FontCombo(string selected)
    {
        var c = new ComboBox { Width = 220, IsEditable = true };
        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).Where(s => !s.Contains('#')).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            c.Items.Add(f);
        c.Text = selected;
        return c;
    }
}

using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Security.UI;

/// <summary>
/// Edytor parametrów kroku budowany z właściwości rekordu opcji (bool, liczby, tekst, wyliczenia, uprawnienia,
/// kolor #RRGGBB, zagnieżdżony styl tekstu). Etykiety: Security.Param.&lt;Właściwość&gt;. Rekord zmieniany w miejscu.
/// </summary>
internal static class OptionsEditor
{
    /// <summary>Właściwości pomijane w edytorze (hasła – pytamy przy uruchomieniu; listy stron/obszary – nie dotyczą wsadu).</summary>
    private static readonly HashSet<string> Hidden = new() { "UserPassword", "OwnerPassword", "Pages", "Region", "ImagePath" };

    public static FrameworkElement Build(object options, ILocalizationService loc, Action changed)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var prop in options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.GetIndexParameters().Length > 0 || Hidden.Contains(prop.Name)) continue;
            var editor = CreateEditor(prop, options, loc, changed);
            if (editor is null) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Ui.Text(loc, "Security.Param." + prop.Name);
            label.Margin = new Thickness(0, 3, 10, 3);
            label.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(label, row);
            editor.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            System.Windows.Automation.AutomationProperties.SetName(editor, loc["Security.Param." + prop.Name]);
            grid.Children.Add(label);
            grid.Children.Add(editor);
            row++;
        }
        return grid;
    }

    private static FrameworkElement? CreateEditor(PropertyInfo prop, object target, ILocalizationService loc, Action changed)
    {
        var type = prop.PropertyType;
        var value = prop.GetValue(target);
        void Set(object? v) { prop.SetValue(target, v); changed(); }

        if (type == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)value!, VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (_, _) => Set(true);
            cb.Unchecked += (_, _) => Set(false);
            return cb;
        }
        if (type == typeof(string))
        {
            var tb = new TextBox { Text = (string?)value ?? string.Empty };
            tb.TextChanged += (_, _) => Set(tb.Text);
            return tb;
        }
        if (type == typeof(int) || type == typeof(long) || type == typeof(double))
        {
            var tb = new TextBox { Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty, MaxWidth = 120, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 80 };
            tb.TextChanged += (_, _) =>
            {
                bool ok = double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) || double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                tb.SetResourceReference(Control.BorderBrushProperty, ok ? ThemeKeys.InputBorder : ThemeKeys.StatusError);
                if (!ok) return;
                Set(type == typeof(int) ? (object)(int)Math.Round(d) : type == typeof(long) ? (long)Math.Round(d) : d);
            };
            return tb;
        }
        if (type == typeof(DocumentPermissions))
        {
            var panel = new WrapPanel();
            var current = (DocumentPermissions)value!;
            foreach (var flag in Enum.GetValues<DocumentPermissions>().Where(f => f is not DocumentPermissions.None and not DocumentPermissions.All))
            {
                var cb = Ui.Check(loc, "Security.Perm." + flag, current.HasFlag(flag));
                cb.Margin = new Thickness(0, 2, 12, 2);
                void Update()
                {
                    var p = (DocumentPermissions)prop.GetValue(target)!;
                    Set(cb.IsChecked == true ? p | flag : p & ~flag);
                }
                cb.Checked += (_, _) => Update();
                cb.Unchecked += (_, _) => Update();
                panel.Children.Add(cb);
            }
            return panel;
        }
        if (type.IsEnum)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };
            foreach (var v in Enum.GetValues(type))
            {
                if (type == typeof(PdfEncryptionMethod) && v is PdfEncryptionMethod m && m is not (PdfEncryptionMethod.Aes256 or PdfEncryptionMethod.Aes128)) continue;
                var key = type == typeof(PdfEncryptionMethod) ? "Security.Method." + v : "Security.Enum." + v;
                combo.Items.Add(new ComboBoxItem { Tag = v }.Loc(ContentControl.ContentProperty, loc, key));
            }
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem i) Set(i.Tag); };
            return combo;
        }
        if (type == typeof(PdfColor))
        {
            var c = (PdfColor)value!;
            var tb = new TextBox { Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}", MaxWidth = 120, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Left };
            tb.TextChanged += (_, _) =>
            {
                var t = tb.Text.Trim().TrimStart('#');
                bool ok = t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
                tb.SetResourceReference(Control.BorderBrushProperty, ok ? ThemeKeys.InputBorder : ThemeKeys.StatusError);
                if (!ok) return;
                int rgb = int.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Set(new PdfColor(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            };
            return tb;
        }
        if (type == typeof(PdfColor?))
        {
            return null;
        }
        if (type == typeof(TextStyle))
        {
            var style = (TextStyle)(value ?? new TextStyle());
            var inner = Build(style, loc, () => Set(style));
            var border = new Border { Child = inner, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0) };
            border.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Border);
            return border;
        }
        return null;
    }
}

using System.Windows;
using System.Windows.Controls;
using ZetPDF.Annotations.Forms;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>Ustawienia pola kodu kreskowego: symbolika, format danych, nazwy pól w pierwszym wierszu, pola źródłowe.</summary>
internal sealed class BarcodeFieldDialog : Window
{
    private readonly ComboBox _sym, _format;
    private readonly CheckBox _names;
    private readonly List<CheckBox> _fields = new();

    public BarcodeFieldDialog(ILocalizationService loc, BarcodeOptions current, IReadOnlyList<string> availableFields)
    {
        Ui.SetupDialog(this, loc, "Annot.Barcode.Title");
        var root = new StackPanel { Margin = new Thickness(16), Width = 360 };
        root.Children.Add(Ui.Label(loc, "Annot.Barcode.Symbology"));
        _sym = new ComboBox();
        foreach (var s in Enum.GetValues<BarcodeSymbology>()) _sym.Items.Add(new ComboBoxItem { Content = loc["Annot.Barcode.Sym." + s], Tag = s });
        _sym.SelectedIndex = (int)current.Symbology;
        System.Windows.Automation.AutomationProperties.SetName(_sym, loc["Annot.Barcode.Symbology"]);
        root.Children.Add(_sym);
        root.Children.Add(Ui.Label(loc, "Annot.Barcode.Format"));
        _format = new ComboBox();
        foreach (var f in Enum.GetValues<BarcodeDataFormat>()) _format.Items.Add(new ComboBoxItem { Content = loc["Annot.Barcode.Fmt." + f], Tag = f });
        _format.SelectedIndex = (int)current.Format;
        System.Windows.Automation.AutomationProperties.SetName(_format, loc["Annot.Barcode.Format"]);
        root.Children.Add(_format);
        _names = new CheckBox { IsChecked = current.IncludeNames, Margin = new Thickness(0, 6, 0, 0) };
        _names.Loc(ContentControl.ContentProperty, loc, "Annot.Barcode.IncludeNames");
        _format.SelectionChanged += (_, _) => _names.IsEnabled = _format.SelectedIndex == (int)BarcodeDataFormat.TabDelimited;
        _names.IsEnabled = current.Format == BarcodeDataFormat.TabDelimited;
        root.Children.Add(_names);
        root.Children.Add(Ui.Label(loc, "Annot.Barcode.Fields", new Thickness(0, 10, 0, 4)));
        var list = new StackPanel();
        foreach (var name in availableFields)
        {
            var cb = new CheckBox { Content = name, Tag = name, IsChecked = current.Fields.Count == 0 || current.Fields.Contains(name), Margin = new Thickness(0, 1, 0, 1) };
            _fields.Add(cb);
            list.Children.Add(cb);
        }
        if (availableFields.Count == 0) list.Children.Add(Ui.Label(loc, "Annot.Barcode.NoFields"));
        var scroll = new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        root.Children.Add(Ui.ButtonRow(
            Ui.Button(loc, "Annot.Dialog.Ok", (_, _) => DialogResult = true, isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true)));
        Content = root;
    }

    public BarcodeOptions Result
    {
        get
        {
            var chosen = _fields.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            return new BarcodeOptions
            {
                Symbology = (BarcodeSymbology)((ComboBoxItem)_sym.SelectedItem).Tag,
                Format = (BarcodeDataFormat)((ComboBoxItem)_format.SelectedItem).Tag,
                IncludeNames = _names.IsChecked == true,
                // wszystkie zaznaczone = pusta lista (nowe pola też trafią do kodu)
                Fields = chosen.Count == _fields.Count ? Array.Empty<string>() : chosen,
            };
        }
    }
}

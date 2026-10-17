using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>Właściwości pola formularza: nazwa, podpowiedź, wymagane, tylko do odczytu, wartość domyślna, opcje, czcionka, akcja przycisku, format daty.</summary>
internal sealed class FieldPropertiesDialog : Window
{
    private readonly FormFieldSpec _spec;
    private readonly TextBox _name, _tooltip, _default, _fontSize;
    private readonly ComboBox _font;
    private readonly CheckBox _required, _readOnly, _multiline;
    private readonly TextBox? _options, _export, _url, _caption, _dateFormat;
    private readonly ComboBox? _action;
    private readonly TextBlock _error;
    private readonly Func<string, bool> _nameTaken;

    public FieldPropertiesDialog(ILocalizationService loc, FormFieldSpec spec, Func<string, bool> nameTaken)
    {
        _spec = spec;
        _nameTaken = nameTaken;
        Ui.SetupDialog(this, loc, "Annot.Field.Title");
        var root = new StackPanel { Margin = new Thickness(16), Width = 380 };
        root.Children.Add(new TextBlock { Text = loc["Annot.Field.Kind." + spec.Kind], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

        TextBox Field(string key, string value, bool multi = false)
        {
            root.Children.Add(Ui.Label(loc, key));
            var tb = new TextBox { Text = value, AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, Height = multi ? 80 : double.NaN, VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
            System.Windows.Automation.AutomationProperties.SetName(tb, loc[key]);
            root.Children.Add(tb);
            return tb;
        }
        CheckBox Check(string key, bool value)
        {
            var cb = new CheckBox { IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
            cb.Loc(ContentControl.ContentProperty, loc, key);
            root.Children.Add(cb);
            return cb;
        }

        _name = Field("Annot.Field.Name", spec.Name);
        _tooltip = Field("Annot.Field.Tooltip", spec.Tooltip);
        bool hasValue = spec.Kind is FormFieldKind.Text or FormFieldKind.Date or FormFieldKind.ComboBox or FormFieldKind.ListBox or FormFieldKind.CheckBox or FormFieldKind.RadioButton;
        _default = Field("Annot.Field.Default", spec.DefaultValue);
        _default.IsEnabled = hasValue;
        if (spec.Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox)
            _options = Field("Annot.Field.Options", string.Join(Environment.NewLine, spec.Options), multi: true);
        if (spec.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton)
            _export = Field("Annot.Field.ExportValue", spec.ExportValue ?? "");
        if (spec.Kind == FormFieldKind.Date)
            _dateFormat = Field("Annot.Field.DateFormat", spec.DateFormat ?? "yyyy-MM-dd");
        if (spec.Kind == FormFieldKind.PushButton)
        {
            _caption = Field("Annot.Field.Caption", spec.ButtonCaption ?? spec.Name);
            root.Children.Add(Ui.Label(loc, "Annot.Field.Action"));
            _action = new ComboBox();
            foreach (var a in Enum.GetValues<FormButtonAction>()) _action.Items.Add(new ComboBoxItem { Content = loc["Annot.Field.Action." + a], Tag = a });
            _action.SelectedIndex = (int)spec.ButtonAction;
            root.Children.Add(_action);
            _url = Field("Annot.Field.Url", spec.ButtonUrl ?? "https://");
        }
        root.Children.Add(Ui.Label(loc, "Annot.Style.Font"));
        var fontRow = new StackPanel { Orientation = Orientation.Horizontal };
        _font = new ComboBox { Width = 200, IsEditable = true, Text = spec.Style.FontFamily };
        foreach (var f in new[] { "Arial", "Calibri", "Courier New", "Georgia", "Segoe UI", "Tahoma", "Times New Roman", "Verdana" }) _font.Items.Add(f);
        _fontSize = new TextBox { Width = 60, Margin = new Thickness(6, 0, 0, 0), Text = spec.Style.FontSize > 0 ? spec.Style.FontSize.ToString("0.#", CultureInfo.CurrentCulture) : loc["Annot.Field.Auto"] };
        _fontSize.Tip(loc, "Annot.Style.FontSize");
        fontRow.Children.Add(_font);
        fontRow.Children.Add(_fontSize);
        root.Children.Add(fontRow);
        _required = Check("Annot.Field.Required", spec.Required);
        _readOnly = Check("Annot.Field.ReadOnly", spec.ReadOnly);
        _multiline = Check("Annot.Field.Multiline", spec.Multiline);
        _multiline.Visibility = spec.Kind == FormFieldKind.Text ? Visibility.Visible : Visibility.Collapsed;
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        _error.SetResourceReference(TextBlock.ForegroundProperty, Core.Theming.ThemeKeys.StatusError);
        root.Children.Add(_error);
        root.Children.Add(Ui.ButtonRow(
            Ui.Button(loc, "Annot.Dialog.Ok", (_, _) => Accept(loc), isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true)));
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 700 };
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    public FormFieldSpec? Result { get; private set; }

    private void Accept(ILocalizationService loc)
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) { _error.Text = loc["Annot.Field.NameRequired"]; return; }
        if (name != _spec.Name && _nameTaken(name) && _spec.Kind != FormFieldKind.RadioButton) { _error.Text = loc["Annot.Field.NameTaken"]; return; }
        double size = double.TryParse(_fontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var s) ? Math.Clamp(s, 0, 144) : 0;
        Result = _spec with
        {
            Name = name,
            Tooltip = _tooltip.Text.Trim(),
            DefaultValue = _default.Text,
            Options = _options is null ? _spec.Options : _options.Text.Split('\n').Select(o => o.Trim('\r', ' ')).Where(o => o.Length > 0).ToList(),
            ExportValue = _export is null ? _spec.ExportValue : _export.Text.Trim(),
            DateFormat = _dateFormat?.Text.Trim() ?? _spec.DateFormat,
            ButtonCaption = _caption?.Text ?? _spec.ButtonCaption,
            ButtonAction = _action?.SelectedItem is ComboBoxItem { Tag: FormButtonAction a } ? a : _spec.ButtonAction,
            ButtonUrl = _url?.Text.Trim() ?? _spec.ButtonUrl,
            Required = _required.IsChecked == true,
            ReadOnly = _readOnly.IsChecked == true,
            Multiline = _multiline.IsChecked == true,
            Style = _spec.Style with { FontFamily = string.IsNullOrWhiteSpace(_font.Text) ? "Arial" : _font.Text.Trim(), FontSize = size },
        };
        if (Result.ButtonAction == FormButtonAction.OpenUrl && !Uri.TryCreate(Result.ButtonUrl, UriKind.Absolute, out _)) { _error.Text = loc["Annot.Field.UrlInvalid"]; return; }
        DialogResult = true;
    }
}

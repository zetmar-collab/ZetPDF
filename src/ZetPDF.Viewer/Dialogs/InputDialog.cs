using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.Dialogs;

/// <summary>Proste okno z jednym polem (Powiększ do… Ctrl+M, Przejdź do strony Ctrl+G). Walidacja przez delegat.</summary>
internal sealed class InputDialog : Window
{
    private readonly ComboBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string?> _validate;

    /// <param name="validate">Zwraca komunikat błędu (przetłumaczony) albo null, gdy wartość poprawna.</param>
    public InputDialog(ILocalizationService loc, string titleKey, string promptText, string initial, IEnumerable<string> suggestions, Func<string, string?> validate)
    {
        _validate = validate;
        this.Loc(TitleProperty, loc, titleKey);
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);

        var prompt = new TextBlock { Text = promptText, Margin = new Thickness(0, 0, 0, 6) };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
        _box = new ComboBox { IsEditable = true, Width = 220, Text = initial };
        foreach (var s in suggestions) _box.Items.Add(s);
        System.Windows.Automation.AutomationProperties.SetName(_box, promptText);
        _error = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        _error.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusError);

        var ok = new Button { MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Dialog.Ok");
        var cancel = new Button { MinWidth = 80, IsCancel = true, Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Dialog.Cancel");
        ok.Click += (_, _) =>
        {
            var err = _validate(_box.Text);
            if (err is null) { Value = _box.Text; DialogResult = true; }
            else _error.Text = err;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(prompt);
        root.Children.Add(_box);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) =>
        {
            _box.Focus();
            if (_box.Template.FindName("PART_EditableTextBox", _box) is TextBox tb) tb.SelectAll();
        };
    }

    public string Value { get; private set; } = string.Empty;
}

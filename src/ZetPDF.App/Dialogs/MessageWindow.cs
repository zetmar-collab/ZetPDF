using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Dialogs;

/// <summary>
/// Okno komunikatu w motywie ZetPDF (zamiast systemowego MessageBox, który nie obsługuje motywu ciemnego).
/// Przyciski i teksty przekazuje wołający (już przetłumaczone).
/// </summary>
public sealed class MessageWindow : Window
{
    public sealed record ButtonSpec(string Text, int Result, bool IsDefault = false, bool IsCancel = false);

    private int _result;

    public MessageWindow(string title, string text, MessageSeverity? severity, bool question, IReadOnlyList<ButtonSpec> buttons, int cancelResult, string? details = null, string? detailsHeader = null)
    {
        _result = cancelResult;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        MinWidth = 360;
        MaxWidth = 620;

        var icon = new TextBlock
        {
            FontSize = 30,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Text = question ? "" : severity switch
            {
                MessageSeverity.Error => "",
                MessageSeverity.Warning => "",
                _ => "",
            },
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        icon.SetResourceReference(TextBlock.ForegroundProperty, question ? ThemeKeys.Accent : severity switch
        {
            MessageSeverity.Error => ThemeKeys.StatusError,
            MessageSeverity.Warning => ThemeKeys.StatusWarning,
            _ => ThemeKeys.StatusInfo,
        });

        // Tekst w polu tylko do odczytu – można go zaznaczyć i skopiować (np. ścieżkę pliku w błędzie).
        var message = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
            MaxWidth = 500,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = true,
            IsTabStop = false,
        };
        message.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextPrimary);

        var body = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        var textStack = new StackPanel();
        textStack.Children.Add(message);
        if (!string.IsNullOrWhiteSpace(details))
        {
            var detailsBox = new TextBox
            {
                Text = details,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                MaxHeight = 220,
                MaxWidth = 500,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 11,
            };
            detailsBox.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontMono);
            textStack.Children.Add(new Expander { Header = detailsHeader, Content = detailsBox, Margin = new Thickness(0, 10, 0, 0) });
        }
        body.Children.Add(textStack);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var spec in buttons)
        {
            var b = new Button { Content = spec.Text, IsDefault = spec.IsDefault, IsCancel = spec.IsCancel, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            var result = spec.Result;
            b.Click += (_, _) =>
            {
                _result = result;
                DialogResult = true;
            };
            buttonPanel.Children.Add(b);
        }

        var footer = new Border { Padding = new Thickness(20, 12, 20, 12), Child = buttonPanel };
        footer.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new Border { Padding = new Thickness(20, 20, 24, 20), Child = body });
        Content = root;

        Loaded += (_, _) =>
        {
            var def = buttonPanel.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault) ?? buttonPanel.Children.OfType<Button>().FirstOrDefault();
            def?.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && message.SelectionLength == 0)
            {
                try { Clipboard.SetText(title + Environment.NewLine + text + (details is null ? string.Empty : Environment.NewLine + details)); }
                catch (Exception) { /* schowek zajęty – pomijamy */ }
            }
        };
    }

    public int Result => _result;
}

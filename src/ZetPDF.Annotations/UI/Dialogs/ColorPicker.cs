using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Annotations.UI.Dialogs;

/// <summary>Wybór koloru treści PDF: próbki + pole #RRGGBB. Kolory próbek to kolory treści (wybór użytkownika), nie interfejsu.</summary>
internal sealed class ColorPicker : StackPanel
{
    private static readonly PdfColor[] Swatches =
    {
        new(255, 255, 230, 0), new(255, 255, 160, 0), new(255, 220, 30, 30), new(255, 230, 60, 160), new(255, 140, 60, 200),
        new(255, 30, 110, 220), new(255, 0, 170, 200), new(255, 40, 160, 60), new(255, 120, 200, 60), new(255, 0, 0, 0),
        new(255, 110, 110, 110), new(255, 255, 255, 255),
    };

    private readonly TextBox _hex;
    private readonly Border _preview;
    private PdfColor _color;

    public ColorPicker(ILocalizationService loc, PdfColor initial)
    {
        Orientation = Orientation.Vertical;
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var c in Swatches)
        {
            var b = new Button { Width = 22, Height = 22, Margin = new Thickness(2), Background = Ui.ContentBrush(c), ToolTip = PdfLow.Hex(c) };
            System.Windows.Automation.AutomationProperties.SetName(b, PdfLow.Hex(c));
            b.Click += (_, _) => Color = c;
            grid.Children.Add(b);
        }
        Children.Add(grid);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _preview = new Border { Width = 22, Height = 22, BorderThickness = new Thickness(1), Margin = new Thickness(2, 0, 6, 0) };
        _preview.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.BorderStrong);
        _hex = new TextBox { Width = 80, VerticalContentAlignment = VerticalAlignment.Center };
        _hex.Tip(loc, "Annot.Style.Hex");
        _hex.LostFocus += (_, _) => { if (PdfLow.ParseHex(_hex.Text) is { } c) Color = c; else _hex.Text = PdfLow.Hex(_color); };
        row.Children.Add(_preview);
        row.Children.Add(_hex);
        Children.Add(row);
        Color = initial;
    }

    public PdfColor Color
    {
        get => PdfLow.ParseHex(_hex.Text) ?? _color;
        set
        {
            _color = value;
            _hex.Text = PdfLow.Hex(value);
            _preview.Background = Ui.ContentBrush(value);
        }
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.View;

/// <summary>Dolny pasek widoku: nawigacja po stronach (pole numeru strony) i pole procentowe powiększenia.</summary>
internal sealed class ViewerBar : Border
{
    private readonly DocumentView _view;
    private readonly ILocalizationService _loc;
    private readonly TextBox _page;
    private readonly TextBlock _of;
    private readonly ComboBox _zoom;
    private bool _updating;

    public ViewerBar(DocumentView view, ILocalizationService loc)
    {
        _view = view;
        _loc = loc;
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(4, 2, 4, 2);
        this.Themed(BackgroundProperty, ThemeKeys.Surface).Themed(BorderBrushProperty, ThemeKeys.Border);

        _page = new TextBox { Width = 52, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
        _page.Tip(loc, "Viewer.Bar.PageNumber");
        System.Windows.Automation.AutomationProperties.SetName(_page, loc["Viewer.Bar.PageNumber"]);
        _page.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitPage(); e.Handled = true; } else if (e.Key == Key.Escape) { Refresh(); _view.FocusPages(); e.Handled = true; } };
        _page.LostKeyboardFocus += (_, _) => Refresh();
        _of = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
        _of.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);

        _zoom = new ComboBox { IsEditable = true, Width = 86, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
        _zoom.Tip(loc, "Viewer.Bar.Zoom");
        System.Windows.Automation.AutomationProperties.SetName(_zoom, loc["Viewer.Bar.Zoom"]);
        foreach (var s in PageLayout.ZoomSteps.Where(s => s >= 0.25 && s <= 16)) _zoom.Items.Add(Percent(s));
        _zoom.SelectionChanged += (_, _) => { if (!_updating && _zoom.SelectedItem is string s) CommitZoom(s); };
        _zoom.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitZoom(_zoom.Text); e.Handled = true; } else if (e.Key == Key.Escape) { Refresh(); _view.FocusPages(); e.Handled = true; } };
        _zoom.LostKeyboardFocus += (_, _) => { if (!_zoom.IsKeyboardFocusWithin) Refresh(); };

        var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.FirstPage", (_, _) => _view.GoToPage(0)));
        left.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.PreviousPage", (_, _) => _view.NavigatePage(-1)));
        left.Children.Add(_page);
        left.Children.Add(_of);
        left.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.NextPage", (_, _) => _view.NavigatePage(+1)));
        left.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.LastPage", (_, _) => _view.GoToPage(int.MaxValue)));

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.ZoomOut", (_, _) => _view.ZoomStep(false)));
        right.Children.Add(_zoom);
        right.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.ZoomIn", (_, _) => _view.ZoomStep(true)));
        right.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.FitPage", (_, _) => _view.ZoomMode = ZoomMode.FitPage));
        right.Children.Add(Ui.IconButton("", loc, "Viewer.Bar.FitWidth", (_, _) => _view.ZoomMode = ZoomMode.FitWidth));

        var grid = new Grid();
        grid.Children.Add(left);
        grid.Children.Add(right);
        Child = grid;

        view.CurrentPageChanged += (_, _) => Refresh();
        view.ViewStateChanged += (_, _) => Refresh();
        loc.CultureChanged += (_, _) => Refresh();
        Refresh();
    }

    private static string Percent(double zoom) => (zoom * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";

    public void Refresh()
    {
        _updating = true;
        try
        {
            if (!_page.IsKeyboardFocusWithin) _page.Text = _view.PageCount == 0 ? string.Empty : (_view.CurrentPageIndex + 1).ToString(CultureInfo.CurrentCulture);
            _of.Text = _loc.Format("Viewer.Bar.Of", _view.PageCount);
            if (!_zoom.IsKeyboardFocusWithin)
            {
                _zoom.SelectedItem = null;
                _zoom.Text = Percent(_view.Zoom);
            }
        }
        finally { _updating = false; }
    }

    private void CommitPage()
    {
        if (int.TryParse(_page.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n)) _view.GoToPage(n - 1);
        _view.FocusPages();
        Refresh();
    }

    private void CommitZoom(string text)
    {
        if (TryParsePercent(text, out double z)) _view.Zoom = z;
        _view.FocusPages();
        Refresh();
    }

    /// <summary>„125%”, „125”, „1,25” → 1.25 (procenty).</summary>
    internal static bool TryParsePercent(string? text, out double zoom)
    {
        zoom = 0;
        var t = (text ?? string.Empty).Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0) return false;
        zoom = PageLayout.Clamp(v / 100.0);
        return true;
    }
}

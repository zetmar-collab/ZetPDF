using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Shell;

/// <summary>
/// Pasek stanu: komunikat, nawigacja (pierwsza/poprzednia/następna/ostatnia strona, pole numeru strony / liczba
/// stron), układ stron i powiększenie (suwak logarytmiczny 10–640% + pole z wartościami i trybami dopasowania).
/// Działa na <see cref="IViewerService.ActiveView"/> bezpośrednio przez kontrakt widoku.
/// </summary>
internal sealed class StatusBarView : UserControl
{
    private const double MinZoom = 0.1;
    private const double ZoomBase = 64; // 0.1 · 64 = 6.4 (640%)

    private readonly IViewerService _viewer;
    private readonly ILocalizationService _loc;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBox _page = new() { Width = 46, MinHeight = 22, Padding = new Thickness(2, 0, 2, 0), HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 12 };
    private readonly TextBlock _pageCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), MinWidth = 36 };
    private readonly Slider _zoomSlider = new() { Width = 110, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), SmallChange = 0.02, LargeChange = 0.1, Focusable = false };
    private readonly ComboBox _zoom = new() { Width = 156, IsEditable = true, MinHeight = 22, FontSize = 12, Padding = new Thickness(4, 0, 24, 0) };
    private readonly List<(ToggleButton Button, PageLayoutMode Mode)> _layouts = new();
    private readonly List<ButtonBase> _viewControls = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private IDocumentView? _view;
    private bool _updating;

    public StatusBarView(IViewerService viewer, ILocalizationService loc, ICommandRegistry registry, Services.ShellService events)
    {
        _viewer = viewer;
        _loc = loc;
        Height = 30;
        SetResourceReference(ForegroundProperty, ThemeKeys.StatusBarText);
        _status.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusBarText);
        _pageCount.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusBarText);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(NavButton(registry, CommandIds.Nav.FirstPage, v => v.GoToPage(0)));
        right.Children.Add(NavButton(registry, CommandIds.Nav.PreviousPage, v => v.GoToPage(Math.Max(0, v.CurrentPageIndex - 1))));
        right.Children.Add(_page);
        right.Children.Add(_pageCount);
        right.Children.Add(NavButton(registry, CommandIds.Nav.NextPage, v => v.GoToPage(Math.Min(v.PageCount - 1, v.CurrentPageIndex + 1))));
        right.Children.Add(NavButton(registry, CommandIds.Nav.LastPage, v => v.GoToPage(v.PageCount - 1)));
        right.Children.Add(Separator());
        right.Children.Add(LayoutButton(registry, CommandIds.View.SinglePage, PageLayoutMode.SinglePage));
        right.Children.Add(LayoutButton(registry, CommandIds.View.Continuous, PageLayoutMode.Continuous));
        right.Children.Add(LayoutButton(registry, CommandIds.View.TwoPages, PageLayoutMode.TwoPages));
        right.Children.Add(LayoutButton(registry, CommandIds.View.TwoPagesContinuous, PageLayoutMode.TwoPagesContinuous));
        right.Children.Add(Separator());
        right.Children.Add(NavButton(registry, CommandIds.View.ZoomOut, v => SetZoom(v, v.Zoom / 1.25)));
        right.Children.Add(_zoomSlider);
        right.Children.Add(NavButton(registry, CommandIds.View.ZoomIn, v => SetZoom(v, v.Zoom * 1.25)));
        right.Children.Add(_zoom);
        _page.SetBinding(ToolTipProperty, Loc.Bind(loc, "Cmd.Nav.GoToPage"));
        _zoomSlider.SetBinding(ToolTipProperty, Loc.Bind(loc, "Shell.Status.Zoom"));
        _zoom.SetBinding(ToolTipProperty, Loc.Bind(loc, "Shell.Status.Zoom"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(_page, "Shell.Status.Page");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_zoom, "Shell.Status.Zoom");

        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 0, 6, 0) };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(_status);
        Content = dock;

        _page.KeyDown += OnPageKeyDown;
        _page.LostKeyboardFocus += (_, _) => Update();
        _page.GotKeyboardFocus += (_, _) => _page.SelectAll();
        _zoomSlider.ValueChanged += (_, e) =>
        {
            if (_updating || _view is null) return;
            SetZoom(_view, MinZoom * Math.Pow(ZoomBase, e.NewValue));
        };
        _zoom.SelectionChanged += (_, _) =>
        {
            if (_updating || _view is null || _zoom.SelectedItem is not ComboBoxItem { Tag: object tag }) return;
            Apply(tag);
        };
        _zoom.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _view is null) return;
            if (TryParsePercent(_zoom.Text, out var z)) SetZoom(_view, z);
            Update();
            e.Handled = true;
        };
        _zoom.LostKeyboardFocus += (_, _) => Update();

        _viewer.ActiveViewChanged += (_, v) => Attach(v);
        _loc.CultureChanged += (_, _) =>
        {
            BuildZoomItems();
            Update();
        };
        events.StatusRequested += (_, text) => SetStatus(text);
        _statusTimer.Tick += (_, _) => SetStatus(null);
        BuildZoomItems();
        SetStatus(null);
        Attach(_viewer.ActiveView);
    }

    public void SetStatus(string? text)
    {
        _statusTimer.Stop();
        if (text is null) _status.SetBinding(TextBlock.TextProperty, Loc.Bind(_loc, "Shell.Status.Ready"));
        else
        {
            BindingOperationsClear();
            _status.Text = text;
            _statusTimer.Start();
        }
    }

    private void BindingOperationsClear() => System.Windows.Data.BindingOperations.ClearBinding(_status, TextBlock.TextProperty);

    private Rectangle Separator()
    {
        var r = new Rectangle();
        r.SetResourceReference(StyleProperty, "Shell.StatusSeparator");
        return r;
    }

    private Button NavButton(ICommandRegistry registry, string commandId, Action<IDocumentView> action)
    {
        var d = registry.GetDescriptor(commandId);
        var glyph = new TextBlock { Text = d?.IconGlyph ?? "?", FontSize = 12 };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        var b = new Button { Content = glyph };
        b.SetResourceReference(StyleProperty, "Shell.StatusButton");
        if (d is not null) b.SetBinding(ToolTipProperty, Loc.Bind(_loc, d.TextKey));
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "Shell.Status." + commandId);
        if (d is not null) b.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, Loc.Bind(_loc, d.TextKey));
        b.Click += (_, _) =>
        {
            if (_view is not null) action(_view);
            Update();
        };
        _viewControls.Add(b);
        return b;
    }

    private ToggleButton LayoutButton(ICommandRegistry registry, string commandId, PageLayoutMode mode)
    {
        var d = registry.GetDescriptor(commandId);
        var glyph = new TextBlock { Text = d?.IconGlyph ?? "?", FontSize = 12 };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.FontIcons);
        var b = new ToggleButton { Content = glyph };
        b.SetResourceReference(StyleProperty, "Shell.StatusButton");
        if (d is not null)
        {
            b.SetBinding(ToolTipProperty, Loc.Bind(_loc, d.TextKey));
            b.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, Loc.Bind(_loc, d.TextKey));
        }
        b.Click += (_, _) =>
        {
            if (_view is not null) _view.Layout = mode;
            Update();
        };
        _layouts.Add((b, mode));
        _viewControls.Add(b);
        return b;
    }

    private void BuildZoomItems()
    {
        _updating = true;
        try
        {
            _zoom.Items.Clear();
            void Add(string text, object tag) => _zoom.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            Add(_loc["Cmd.View.FitPage"], ZoomMode.FitPage);
            Add(_loc["Cmd.View.FitWidth"], ZoomMode.FitWidth);
            Add(_loc["Cmd.View.FitVisible"], ZoomMode.FitVisible);
            Add(_loc["Cmd.View.ActualSize"], ZoomMode.ActualSize);
            foreach (var p in new[] { 25, 50, 75, 100, 125, 150, 200, 300, 400, 640 }) Add(p.ToString(CultureInfo.CurrentCulture) + "%", p / 100.0);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Apply(object tag)
    {
        if (_view is null) return;
        if (tag is ZoomMode mode) _view.ZoomMode = mode;
        else if (tag is double z) SetZoom(_view, z);
        Dispatcher.BeginInvoke(Update);
    }

    private static void SetZoom(IDocumentView v, double zoom)
    {
        v.ZoomMode = ZoomMode.Custom;
        v.Zoom = Math.Clamp(zoom, MinZoom, MinZoom * ZoomBase);
    }

    /// <summary>"125%", "125", "125,5 %" → 1.25.</summary>
    internal static bool TryParsePercent(string? text, out double zoom)
    {
        zoom = 0;
        var t = (text ?? string.Empty).Replace("%", string.Empty, StringComparison.Ordinal).Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p <= 0) return false;
        zoom = Math.Clamp(p / 100.0, MinZoom, MinZoom * ZoomBase);
        return true;
    }

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        if (_view is null) return;
        if (e.Key == Key.Enter)
        {
            if (int.TryParse(_page.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && _view.PageCount > 0)
                _view.GoToPage(Math.Clamp(n, 1, _view.PageCount) - 1);
            Update();
            _page.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Update();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void Attach(IDocumentView? view)
    {
        if (_view is not null)
        {
            _view.CurrentPageChanged -= OnViewChanged;
            _view.ViewStateChanged -= OnViewChanged;
        }
        _view = view;
        if (_view is not null)
        {
            _view.CurrentPageChanged += OnViewChanged;
            _view.ViewStateChanged += OnViewChanged;
        }
        Update();
    }

    private void OnViewChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Update);

    private void Update()
    {
        _updating = true;
        try
        {
            var v = _view;
            var has = v is not null;
            foreach (var c in _viewControls) c.IsEnabled = has;
            _page.IsEnabled = has && v!.PageCount > 0;
            _zoom.IsEnabled = has;
            _zoomSlider.IsEnabled = has;
            if (v is null)
            {
                if (!_page.IsKeyboardFocused) _page.Text = string.Empty;
                _pageCount.Text = string.Empty;
                _zoom.Text = string.Empty;
                foreach (var (b, _) in _layouts) b.IsChecked = false;
                return;
            }
            if (!_page.IsKeyboardFocused) _page.Text = v.PageCount > 0 ? (v.CurrentPageIndex + 1).ToString(CultureInfo.CurrentCulture) : string.Empty;
            _pageCount.Text = _loc.Format("Shell.Status.PageCount", v.PageCount);
            foreach (var (b, mode) in _layouts) b.IsChecked = v.Layout == mode;
            _zoomSlider.Value = Math.Log(Math.Clamp(v.Zoom, MinZoom, MinZoom * ZoomBase) / MinZoom, ZoomBase);
            if (!_zoom.IsKeyboardFocusWithin)
            {
                _zoom.SelectedItem = null;
                _zoom.Text = v.ZoomMode switch
                {
                    ZoomMode.FitPage => _loc["Cmd.View.FitPage"],
                    ZoomMode.FitWidth => _loc["Cmd.View.FitWidth"],
                    ZoomMode.FitVisible => _loc["Cmd.View.FitVisible"],
                    _ => (v.Zoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%",
                };
            }
        }
        finally
        {
            _updating = false;
        }
    }
}

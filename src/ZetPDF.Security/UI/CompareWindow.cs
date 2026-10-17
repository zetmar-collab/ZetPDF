using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Security.UI;

/// <summary>
/// Wyniki porównania: lista zmian i widok obok siebie (strona dokumentu starego i nowego) z podświetleniem
/// wstawionych / usuniętych / zmienionych fragmentów (pędzle motywu Brush.Diff.*). Ctrl+. / Ctrl+, – następna/poprzednia zmiana.
/// </summary>
internal sealed class CompareWindow : Window
{
    private const double RenderScale = 1.5;
    private readonly ILocalizationService _loc;
    private readonly CompareResult _result;
    private readonly IRenderDocument? _docA, _docB;
    private readonly ListBox _changes = new();
    private readonly Image _imageA = new(), _imageB = new();
    private readonly Canvas _overlayA = new(), _overlayB = new();
    private readonly TextBlock _headerA, _headerB, _summary;
    private int _shownA = -2, _shownB = -2;
    private int _renderVersion;

    public CompareWindow(ILocalizationService loc, CompareResult result, IRenderDocument? docA, IRenderDocument? docB, Action report)
    {
        _loc = loc;
        _result = result;
        _docA = docA;
        _docB = docB;
        this.Loc(TitleProperty, loc, "Security.Compare.Window.Title");
        Width = 1200;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Themed(BackgroundProperty, ThemeKeys.WindowBackground).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontUi);

        _summary = Ui.Plain(string.Empty);
        _summary.Margin = new Thickness(8);
        _headerA = Ui.Plain(string.Empty, wrap: false);
        _headerB = Ui.Plain(string.Empty, wrap: false);

        foreach (var c in result.Changes) _changes.Items.Add(new ListBoxItem { Content = BuildChangeItem(c), Tag = c });
        _changes.SelectionChanged += (_, _) => ShowSelected();
        System.Windows.Automation.AutomationProperties.SetName(_changes, loc["Security.Compare.Changes"]);

        var toolbar = Ui.Row(
            Ui.Button(loc, "Security.Compare.Previous", (_, _) => Move(-1)),
            Ui.Button(loc, "Security.Compare.Next", (_, _) => Move(+1)),
            Ui.Button(loc, "Security.Compare.ReportButton", (_, _) => report()));
        toolbar.Margin = new Thickness(8, 8, 8, 0);

        var left = new DockPanel { Width = 330 };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Top);
        left.Children.Add(toolbar);
        left.Children.Add(_summary);
        left.Children.Add(_changes);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        var colA = Column(_headerA, _imageA, _overlayA);
        var colB = Column(_headerB, _imageB, _overlayB);
        Grid.SetColumn(colA, 1);
        Grid.SetColumn(colB, 2);
        grid.Children.Add(colA);
        grid.Children.Add(colB);
        Content = grid;

        InputBindings.Add(new KeyBinding(new RelayCommand(() => Move(+1)), Key.OemPeriod, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Move(-1)), Key.OemComma, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(Close), Key.Escape, ModifierKeys.None));
        _loc.CultureChanged += OnCultureChanged;
        Closed += (_, _) => _loc.CultureChanged -= OnCultureChanged;

        UpdateSummary();
        Loaded += (_, _) =>
        {
            if (_changes.Items.Count > 0) _changes.SelectedIndex = 0;
            else _ = ShowPagesAsync(PageOrNone(0, true), PageOrNone(0, false), null);
        };
    }

    private int PageOrNone(int index, bool a) => a ? (_result.PageCountA > index ? index : -1) : (_result.PageCountB > index ? index : -1);

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        UpdateSummary();
        foreach (ListBoxItem item in _changes.Items) item.Content = BuildChangeItem((CompareChange)item.Tag);
        _shownA = _shownB = -2;
        ShowSelected();
    }

    private void UpdateSummary()
    {
        var r = _result;
        _summary.Text = r.Changes.Count == 0 ? _loc["Security.Compare.NoChanges"]
            : _loc.Format("Security.Compare.Report.Summary", r.Changes.Count, r.Changes.Count(x => x.Kind == CompareChangeKind.Inserted),
                r.Changes.Count(x => x.Kind == CompareChangeKind.Deleted), r.Changes.Count(x => x.Kind == CompareChangeKind.Changed));
    }

    private FrameworkElement BuildChangeItem(CompareChange c)
    {
        var culture = _loc.CurrentCulture;
        var p = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
        var head = Ui.Plain(string.Format(culture, "{0}. {1} – {2} / {3}", c.Index + 1, CompareService.KindText(c.Kind, culture), CompareService.PageText(c.PageA), CompareService.PageText(c.PageB)), wrap: false);
        head.FontWeight = FontWeights.SemiBold;
        var swatch = new Rectangle { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0) };
        swatch.SetResourceReference(Shape.FillProperty, BrushKey(c.Kind));
        p.Children.Add(Ui.Row(swatch, head));
        if (c.TextA.Length == 0 && c.TextB.Length == 0) p.Children.Add(Ui.Plain(_loc["Security.Compare.Graphic"], secondary: true));
        if (c.TextA.Length > 0) p.Children.Add(Ui.Plain("− " + Trim(c.TextA), secondary: true));
        if (c.TextB.Length > 0) p.Children.Add(Ui.Plain("+ " + Trim(c.TextB), secondary: true));
        return p;
    }

    private static string Trim(string s) => s.Length > 160 ? s[..160] + "…" : s;

    private static string BrushKey(CompareChangeKind k) => k switch
    {
        CompareChangeKind.Inserted => ThemeKeys.DiffInserted,
        CompareChangeKind.Deleted => ThemeKeys.DiffDeleted,
        _ => ThemeKeys.DiffChanged,
    };

    private static FrameworkElement Column(TextBlock header, Image image, Canvas overlay)
    {
        header.Margin = new Thickness(8, 8, 8, 4);
        var stack = new Grid();
        stack.Children.Add(image);
        stack.Children.Add(overlay);
        var page = new Border { Child = stack, Margin = new Thickness(8), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        page.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.ViewerPageBorder);
        var box = new Viewbox { Child = page, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, VerticalAlignment = VerticalAlignment.Top };
        var area = new Border { Child = box };
        area.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ViewerBackground);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(area);
        return dock;
    }

    /// <summary>Przejście do następnej (+1) lub poprzedniej (-1) zmiany.</summary>
    public void Move(int delta)
    {
        if (_changes.Items.Count == 0) return;
        int i = _changes.SelectedIndex < 0 ? (delta > 0 ? 0 : _changes.Items.Count - 1) : _changes.SelectedIndex + delta;
        _changes.SelectedIndex = Math.Clamp(i, 0, _changes.Items.Count - 1);
        _changes.ScrollIntoView(_changes.SelectedItem);
    }

    private void ShowSelected()
    {
        if (_changes.SelectedItem is not ListBoxItem { Tag: CompareChange c }) return;
        int pa = c.PageA, pb = c.PageB;
        // druga strona z mapy stron, gdy zmiana jest tylko po jednej stronie
        if (pa < 0) pa = _result.PageMap.FirstOrDefault(m => m.PageB == pb && m.PageA >= 0, (PageA: -1, PageB: -1)).PageA;
        if (pb < 0) pb = _result.PageMap.FirstOrDefault(m => m.PageA == pa && m.PageB >= 0, (PageA: -1, PageB: -1)).PageB;
        _ = ShowPagesAsync(pa, pb, c);
    }

    private async Task ShowPagesAsync(int pageA, int pageB, CompareChange? current)
    {
        int version = ++_renderVersion;
        var culture = _loc.CurrentCulture;
        _headerA.Text = _loc.Format("Security.Compare.Window.Old", _result.NameA, pageA >= 0 ? (pageA + 1).ToString(culture) : "–");
        _headerB.Text = _loc.Format("Security.Compare.Window.New", _result.NameB, pageB >= 0 ? (pageB + 1).ToString(culture) : "–");
        try
        {
            if (pageA != _shownA) { _imageA.Source = await RenderAsync(_docA, pageA); _shownA = pageA; }
            if (version != _renderVersion) return;
            if (pageB != _shownB) { _imageB.Source = await RenderAsync(_docB, pageB); _shownB = pageB; }
            if (version != _renderVersion) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PdfCorruptException or OperationCanceledException)
        {
            return;
        }
        DrawOverlay(_overlayA, _imageA, _docA, pageA, true, current);
        DrawOverlay(_overlayB, _imageB, _docB, pageB, false, current);
    }

    private static async Task<ImageSource?> RenderAsync(IRenderDocument? doc, int page)
    {
        if (doc is null || page < 0 || page >= doc.PageCount) return null;
        var bmp = await doc.RenderPageAsync(page, new RenderRequest(RenderScale));
        var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null, bmp.Bgra, bmp.Stride);
        src.Freeze();
        return src;
    }

    private void DrawOverlay(Canvas canvas, Image image, IRenderDocument? doc, int page, bool sideA, CompareChange? current)
    {
        canvas.Children.Clear();
        if (doc is null || page < 0 || page >= doc.PageCount || image.Source is null) return;
        var info = doc.Pages[page];
        canvas.Width = image.Source.Width;
        canvas.Height = image.Source.Height;
        var transform = new PageTransform(info.Size, info.CropOrigin, info.Rotation, RenderScale, new Point(0, 0));
        foreach (var c in _result.Changes)
        {
            if ((sideA ? c.PageA : c.PageB) != page) continue;
            var rects = sideA ? c.RectsA : c.RectsB;
            bool isCurrent = ReferenceEquals(c, current) || (current is not null && c.Index == current.Index);
            foreach (var r in rects)
            {
                var v = transform.ToView(r);
                var shape = new Rectangle { Width = Math.Max(2, v.Width + 2), Height = Math.Max(2, v.Height + 2), Opacity = isCurrent ? 0.55 : 0.3, StrokeThickness = isCurrent ? 2 : 0 };
                shape.SetResourceReference(Shape.FillProperty, BrushKey(c.Kind));
                shape.SetResourceReference(Shape.StrokeProperty, ThemeKeys.Focus);
                Canvas.SetLeft(shape, v.X - 1);
                Canvas.SetTop(shape, v.Y - 1);
                canvas.Children.Add(shape);
            }
        }
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _a;
        public RelayCommand(Action a) => _a = a;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _a();
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using PageRange = ZetPDF.Core.Utilities.PageRange;
using ZetPDF.Core.Localization;

namespace ZetPDF.Viewer.Printing;

/// <summary>Okno druku ZetPDF (Ctrl+P) z podglądem pierwszego arkusza. Wynik: <see cref="Result"/>.</summary>
internal sealed class PrintDialogWindow : Window
{
    private readonly IRenderDocument _doc;
    private readonly ILocalizationService _loc;
    private readonly int _currentPage;
    private readonly PrintSettingsPanel _settings;
    private readonly RadioButton _all = new() { GroupName = "range", IsChecked = true };
    private readonly RadioButton _current = new() { GroupName = "range" };
    private readonly RadioButton _custom = new() { GroupName = "range" };
    private readonly TextBox _rangeText = new() { Width = 140 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _sheetInfo = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Image _preview = new() { Width = 260, Height = 340, Stretch = Stretch.Uniform };
    private readonly DispatcherTimer _previewTimer;
    private CancellationTokenSource? _previewCts;

    public PrintDialogWindow(IRenderDocument doc, ILocalizationService loc, int currentPage)
    {
        _doc = doc;
        _loc = loc;
        _currentPage = Math.Clamp(currentPage, 0, Math.Max(0, doc.PageCount - 1));
        this.Loc(TitleProperty, loc, "Viewer.Print.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);
        _error.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusError);
        _sheetInfo.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);

        var (printers, def) = PrintService.GetPrinters();
        _settings = new PrintSettingsPanel(loc, printers, def, "print") { Width = 330 };

        _all.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.RangeAll");
        _current.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.RangeCurrent");
        _custom.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.RangePages");
        _rangeText.Tip(loc, "Viewer.Print.RangeHint");
        _rangeText.GotKeyboardFocus += (_, _) => _custom.IsChecked = true;

        var left = _settings;
        left.Children.Insert(1, _settings.Section("Viewer.Print.Range", PrintSettingsPanel.Stack(_all, _current, PrintSettingsPanel.Row(_custom, _rangeText))));
        left.Children.Add(_error);

        var previewBorder = new Border { BorderThickness = new Thickness(1), Child = _preview, Padding = new Thickness(4) };
        previewBorder.Themed(Border.BorderBrushProperty, ThemeKeys.Border).Themed(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(Ui.Label(loc, "Viewer.Print.Preview", 0));
        right.Children.Add(previewBorder);
        right.Children.Add(_sheetInfo);

        var ok = new Button { MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Print.Ok");
        var cancel = new Button { MinWidth = 90, IsCancel = true, Padding = new Thickness(10, 3, 10, 3) }.Loc(ContentControl.ContentProperty, loc, "Viewer.Dialog.Cancel");
        ok.Click += (_, _) => { if (TryBuild(out var o, showError: true)) { Result = o; DialogResult = true; } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        if (!_settings.HasPrinters) { ok.IsEnabled = false; _error.Text = loc["Viewer.Print.NoPrinters"]; }

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(left);
        body.Children.Add(right);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = UpdatePreviewAsync(); };
        foreach (var t in new[] { _all, _current, _custom }) { t.Checked += (_, _) => SchedulePreview(); t.Unchecked += (_, _) => SchedulePreview(); }
        _rangeText.TextChanged += (_, _) => SchedulePreview();
        _settings.Changed += (_, _) => SchedulePreview();
        Loaded += (_, _) => SchedulePreview();
        Closed += (_, _) => _previewCts?.Cancel();
    }

    public PrintOptions? Result { get; private set; }

    private bool TryBuild(out PrintOptions options, bool showError)
    {
        options = null!;
        string range = _all.IsChecked == true ? string.Empty : _current.IsChecked == true ? (_currentPage + 1).ToString(CultureInfo.InvariantCulture) : _rangeText.Text;
        if (!PageRange.TryParse(range, _doc.PageCount, out var pages) || pages.Count == 0)
        {
            if (showError) _error.Text = _loc.Format("Viewer.Print.InvalidRange", _doc.PageCount);
            return false;
        }
        if (!_settings.TryBuild(range, out options, out var error))
        {
            if (showError) _error.Text = error ?? string.Empty;
            return false;
        }
        _error.Text = string.Empty;
        return true;
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task UpdatePreviewAsync()
    {
        _previewCts?.Cancel();
        if (!TryBuild(out var options, showError: false)) { _preview.Source = null; _sheetInfo.Text = string.Empty; return; }
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        try
        {
            var pages = PageRange.Parse(options.PageRange, _doc.PageCount);
            var sheets = PrintLayout.SheetsFor(pages, options);
            _sheetInfo.Text = options.Booklet
                ? _loc.Format("Viewer.Print.Booklet.Count", sheets.Count / 2, BookletImposition.PaddedCount(pages.Count) - pages.Count)
                : _loc.Format("Viewer.Print.SheetCount", sheets.Count);
            var paper = new Size(PdfSize.A4.Width * PrintLayout.DipPerPoint, PdfSize.A4.Height * PrintLayout.DipPerPoint);
            var printable = new Rect(24, 24, paper.Width - 48, paper.Height - 48);
            if (options.Booklet) (paper, printable) = PrintLayout.ForBooklet(paper, printable);
            var placements = PrintLayout.LayoutSheet(sheets[0], i => new Size(_doc.Pages[i].DisplaySize.Width, _doc.Pages[i].DisplaySize.Height), printable, options);
            var bitmaps = await PrintService.RenderBitmapsAsync(_doc, placements, 50, options, cts.Token);
            if (cts.IsCancellationRequested) return;
            var visual = PrintService.ComposeSheet(paper, bitmaps);
            var rtb = new RenderTargetBitmap((int)paper.Width, (int)paper.Height, 96, 96, PixelFormats.Pbgra32);
            var host = new DrawingVisual();
            using (var dc = host.RenderOpen())
            {
                // podgląd papieru (kolor treści wydruku – biały arkusz)
                dc.DrawRectangle(Brushes.White, null, new Rect(paper));
                dc.DrawRectangle(new VisualBrush(visual), null, new Rect(paper));
            }
            rtb.Render(host);
            rtb.Freeze();
            _preview.Source = rtb;
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _error.Text = _loc.Describe(ex); }
    }
}

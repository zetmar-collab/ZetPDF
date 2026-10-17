using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Editing.Imaging;

namespace ZetPDF.Editing.Ui.Dialogs;

/// <summary>Opcje OCR dokumentu: strony, języki, pomijanie stron z tekstem, próg podejrzanych, przegląd przed zapisem.</summary>
internal sealed class OcrDialog : DialogBase
{
    private readonly PageRangePicker _range;
    private readonly CheckBox _pol, _eng, _skip, _review;
    private readonly TextBox _threshold;

    public OcrDialog(ILocalizationService loc, int currentPage, int count, string languages, int threshold, bool review) : base(loc, "Edit.Ocr.Title")
    {
        _range = new PageRangePicker(loc, currentPage, count, "ocrrange");
        _pol = Ui.Check(loc, "Edit.Ocr.Polish", languages.Contains("pol", StringComparison.Ordinal));
        _eng = Ui.Check(loc, "Edit.Ocr.English", languages.Contains("eng", StringComparison.Ordinal));
        _skip = Ui.Check(loc, "Edit.Ocr.SkipText", true);
        _review = Ui.Check(loc, "Edit.Ocr.Review", review);
        _threshold = Ui.Box(threshold.ToString(CultureInfo.CurrentCulture), 60);
        _threshold.HorizontalAlignment = HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(_threshold, loc["Edit.Ocr.Threshold"]);
        var langs = new StackPanel { Orientation = Orientation.Horizontal };
        _pol.Margin = new Thickness(0, 4, 12, 4);
        langs.Children.Add(_pol);
        langs.Children.Add(_eng);
        SetBody(Ui.Form((Ui.Label(loc, "Edit.Range.Title"), _range), (Ui.Label(loc, "Edit.Ocr.Languages"), langs), (null, _skip),
            (Ui.Label(loc, "Edit.Ocr.Threshold"), _threshold), (null, _review), (null, Ui.Label(loc, "Edit.Ocr.Note", secondary: true))));
        AddOkCancel("Edit.Ocr.Start");
    }

    public string Languages => string.Join('+', new[] { _pol.IsChecked == true ? "pol" : null, _eng.IsChecked == true ? "eng" : null }.Where(x => x is not null));
    public int Threshold => int.TryParse(_threshold.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var t) ? Math.Clamp(t, 0, 100) : 70;
    public bool Review => _review.IsChecked == true;

    public OcrOptions Options => new() { Languages = Languages, Pages = _range.Pages, SkipPagesWithText = _skip.IsChecked == true, SuspectThreshold = Threshold };

    protected override bool Validate()
    {
        if (Languages.Length == 0) { ShowError(L["Edit.Ocr.NoLanguage"]); return false; }
        if (_range.RangeText is null) { ShowError(L["Edit.Range.Invalid"]); return false; }
        if (!int.TryParse(_threshold.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var t) || t < 0 || t > 100) { ShowError(L["Edit.Ocr.InvalidThreshold"]); return false; }
        return true;
    }
}

/// <summary>
/// Przegląd podejrzanych wyników OCR: wycinek obrazu słowa, rozpoznany tekst do poprawienia, pewność.
/// Zatwierdzenie zwraca wynik z poprawkami (słowa przestają być podejrzane), z którego powstaje warstwa tekstu.
/// </summary>
internal sealed class OcrReviewWindow : Window
{
    private const double CropDpi = 200;
    private readonly ILocalizationService _loc;
    private readonly OcrResult _result;
    private readonly List<(OcrPageResult Page, int Index, TextBox Box, Image Image)> _rows = new();
    private readonly byte[] _pdf;
    private readonly IPdfRenderer _renderer;

    public OcrReviewWindow(ILocalizationService loc, OcrResult result, byte[] pdf, IPdfRenderer renderer)
    {
        _loc = loc;
        _result = result;
        _pdf = pdf;
        _renderer = renderer;
        this.Loc(TitleProperty, loc, "Edit.Ocr.ReviewTitle");
        Width = 620;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);

        var list = new StackPanel();
        foreach (var page in result.Pages)
            for (int i = 0; i < page.Words.Count; i++)
            {
                var w = page.Words[i];
                if (!w.IsSuspect) continue;
                if (_rows.Count >= 500) break;
                var image = new Image { Height = 36, MaxWidth = 300, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 10, 0) };
                var box = Ui.Box(w.Text, 200);
                System.Windows.Automation.AutomationProperties.SetName(box, loc.Format("Edit.Ocr.WordAutomation", page.PageIndex + 1, w.Confidence.ToString("0", CultureInfo.CurrentCulture)));
                var info = Ui.Text(loc.Format("Edit.Ocr.WordInfo", page.PageIndex + 1, w.Confidence.ToString("0", CultureInfo.CurrentCulture)), secondary: true);
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                Grid.SetColumn(box, 1);
                Grid.SetColumn(info, 2);
                row.Children.Add(image);
                row.Children.Add(box);
                row.Children.Add(info);
                var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 2, 0, 2), Child = row }.Themed(Border.BorderBrushProperty, ThemeKeys.Border);
                list.Children.Add(border);
                _rows.Add((page, i, box, image));
            }

        var header = Ui.Text(loc.Format("Edit.Ocr.ReviewCount", result.SuspectWords.Count()));
        header.Margin = new Thickness(0, 0, 0, 8);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Ui.Button(loc, "Edit.Ocr.ApplyLayer", (_, _) => DialogResult = true, isDefault: true));
        buttons.Children.Add(Ui.Button(loc, "Edit.Dialog.Cancel", (_, _) => DialogResult = false, isCancel: true));
        var dock = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
        Loaded += async (_, _) => await LoadCropsAsync();
    }

    private async Task LoadCropsAsync()
    {
        try
        {
            using var doc = await _renderer.OpenAsync(_pdf);
            foreach (var group in _rows.GroupBy(r => r.Page.PageIndex))
            {
                if (!IsVisible) return;
                var info = doc.Pages[group.Key];
                var bmp = await doc.RenderPageAsync(group.Key, new RenderRequest(CropDpi / 72.0, 0, null, RenderFlags.None));
                var full = ImageCodec.ToBitmapSource(bmp, 96);
                var t = new PageTransform(info.Size, info.CropOrigin, info.Rotation, bmp.Scale, new Point(0, 0));
                foreach (var (page, index, _, image) in group)
                {
                    var r = t.ToView(page.Words[index].Box.Inflate(3));
                    int x = (int)Math.Max(0, r.Left), y = (int)Math.Max(0, r.Top);
                    int w = (int)Math.Min(bmp.Width - x, r.Width), h = (int)Math.Min(bmp.Height - y, r.Height);
                    if (w < 2 || h < 2) continue;
                    var crop = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
                    crop.Freeze();
                    image.Source = crop;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Wycinki są pomocnicze – przegląd działa bez nich.
        }
    }

    /// <summary>Wynik z poprawkami użytkownika (puste pole = słowo usunięte z warstwy).</summary>
    public OcrResult Corrected
    {
        get
        {
            var edits = _rows.ToDictionary(r => (r.Page.PageIndex, r.Index), r => r.Box.Text.Trim());
            var pages = _result.Pages.Select(p => p with
            {
                Words = p.Words.Select((w, i) => edits.TryGetValue((p.PageIndex, i), out var t) ? w with { Text = t, IsSuspect = false } : w)
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList(),
            }).ToList();
            return new OcrResult(pages); // RegionOfInterest stron zachowany przez „with”
        }
    }
}

/// <summary>Wyszukaj i redaguj: frazy + wzorce, lista trafień do zatwierdzenia.</summary>
internal sealed class SearchRedactDialog : DialogBase
{
    private static readonly (SensitiveDataPattern P, string Key)[] PatternKeys =
    {
        (SensitiveDataPattern.Pesel, "Edit.Redact.Pattern.Pesel"), (SensitiveDataPattern.Nip, "Edit.Redact.Pattern.Nip"),
        (SensitiveDataPattern.Email, "Edit.Redact.Pattern.Email"), (SensitiveDataPattern.PhonePl, "Edit.Redact.Pattern.Phone"),
        (SensitiveDataPattern.CardNumber, "Edit.Redact.Pattern.Card"), (SensitiveDataPattern.IbanPl, "Edit.Redact.Pattern.Iban"),
    };

    private readonly Func<RedactionSearch, Task<IReadOnlyList<RedactionCandidate>>> _find;
    private readonly TextBox _phrases = Ui.Box(multiline: true);
    private readonly List<(SensitiveDataPattern P, CheckBox Box)> _patterns = new();
    private readonly CheckBox _case, _word;
    private readonly ListBox _results = new() { Height = 200, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _summary = Ui.Text(string.Empty, secondary: true);
    private readonly Button _searchButton;

    public SearchRedactDialog(ILocalizationService loc, Func<RedactionSearch, Task<IReadOnlyList<RedactionCandidate>>> find) : base(loc, "Edit.Redact.Search.Title", 600)
    {
        _find = find;
        System.Windows.Automation.AutomationProperties.SetName(_phrases, loc["Edit.Redact.Search.Phrases"]);
        System.Windows.Automation.AutomationProperties.SetName(_results, loc["Edit.Redact.Search.Results"]);
        var patterns = new WrapPanel();
        foreach (var (p, key) in PatternKeys)
        {
            var cb = Ui.Check(loc, key, false);
            cb.Margin = new Thickness(0, 3, 14, 3);
            patterns.Children.Add(cb);
            _patterns.Add((p, cb));
        }
        _case = Ui.Check(loc, "Edit.Search.MatchCase", false);
        _word = Ui.Check(loc, "Edit.Search.WholeWord", false);
        var opts = new StackPanel { Orientation = Orientation.Horizontal };
        _case.Margin = new Thickness(0, 3, 14, 3);
        opts.Children.Add(_case);
        opts.Children.Add(_word);
        _searchButton = Ui.Button(loc, "Edit.Redact.Search.Find", async (_, _) => await SearchAsync());
        _searchButton.HorizontalAlignment = HorizontalAlignment.Left;
        _searchButton.Margin = new Thickness(0, 6, 0, 0);
        var selectAll = Ui.Button(loc, "Edit.Redact.Search.SelectAll", (_, _) => SetAll(true));
        var selectNone = Ui.Button(loc, "Edit.Redact.Search.SelectNone", (_, _) => SetAll(false));
        var sel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        selectAll.Margin = new Thickness(0, 0, 6, 0);
        sel.Children.Add(selectAll);
        sel.Children.Add(selectNone);
        sel.Children.Add(_summary);
        _summary.Margin = new Thickness(10, 0, 0, 0);

        var root = new StackPanel();
        root.Children.Add(Ui.Label(loc, "Edit.Redact.Search.Phrases"));
        root.Children.Add(_phrases);
        root.Children.Add(Ui.Label(loc, "Edit.Redact.Search.Patterns"));
        root.Children.Add(patterns);
        root.Children.Add(opts);
        root.Children.Add(_searchButton);
        root.Children.Add(_results);
        root.Children.Add(sel);
        SetBody(root);
        AddOkCancel("Edit.Redact.Search.Mark");
    }

    private void SetAll(bool value)
    {
        foreach (var cb in _results.Items.OfType<CheckBox>()) cb.IsChecked = value;
    }

    private RedactionSearch Search => new()
    {
        Phrases = _phrases.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
        Patterns = _patterns.Where(p => p.Box.IsChecked == true).Aggregate(SensitiveDataPattern.None, (a, p) => a | p.P),
        Options = (_case.IsChecked == true ? TextSearchOptions.MatchCase : 0) | (_word.IsChecked == true ? TextSearchOptions.WholeWord : 0),
    };

    private async Task SearchAsync()
    {
        var s = Search;
        if (s.Phrases.Count == 0 && s.Patterns == SensitiveDataPattern.None) { ShowError(L["Edit.Redact.Search.Nothing"]); return; }
        ShowError(null);
        _searchButton.IsEnabled = false;
        try
        {
            var found = await _find(s);
            _results.Items.Clear();
            foreach (var c in found)
            {
                var kind = c.Pattern == SensitiveDataPattern.None ? L["Edit.Redact.Pattern.Phrase"] : L[PatternKeys.First(p => p.P == c.Pattern).Key];
                _results.Items.Add(new CheckBox { IsChecked = true, Tag = c, Content = L.Format("Edit.Redact.Search.Item", c.PageIndex + 1, kind, c.Text) });
            }
            _summary.Text = L.Format("Edit.Redact.Search.Found", found.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex.Message);
        }
        finally { _searchButton.IsEnabled = true; }
    }

    public IReadOnlyList<RedactionCandidate> Selected => _results.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (RedactionCandidate)c.Tag).ToList();

    protected override bool Validate()
    {
        if (Selected.Count == 0) { ShowError(L["Edit.Redact.Search.NoneSelected"]); return false; }
        return true;
    }
}

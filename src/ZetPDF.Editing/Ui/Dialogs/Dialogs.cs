using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using CorePageRange = ZetPDF.Core.Utilities.PageRange;

namespace ZetPDF.Editing.Ui.Dialogs;

/// <summary>Łącze: adres URL lub strona dokumentu.</summary>
internal sealed class LinkDialog : DialogBase
{
    private readonly RadioButton _uri;
    private readonly RadioButton _page;
    private readonly TextBox _uriBox;
    private readonly TextBox _pageBox;
    private readonly int _pageCount;

    public LinkDialog(ILocalizationService loc, int pageCount, PdfLinkInfo? existing, bool canDelete) : base(loc, canDelete ? "Edit.Link.EditTitle" : "Edit.Link.Title")
    {
        _pageCount = pageCount;
        bool isPage = existing?.Kind == PdfLinkKind.GoToPage;
        _uri = Ui.Radio(loc, "Edit.Link.ToUrl", "target", !isPage);
        _page = Ui.Radio(loc, "Edit.Link.ToPage", "target", isPage);
        _uriBox = Ui.Box(existing?.Uri ?? "https://");
        _pageBox = Ui.Box(((existing?.TargetPageIndex ?? 0) + 1).ToString(CultureInfo.CurrentCulture), 80);
        _pageBox.HorizontalAlignment = HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(_uriBox, loc["Edit.Link.ToUrl"]);
        System.Windows.Automation.AutomationProperties.SetName(_pageBox, loc["Edit.Link.ToPage"]);
        _uriBox.GotFocus += (_, _) => _uri.IsChecked = true;
        _pageBox.GotFocus += (_, _) => _page.IsChecked = true;
        SetBody(Ui.Form((_uri, _uriBox), (_page, _pageBox), (null, Ui.Label(loc, "Edit.Link.PageHint", secondary: true))));
        if (canDelete) Buttons.Children.Add(Ui.Button(loc, "Edit.Link.Delete", (_, _) => { DeleteRequested = true; DialogResult = true; }));
        AddOkCancel();
    }

    public bool DeleteRequested { get; private set; }

    public LinkTarget Target => _uri.IsChecked == true
        ? new LinkTarget(Uri: NormalizeUri(_uriBox.Text))
        : new LinkTarget(PageIndex: int.Parse(_pageBox.Text.Trim(), CultureInfo.CurrentCulture) - 1);

    internal static string NormalizeUri(string text)
    {
        var t = text.Trim();
        if (t.Contains('@') && !t.Contains("://", StringComparison.Ordinal) && !t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return "mailto:" + t;
        if (!t.Contains(':', StringComparison.Ordinal)) return "http://" + t;
        return t;
    }

    protected override bool Validate()
    {
        if (_uri.IsChecked == true)
        {
            var u = NormalizeUri(_uriBox.Text);
            if (u.Length < 8 || !Uri.TryCreate(u, UriKind.Absolute, out _)) { ShowError(L["Edit.Link.InvalidUrl"]); return false; }
            return true;
        }
        if (!int.TryParse(_pageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var p) || p < 1 || p > _pageCount)
        {
            ShowError(L.Format("Edit.Link.InvalidPage", _pageCount));
            return false;
        }
        return true;
    }
}

/// <summary>Wyszukaj i zamień (Ctrl+T).</summary>
internal sealed class FindReplaceDialog : DialogBase
{
    private readonly TextBox _find = Ui.Box();
    private readonly TextBox _replace = Ui.Box();
    private readonly CheckBox _case;
    private readonly CheckBox _word;

    public FindReplaceDialog(ILocalizationService loc, string initial) : base(loc, "Edit.FindReplace.Title")
    {
        _find.Text = initial;
        _case = Ui.Check(loc, "Edit.Search.MatchCase", false);
        _word = Ui.Check(loc, "Edit.Search.WholeWord", false);
        System.Windows.Automation.AutomationProperties.SetName(_find, loc["Edit.FindReplace.Find"]);
        System.Windows.Automation.AutomationProperties.SetName(_replace, loc["Edit.FindReplace.Replace"]);
        SetBody(Ui.Form((Ui.Label(loc, "Edit.FindReplace.Find"), _find), (Ui.Label(loc, "Edit.FindReplace.Replace"), _replace), (null, _case), (null, _word),
            (null, Ui.Label(loc, "Edit.FindReplace.Note", secondary: true))));
        AddOkCancel("Edit.FindReplace.ReplaceAll");
        Loaded += (_, _) => _find.Focus();
    }

    public string Find => _find.Text;
    public string Replace => _replace.Text;
    public TextSearchOptions Options => (_case.IsChecked == true ? TextSearchOptions.MatchCase : 0) | (_word.IsChecked == true ? TextSearchOptions.WholeWord : 0);

    protected override bool Validate()
    {
        if (string.IsNullOrEmpty(_find.Text)) { ShowError(L["Edit.FindReplace.Empty"]); return false; }
        return true;
    }
}

/// <summary>Opcje konwersji obrazów do PDF.</summary>
internal sealed class ImagesToPdfDialog : DialogBase
{
    private readonly ComboBox _size;
    private readonly TextBox _margin;

    public ImagesToPdfDialog(ILocalizationService loc, int count) : base(loc, "Edit.Convert.Images.Title")
    {
        _size = Ui.Combo(loc, new[] { (ImagePageSize.FitImage, "Edit.Convert.Images.FitImage"), (ImagePageSize.A4, "Edit.Convert.Images.A4"), (ImagePageSize.Letter, "Edit.Convert.Images.Letter") }, ImagePageSize.FitImage);
        _margin = Ui.Box("0", 80);
        _margin.HorizontalAlignment = HorizontalAlignment.Left;
        SetBody(Ui.Form((null, Ui.Text(loc.Format("Edit.Convert.Images.Count", count))), (Ui.Label(loc, "Edit.Convert.Images.PageSize"), _size), (Ui.Label(loc, "Edit.Convert.Images.Margin"), _margin)));
        AddOkCancel("Edit.Convert.Create");
    }

    public ImageToPdfOptions Options => new()
    {
        PageSize = Ui.Selected<ImagePageSize>(_size),
        Margin = double.TryParse(_margin.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var m) ? Math.Clamp(m, 0, 200) : 0,
    };

    protected override bool Validate()
    {
        if (!double.TryParse(_margin.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var m) || m < 0 || m > 200) { ShowError(L["Edit.Convert.Images.InvalidMargin"]); return false; }
        return true;
    }
}

/// <summary>Połącz pliki: lista PDF/obrazów/TXT z kolejnością.</summary>
internal sealed class CombineFilesDialog : DialogBase
{
    private readonly ListBox _list = new() { Height = 220, Margin = new Thickness(0, 0, 8, 0), SelectionMode = SelectionMode.Extended };
    private readonly Func<IReadOnlyList<string>?> _pick;

    public CombineFilesDialog(ILocalizationService loc, Func<IReadOnlyList<string>?> pick, IEnumerable<string> initial) : base(loc, "Edit.Combine.Title", 560)
    {
        _pick = pick;
        foreach (var f in initial) Add(f);
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Edit.Combine.Files"]);
        var side = new StackPanel { Width = 120 };
        Button B(string key, RoutedEventHandler h) { var b = Ui.Button(loc, key, h); b.Margin = new Thickness(0, 0, 0, 6); b.HorizontalAlignment = HorizontalAlignment.Stretch; return b; }
        side.Children.Add(B("Edit.Combine.Add", (_, _) => { foreach (var f in _pick() ?? Array.Empty<string>()) Add(f); }));
        side.Children.Add(B("Edit.Combine.Remove", (_, _) => { foreach (var i in _list.SelectedItems.Cast<object>().ToList()) _list.Items.Remove(i); }));
        side.Children.Add(B("Edit.Combine.Up", (_, _) => Move(-1)));
        side.Children.Add(B("Edit.Combine.Down", (_, _) => Move(1)));
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        dock.Children.Add(side);
        dock.Children.Add(_list);
        var root = new StackPanel();
        root.Children.Add(Ui.Label(loc, "Edit.Combine.Hint", secondary: true));
        root.Children.Add(dock);
        SetBody(root);
        AddOkCancel("Edit.Combine.Combine");
    }

    private void Add(string path) => _list.Items.Add(new ListBoxItem { Content = Path.GetFileName(path), Tag = path, ToolTip = path });

    private void Move(int delta)
    {
        if (_list.SelectedItem is not ListBoxItem item) return;
        int i = _list.Items.IndexOf(item), j = i + delta;
        if (j < 0 || j >= _list.Items.Count) return;
        _list.Items.RemoveAt(i);
        _list.Items.Insert(j, item);
        _list.SelectedItem = item;
    }

    public IReadOnlyList<string> Files => _list.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag).ToList();

    protected override bool Validate()
    {
        if (_list.Items.Count == 0) { ShowError(L["Edit.Combine.Empty"]); return false; }
        return true;
    }
}

/// <summary>Zakres stron: wszystkie / bieżąca / zakres "1-3,5".</summary>
internal sealed class PageRangePicker : StackPanel
{
    private readonly RadioButton _all, _current, _range;
    private readonly TextBox _text;
    private readonly int _currentPage, _count;

    public PageRangePicker(ILocalizationService loc, int currentPage, int count, string group)
    {
        _currentPage = currentPage;
        _count = count;
        _all = Ui.Radio(loc, "Edit.Range.All", group, true);
        _current = Ui.Radio(loc, "Edit.Range.Current", group, false);
        _range = Ui.Radio(loc, "Edit.Range.Pages", group, false);
        _text = Ui.Box(string.Empty, 140);
        System.Windows.Automation.AutomationProperties.SetName(_text, loc["Edit.Range.Pages"]);
        _text.GotFocus += (_, _) => _range.IsChecked = true;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_range);
        row.Children.Add(_text);
        Children.Add(_all);
        Children.Add(_current);
        Children.Add(row);
    }

    /// <summary>Tekst zakresu ("" = wszystkie) lub null, gdy niepoprawny.</summary>
    public string? RangeText =>
        _all.IsChecked == true ? string.Empty
        : _current.IsChecked == true ? (_currentPage + 1).ToString(CultureInfo.InvariantCulture)
        : CorePageRange.TryParse(_text.Text, _count, out var pages) && pages.Count > 0 ? CorePageRange.Format(pages) : null;

    public IReadOnlyList<int>? Pages => RangeText is { } t ? (t.Length == 0 ? Enumerable.Range(0, _count).ToList() : CorePageRange.Parse(t, _count)) : null;
}

/// <summary>Eksport do obrazów.</summary>
internal sealed class ExportImagesDialog : DialogBase
{
    private readonly ComboBox _format;
    private readonly ComboBox _dpi;
    private readonly TextBox _quality = Ui.Box("90", 60);
    private readonly CheckBox _multi;
    private readonly PageRangePicker _range;

    public ExportImagesDialog(ILocalizationService loc, int currentPage, int count, int dpi) : base(loc, "Edit.Convert.ToImages.Title")
    {
        _format = Ui.Combo(loc, new[] { (ImageFormat.Png, "Edit.Format.Png"), (ImageFormat.Jpeg, "Edit.Format.Jpeg"), (ImageFormat.Tiff, "Edit.Format.Tiff"), (ImageFormat.Bmp, "Edit.Format.Bmp") }, ImageFormat.Png);
        _dpi = new ComboBox { IsEditable = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Text = dpi.ToString(CultureInfo.CurrentCulture) };
        foreach (var d in new[] { 72, 96, 150, 200, 300, 600 }) _dpi.Items.Add(d.ToString(CultureInfo.CurrentCulture));
        _dpi.Text = dpi.ToString(CultureInfo.CurrentCulture);
        _quality.HorizontalAlignment = HorizontalAlignment.Left;
        _multi = Ui.Check(loc, "Edit.Convert.ToImages.MultiTiff", false);
        _range = new PageRangePicker(loc, currentPage, count, "imgrange");
        System.Windows.Automation.AutomationProperties.SetName(_dpi, loc["Edit.Convert.Dpi"]);
        SetBody(Ui.Form((Ui.Label(loc, "Edit.Convert.Format"), _format), (Ui.Label(loc, "Edit.Convert.Dpi"), _dpi), (Ui.Label(loc, "Edit.Convert.JpegQuality"), _quality),
            (null, _multi), (Ui.Label(loc, "Edit.Range.Title"), _range)));
        AddOkCancel("Edit.Convert.Export");
    }

    public ImageExportOptions Options => new()
    {
        Format = Ui.Selected<ImageFormat>(_format),
        Dpi = int.Parse(_dpi.Text, CultureInfo.CurrentCulture),
        JpegQuality = int.TryParse(_quality.Text, out var q) ? Math.Clamp(q, 1, 100) : 90,
        MultiPageTiff = _multi.IsChecked == true && Ui.Selected<ImageFormat>(_format) == ImageFormat.Tiff,
        PageRange = _range.RangeText ?? string.Empty,
    };

    protected override bool Validate()
    {
        if (!int.TryParse(_dpi.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var d) || d < 36 || d > 1200) { ShowError(L["Edit.Convert.InvalidDpi"]); return false; }
        if (_range.RangeText is null) { ShowError(L["Edit.Range.Invalid"]); return false; }
        return true;
    }
}

/// <summary>Eksport do TXT lub DOCX (zakres + opcje).</summary>
internal sealed class ExportOptionsDialog : DialogBase
{
    private readonly PageRangePicker _range;
    private readonly CheckBox? _layout, _separator, _images, _raster;

    public ExportOptionsDialog(ILocalizationService loc, bool docx, int currentPage, int count) : base(loc, docx ? "Edit.Convert.ToDocx.Title" : "Edit.Convert.ToText.Title")
    {
        _range = new PageRangePicker(loc, currentPage, count, "exprange");
        var rows = new List<(UIElement?, UIElement)>();
        if (docx)
        {
            _images = Ui.Check(loc, "Edit.Convert.ToDocx.Images", true);
            _raster = Ui.Check(loc, "Edit.Convert.ToDocx.Rasterize", true);
            rows.Add((null, _images));
            rows.Add((null, _raster));
            rows.Add((null, Ui.Label(loc, "Edit.Convert.ToDocx.Note", secondary: true)));
        }
        else
        {
            _layout = Ui.Check(loc, "Edit.Convert.ToText.Layout", false);
            _separator = Ui.Check(loc, "Edit.Convert.ToText.Separator", true);
            rows.Add((null, _layout));
            rows.Add((null, _separator));
        }
        rows.Add((Ui.Label(loc, "Edit.Range.Title"), _range));
        SetBody(Ui.Form(rows.ToArray()));
        AddOkCancel("Edit.Convert.Export");
    }

    public TextExportOptions TextOptions => new() { PreserveLayout = _layout?.IsChecked == true, PageSeparator = _separator?.IsChecked == true, PageRange = _range.RangeText ?? string.Empty };
    public DocxExportOptions DocxOptions => new() { IncludeImages = _images?.IsChecked == true, RasterizePagesWithoutText = _raster?.IsChecked == true, PageRange = _range.RangeText ?? string.Empty };

    protected override bool Validate()
    {
        if (_range.RangeText is null) { ShowError(L["Edit.Range.Invalid"]); return false; }
        return true;
    }
}

/// <summary>Eksport do XLSX: zakres stron, arkusz na stronę lub na tabelę, informacja o jakości.</summary>
internal sealed class ExportXlsxDialog : DialogBase
{
    private readonly PageRangePicker _range;
    private readonly ComboBox _mode;

    public ExportXlsxDialog(ILocalizationService loc, int currentPage, int count) : base(loc, "Edit.Convert.ToXlsx.Title", 520)
    {
        _range = new PageRangePicker(loc, currentPage, count, "xlsxrange");
        _mode = Ui.Combo(loc, new[] { (XlsxSheetMode.PerPage, "Edit.Convert.ToXlsx.PerPage"), (XlsxSheetMode.PerTable, "Edit.Convert.ToXlsx.PerTable") }, XlsxSheetMode.PerPage);
        System.Windows.Automation.AutomationProperties.SetName(_mode, loc["Edit.Convert.ToXlsx.Sheets"]);
        SetBody(Ui.Form((Ui.Label(loc, "Edit.Convert.ToXlsx.Sheets"), _mode), (Ui.Label(loc, "Edit.Range.Title"), _range), (null, Ui.Label(loc, "Edit.Convert.ToXlsx.Note", secondary: true))));
        AddOkCancel("Edit.Convert.Export");
    }

    public XlsxExportOptions Options => new() { SheetMode = Ui.Selected<XlsxSheetMode>(_mode), PageRange = _range.RangeText ?? string.Empty };

    protected override bool Validate()
    {
        if (_range.RangeText is null) { ShowError(L["Edit.Range.Invalid"]); return false; }
        return true;
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Engine;

namespace ZetPDF.Pages.Ui;

/// <summary>Podziel dokument: co N stron / wg zakładek najwyższego poziomu / wg rozmiaru pliku; wzorzec nazw, folder.</summary>
internal sealed class SplitDialog : PagesDialog
{
    private readonly RadioButton _everyN, _bookmarks, _size;
    private readonly TextBox _n = Box("1", 60), _mb = Box("10", 60), _pattern = Box("{name}_{n}", 220), _folder = Box(string.Empty, 300);

    public SplitDialog(ILocalizationService loc, IDialogService dialogs, string? folder) : base(loc, dialogs, "Pages.Split.Title")
    {
        _everyN = Radio("Pages.Split.EveryN", "mode", true);
        _bookmarks = Radio("Pages.Split.Bookmarks", "mode");
        _size = Radio("Pages.Split.Size", "mode");
        Body.Children.Add(Section("Pages.Split.Mode",
            Horizontal(_everyN, _n, Label("Pages.Split.Pages")),
            _bookmarks,
            Horizontal(_size, _mb, Label("Pages.Unit.Mb"))));
        _folder.Text = folder ?? string.Empty;
        Body.Children.Add(Row("Pages.Dialog.Folder", FolderPicker(_folder, "Pages.Dialog.SelectFolder")));
        Body.Children.Add(Row("Pages.Dialog.Pattern", _pattern));
        Body.Children.Add(Hint("Pages.Split.PatternHint"));
    }

    public SplitOptions Options { get; private set; } = new();
    public string Folder => _folder.Text.Trim();

    protected override bool Accept()
    {
        if (Folder.Length == 0 || !Directory.Exists(Folder)) return Error("Pages.Error.FolderNotFound");
        var mode = _bookmarks.IsChecked == true ? SplitMode.TopLevelBookmarks : _size.IsChecked == true ? SplitMode.MaxFileSize : SplitMode.EveryNPages;
        if (!TryInt(_n, out var n, 1, 100000) || !TryNumber(_mb, out var mb, 0.05, 100000)) return Error("Pages.Error.InvalidNumber");
        Options = new SplitOptions { Mode = mode, PagesPerFile = n, MaxFileSizeBytes = (long)(mb * 1024 * 1024), FileNamePattern = string.IsNullOrWhiteSpace(_pattern.Text) ? "{name}_{n}" : _pattern.Text.Trim() };
        return true;
    }
}

/// <summary>Scal pliki PDF: lista z kolejnością, zakres stron dla każdego pliku, zakładki.</summary>
internal sealed class MergeDialog : PagesDialog
{
    internal sealed class Entry
    {
        public Entry(string path, string? label = null) { Path = path; Label = label ?? System.IO.Path.GetFileName(path); }
        public string Path { get; }
        public string Label { get; }
        public string Range { get; set; } = string.Empty;
        public byte[]? Data { get; init; }
        public override string ToString() => Range.Length == 0 ? Label : $"{Label}  [{Range}]";
    }

    private readonly ObservableCollection<Entry> _entries = new();
    private readonly ListBox _list = new() { Height = 200, Width = 460 };
    private readonly TextBox _range = Box(string.Empty, 140);
    private readonly CheckBox _perFile, _keep;

    public MergeDialog(ILocalizationService loc, IDialogService dialogs, Entry? current) : base(loc, dialogs, "Pages.Merge.Title")
    {
        if (current is not null) _entries.Add(current);
        _list.ItemsSource = _entries;
        Button B(string key, Action a) { var b = new Button { Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 0, 4), MinWidth = 110 }; Bind(b, ContentControl.ContentProperty, key); b.Click += (_, _) => a(); return b; }
        var buttons = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(B("Pages.Dialog.AddFiles", AddFiles));
        buttons.Children.Add(B("Pages.Dialog.Remove", () => { if (_list.SelectedItem is Entry e) _entries.Remove(e); }));
        buttons.Children.Add(B("Pages.Dialog.Up", () => MoveSel(-1)));
        buttons.Children.Add(B("Pages.Dialog.Down", () => MoveSel(1)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_list);
        row.Children.Add(buttons);
        Body.Children.Add(Section("Pages.Merge.Files", row));
        _list.SelectionChanged += (_, _) => _range.Text = (_list.SelectedItem as Entry)?.Range ?? string.Empty;
        _range.TextChanged += (_, _) =>
        {
            if (_list.SelectedItem is Entry e && e.Range != _range.Text.Trim())
            {
                e.Range = _range.Text.Trim();
                _list.Items.Refresh();
            }
        };
        Body.Children.Add(Row("Pages.Merge.Range", Horizontal(_range, Label("Pages.Dialog.SourceRangeHint"))));
        _perFile = Check("Pages.Merge.BookmarkPerFile", true);
        _keep = Check("Pages.Merge.KeepBookmarks", true);
        Body.Children.Add(_perFile);
        Body.Children.Add(_keep);
    }

    private void AddFiles()
    {
        var files = Dialogs.OpenFiles(T("Pages.Dialog.SelectPdf"), PdfFilters(), multiselect: true);
        if (files is null) return;
        foreach (var f in files) _entries.Add(new Entry(f));
    }

    private void MoveSel(int d)
    {
        if (_list.SelectedItem is not Entry e) return;
        int i = _entries.IndexOf(e), j = i + d;
        if (j < 0 || j >= _entries.Count) return;
        _entries.Move(i, j);
        _list.SelectedItem = e;
    }

    public IReadOnlyList<Entry> Entries => _entries.ToArray();
    public MergeOptions Options { get; private set; } = new();

    protected override bool Accept()
    {
        if (_entries.Count < 2) return Error("Pages.Error.MergeTwoFiles");
        foreach (var e in _entries)
            if (e.Range.Length > 0 && !e.Range.Split(',', '-', ';').All(p => p.Trim().Length == 0 || int.TryParse(p.Trim(), out _)))
                return Error("Pages.Error.InvalidRangeFor", e.Label);
        Options = new MergeOptions { BookmarkPerFile = _perFile.IsChecked == true, KeepBookmarks = _keep.IsChecked == true };
        return true;
    }
}

/// <summary>Format numerów stron (etykiety /PageLabels): sekcje od strony N ze stylem, prefiksem i numerem startowym.</summary>
internal sealed class PageLabelsDialog : PagesDialog
{
    private readonly ObservableCollection<PageLabelRange> _ranges;
    private readonly ListBox _list = new() { Height = 130, Width = 360 };
    private readonly TextBox _from = Box("1", 60), _prefix = Box(string.Empty, 100), _start = Box("1", 60);
    private readonly ComboBox _style;
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 6, 0, 0) };
    private readonly int _count;

    public PageLabelsDialog(ILocalizationService loc, IDialogService dialogs, int pageCount, IReadOnlyList<PageLabelRange> current) : base(loc, dialogs, "Pages.Labels.Title")
    {
        _count = pageCount;
        _ranges = new ObservableCollection<PageLabelRange>(current);
        _style = Combo(new[]
        {
            (PageLabelStyle.Decimal, "Pages.Labels.Decimal"), (PageLabelStyle.LowerRoman, "Pages.Labels.LowerRoman"), (PageLabelStyle.UpperRoman, "Pages.Labels.UpperRoman"),
            (PageLabelStyle.LowerLetters, "Pages.Labels.LowerLetters"), (PageLabelStyle.UpperLetters, "Pages.Labels.UpperLetters"), (PageLabelStyle.None, "Pages.Labels.None"),
        }, PageLabelStyle.Decimal, 200);
        _list.DisplayMemberPath = null;
        Refresh();
        Body.Children.Add(Hint("Pages.Labels.Hint"));
        Body.Children.Add(Section("Pages.Labels.Sections", _list));
        var edit = new StackPanel();
        edit.Children.Add(Row("Pages.Labels.FromPage", _from, 140));
        edit.Children.Add(Row("Pages.Labels.Style", _style, 140));
        edit.Children.Add(Row("Pages.Labels.Prefix", _prefix, 140));
        edit.Children.Add(Row("Pages.Labels.Start", _start, 140));
        Button B(string key, Action a) { var b = new Button { Padding = new Thickness(10, 2, 10, 2) }; Bind(b, ContentControl.ContentProperty, key); b.Click += (_, _) => a(); return b; }
        edit.Children.Add(Horizontal(B("Pages.Labels.AddOrUpdate", AddOrUpdate), B("Pages.Dialog.Remove", RemoveSelected), B("Pages.Labels.Clear", () => { _ranges.Clear(); Refresh(); })));
        Body.Children.Add(Section("Pages.Labels.Edit", edit));
        Body.Children.Add(_preview);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _ranges.Count) return;
            var r = _ranges[_list.SelectedIndex];
            _from.Text = (r.StartPageIndex + 1).ToString(CultureInfo.InvariantCulture);
            _prefix.Text = r.Prefix;
            _start.Text = r.StartNumber.ToString(CultureInfo.InvariantCulture);
            foreach (ComboBoxItem it in _style.Items) if (it.Tag is PageLabelStyle s && s == r.Style) _style.SelectedItem = it;
        };
    }

    public IReadOnlyList<PageLabelRange> Ranges => _ranges.ToArray();

    private void AddOrUpdate()
    {
        if (!TryInt(_from, out var from, 1, _count) || !TryInt(_start, out var start, 1, 1_000_000)) { Error("Pages.Error.InvalidPageOf", _count); return; }
        var r = new PageLabelRange(from - 1, ComboValue<PageLabelStyle>(_style), _prefix.Text, start);
        var existing = _ranges.FirstOrDefault(x => x.StartPageIndex == r.StartPageIndex);
        if (existing is not null) _ranges.Remove(existing);
        _ranges.Add(r);
        Refresh();
    }

    private void RemoveSelected()
    {
        if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _ranges.Count) { _ranges.RemoveAt(_list.SelectedIndex); Refresh(); }
    }

    private void Refresh()
    {
        var sorted = _ranges.OrderBy(r => r.StartPageIndex).ToList();
        _ranges.Clear();
        foreach (var r in sorted) _ranges.Add(r);
        _list.Items.Clear();
        foreach (var r in _ranges)
            _list.Items.Add(Loc.Format("Pages.Labels.Item", r.StartPageIndex + 1, Loc["Pages.Labels." + r.Style switch
            {
                PageLabelStyle.Decimal => "Decimal", PageLabelStyle.LowerRoman => "LowerRoman", PageLabelStyle.UpperRoman => "UpperRoman",
                PageLabelStyle.LowerLetters => "LowerLetters", PageLabelStyle.UpperLetters => "UpperLetters", _ => "None",
            }], r.Prefix, r.StartNumber));
        var labels = Enumerable.Range(0, Math.Min(_count, 12)).Select(i => PageLabels.Label(_ranges, i));
        _preview.Text = Loc.Format("Pages.Labels.Preview", string.Join(", ", labels) + (_count > 12 ? ", …" : string.Empty));
    }

    protected override bool Accept() => true;
}

/// <summary>Optymalizacja zaawansowana (oraz szybkie Zmniejsz rozmiar z ustawieniami domyślnymi).</summary>
internal sealed class OptimizeDialog : PagesDialog
{
    private readonly CheckBox _images, _unused, _streams, _thumbs, _meta;
    private readonly TextBox _quality, _dpi;

    public OptimizeDialog(ILocalizationService loc, IDialogService dialogs, OptimizeOptions initial) : base(loc, dialogs, "Pages.Optimize.Title")
    {
        _images = Check("Pages.Optimize.Images", initial.RecompressImages);
        _quality = Box(initial.JpegQuality.ToString(CultureInfo.InvariantCulture), 60);
        _dpi = Box(initial.MaxImageDpi.ToString(CultureInfo.InvariantCulture), 60);
        Body.Children.Add(Section("Pages.Optimize.ImagesHeader", _images,
            Row("Pages.Optimize.Quality", Horizontal(_quality, Label("Pages.Optimize.QualityHint"))),
            Row("Pages.Optimize.Dpi", Horizontal(_dpi, Label("Pages.Optimize.DpiHint")))));
        _unused = Check("Pages.Optimize.Unused", initial.RemoveUnusedObjects);
        _streams = Check("Pages.Optimize.Streams", initial.CompressStreams);
        _thumbs = Check("Pages.Optimize.Thumbnails", initial.RemoveEmbeddedThumbnails);
        _meta = Check("Pages.Optimize.Metadata", initial.RemoveMetadata);
        Body.Children.Add(Section("Pages.Optimize.Cleanup", _unused, _streams, _thumbs, _meta));
    }

    public OptimizeOptions Options { get; private set; } = new();

    protected override bool Accept()
    {
        if (!TryInt(_quality, out var q, 1, 100) || !TryInt(_dpi, out var dpi, 0, 2400)) return Error("Pages.Error.InvalidNumber");
        Options = new OptimizeOptions
        {
            RecompressImages = _images.IsChecked == true, JpegQuality = q, MaxImageDpi = dpi, RemoveUnusedObjects = _unused.IsChecked == true,
            CompressStreams = _streams.IsChecked == true, RemoveEmbeddedThumbnails = _thumbs.IsChecked == true, RemoveMetadata = _meta.IsChecked == true,
        };
        return true;
    }
}

/// <summary>Skala szarości: konwersja kolorów i obrazów albo rasteryzacja (z ostrzeżeniem).</summary>
internal sealed class GrayscaleDialog : PagesDialog
{
    private readonly RadioButton _vector, _raster;

    public GrayscaleDialog(ILocalizationService loc, IDialogService dialogs) : base(loc, dialogs, "Pages.Gray.Title")
    {
        _vector = Radio("Pages.Gray.Vector", "mode", true);
        _raster = Radio("Pages.Gray.Raster", "mode");
        Body.Children.Add(_vector);
        Body.Children.Add(Hint("Pages.Gray.VectorHint"));
        Body.Children.Add(_raster);
        var warn = Hint("Pages.Gray.RasterWarning");
        warn.SetResourceReference(StyleProperty, "Shell.Text.Error");
        Body.Children.Add(warn);
    }

    public bool Rasterize => _raster.IsChecked == true;

    protected override bool Accept() => true;
}

/// <summary>Oczyszczanie dokumentu: elementy do usunięcia.</summary>
internal sealed class SanitizeDialog : PagesDialog
{
    private readonly CheckBox _meta, _comments, _attach, _js, _layers, _form, _links, _thumbs;

    public SanitizeDialog(ILocalizationService loc, IDialogService dialogs) : base(loc, dialogs, "Pages.Sanitize.Title")
    {
        var d = new SanitizeOptions();
        Body.Children.Add(Hint("Pages.Sanitize.Hint"));
        Body.Children.Add(_meta = Check("Pages.Sanitize.Metadata", d.Metadata));
        Body.Children.Add(_comments = Check("Pages.Sanitize.Comments", d.Comments));
        Body.Children.Add(_attach = Check("Pages.Sanitize.Attachments", d.Attachments));
        Body.Children.Add(_js = Check("Pages.Sanitize.JavaScript", d.JavaScript));
        Body.Children.Add(_layers = Check("Pages.Sanitize.Layers", d.HiddenLayers));
        Body.Children.Add(_form = Check("Pages.Sanitize.FormData", d.FormData));
        Body.Children.Add(_links = Check("Pages.Sanitize.Links", d.ExternalLinks));
        Body.Children.Add(_thumbs = Check("Pages.Sanitize.Thumbnails", d.Thumbnails));
    }

    protected override string OkKey => "Pages.Sanitize.Run";

    public SanitizeOptions Options { get; private set; } = new();

    protected override bool Accept()
    {
        var boxes = new[] { _meta, _comments, _attach, _js, _layers, _form, _links, _thumbs };
        if (boxes.All(b => b.IsChecked != true)) return Error("Pages.Error.NothingSelected");
        Options = new SanitizeOptions
        {
            Metadata = _meta.IsChecked == true, Comments = _comments.IsChecked == true, Attachments = _attach.IsChecked == true, JavaScript = _js.IsChecked == true,
            HiddenLayers = _layers.IsChecked == true, FormData = _form.IsChecked == true, ExternalLinks = _links.IsChecked == true, Thumbnails = _thumbs.IsChecked == true,
        };
        return true;
    }

}

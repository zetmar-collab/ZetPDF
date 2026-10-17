using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Ui;

/// <summary>Znak wodny (Dodaj/Aktualizuj): tekst lub obraz, czcionka, krycie, obrót, pozycja, zakres, przed/za treścią.</summary>
internal sealed class WatermarkDialog : PagesDialog
{
    private readonly RadioButton _isText, _isImage;
    private readonly TextBox _text = new() { Width = 320, Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _image = Box(string.Empty, 260);
    private readonly TextBox _scale, _opacity, _rotation, _dx, _dy;
    private readonly StyleEditor _style;
    private readonly ComboBox _h, _v;
    private readonly CheckBox _behind;
    private readonly RangePicker _range;

    public WatermarkDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx, WatermarkOptions? initial, bool update)
        : base(loc, dialogs, update ? "Pages.Watermark.UpdateTitle" : "Pages.Watermark.Title")
    {
        var o = initial ?? new WatermarkOptions { Text = loc["Pages.Watermark.DefaultText"] };
        _isText = Radio("Pages.Watermark.Text", "src", o.ImagePath is null || o.Text is not null);
        _isImage = Radio("Pages.Watermark.Image", "src", o.Text is null && o.ImagePath is not null);
        _text.Text = o.Text ?? string.Empty;
        _image.Text = o.ImagePath ?? string.Empty;
        Body.Children.Add(Section("Pages.Watermark.Source", _isText, _text, _isImage,
            Row("Pages.Watermark.ImageFile", FilePicker(_image, "Pages.Dialog.SelectImage", ImageFilters)),
            Row("Pages.Watermark.ImageScale", Horizontal(_scale = Box(Fmt(o.ImageScale * 100), 60), Label("Pages.Unit.PercentOfPage")))));
        _style = new StyleEditor(this, o.Style);
        Body.Children.Add(_style.Element);
        _h = HorizontalCombo(o.Horizontal);
        _v = VerticalCombo(o.Vertical);
        var look = new StackPanel();
        look.Children.Add(Row("Pages.Watermark.Opacity", Horizontal(_opacity = Box(Fmt(o.Opacity * 100), 60), Label("Pages.Unit.Percent"))));
        look.Children.Add(Row("Pages.Watermark.Rotation", Horizontal(_rotation = Box(Fmt(o.RotationDegrees), 60), Label("Pages.Unit.Degrees"))));
        look.Children.Add(Row("Pages.Watermark.Position", Horizontal(_h, _v)));
        look.Children.Add(Row("Pages.Watermark.Offset", Horizontal(_dx = Box(Fmt(o.OffsetX / MmToPt), 60), _dy = Box(Fmt(o.OffsetY / MmToPt), 60), Label("Pages.Unit.MmXY"))));
        _behind = Check("Pages.Watermark.Behind", o.BehindContent);
        look.Children.Add(_behind);
        Body.Children.Add(Section("Pages.Watermark.Appearance", look));
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: false);
        _range.SetText(o.PageRange);
        Body.Children.Add(_range.Element);
    }

    public WatermarkOptions Options { get; private set; } = new();

    protected override bool Accept()
    {
        bool text = _isText.IsChecked == true;
        if (text && string.IsNullOrWhiteSpace(_text.Text)) return Error("Pages.Error.WatermarkEmpty");
        if (!text && !File.Exists(_image.Text.Trim())) return Error("Pages.Error.ImageNotFound");
        if (!_style.TryGet(out var style)) return Error("Pages.Error.InvalidStyle");
        if (!TryNumber(_opacity, out var op, 1, 100) || !TryNumber(_rotation, out var rot, -360, 360) || !TryNumber(_scale, out var sc, 0, 100)
            || !TryNumber(_dx, out var dx, -2000, 2000) || !TryNumber(_dy, out var dy, -2000, 2000)) return Error("Pages.Error.InvalidNumber");
        if (!_range.TryGetText(out var range)) return Error("Pages.Error.InvalidRange");
        Options = new WatermarkOptions
        {
            Text = text ? _text.Text : null,
            ImagePath = text ? null : _image.Text.Trim(),
            Style = style,
            ImageScale = sc / 100,
            Opacity = op / 100,
            RotationDegrees = rot,
            Horizontal = ComboValue<HorizontalPosition>(_h),
            Vertical = ComboValue<VerticalPosition>(_v),
            OffsetX = dx * MmToPt,
            OffsetY = dy * MmToPt,
            PageRange = range,
            BehindContent = _behind.IsChecked == true,
        };
        return true;
    }
}

/// <summary>Tło: kolor i/lub obraz, krycie, zakres.</summary>
internal sealed class BackgroundDialog : PagesDialog
{
    private readonly CheckBox _useColor, _useImage;
    private readonly ColorField _color;
    private readonly TextBox _image = Box(string.Empty, 260);
    private readonly TextBox _opacity;
    private readonly RangePicker _range;

    public BackgroundDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx, BackgroundOptions? initial, bool update)
        : base(loc, dialogs, update ? "Pages.Background.UpdateTitle" : "Pages.Background.Title")
    {
        var o = initial ?? new BackgroundOptions { Color = new PdfColor(255, 255, 250, 225) };
        _useColor = Check("Pages.Background.Color", o.Color is not null);
        _color = new ColorField(o.Color ?? PdfColor.White);
        _useImage = Check("Pages.Background.Image", o.ImagePath is not null);
        _image.Text = o.ImagePath ?? string.Empty;
        Body.Children.Add(Horizontal(_useColor, _color.Element));
        Body.Children.Add(_useImage);
        Body.Children.Add(Row("Pages.Watermark.ImageFile", FilePicker(_image, "Pages.Dialog.SelectImage", ImageFilters)));
        Body.Children.Add(Row("Pages.Watermark.Opacity", Horizontal(_opacity = Box(Fmt(o.Opacity * 100), 60), Label("Pages.Unit.Percent"))));
        Body.Children.Add(Hint("Pages.Background.Hint"));
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: false);
        _range.SetText(o.PageRange);
        Body.Children.Add(_range.Element);
    }

    public BackgroundOptions Options { get; private set; } = new();

    protected override bool Accept()
    {
        bool color = _useColor.IsChecked == true, image = _useImage.IsChecked == true;
        if (!color && !image) return Error("Pages.Error.BackgroundEmpty");
        PdfColor c = PdfColor.White;
        if (color && !_color.TryGet(out c)) return Error("Pages.Error.InvalidColor");
        if (image && !File.Exists(_image.Text.Trim())) return Error("Pages.Error.ImageNotFound");
        if (!TryNumber(_opacity, out var op, 1, 100)) return Error("Pages.Error.InvalidNumber");
        if (!_range.TryGetText(out var range)) return Error("Pages.Error.InvalidRange");
        Options = new BackgroundOptions { Color = color ? c : null, ImagePath = image ? _image.Text.Trim() : null, Opacity = op / 100, PageRange = range };
        return true;
    }
}

/// <summary>Nagłówek i stopka: 6 pól (lewo/środek/prawo × góra/dół), makra, marginesy, numer początkowy, zakres.</summary>
internal sealed class HeaderFooterDialog : PagesDialog
{
    private readonly TextBox[] _fields = Enumerable.Range(0, 6).Select(_ => Box(string.Empty, 170)).ToArray();
    private readonly StyleEditor _style;
    private readonly TextBox _ml, _mr, _mt, _mb, _date, _start;
    private readonly RangePicker _range;

    public HeaderFooterDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx, HeaderFooterOptions? initial, bool update)
        : base(loc, dialogs, update ? "Pages.HeaderFooter.UpdateTitle" : "Pages.HeaderFooter.Title")
    {
        var o = initial ?? new HeaderFooterOptions { FooterCenter = loc["Pages.PageNumbers.DefaultFormat"] };
        var values = new[] { o.HeaderLeft, o.HeaderCenter, o.HeaderRight, o.FooterLeft, o.FooterCenter, o.FooterRight };
        for (int i = 0; i < 6; i++) _fields[i].Text = values[i];
        var grid = new Grid();
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < 3; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); if (e is FrameworkElement f) f.Margin = new Thickness(0, 2, 6, 2); grid.Children.Add(e); }
        Put(Label("Pages.Pos.Left"), 0, 1); Put(Label("Pages.Pos.Center"), 0, 2); Put(Label("Pages.Pos.Right"), 0, 3);
        Put(Label("Pages.HeaderFooter.Header"), 1, 0); Put(Label("Pages.HeaderFooter.Footer"), 2, 0);
        for (int i = 0; i < 6; i++) Put(_fields[i], 1 + i / 3, 1 + i % 3);
        Body.Children.Add(Section("Pages.HeaderFooter.Texts", grid, Hint("Pages.HeaderFooter.MacroHint")));
        _style = new StyleEditor(this, o.Style);
        Body.Children.Add(_style.Element);
        var m = new StackPanel();
        m.Children.Add(Row("Pages.HeaderFooter.Margins", Horizontal(_ml = Box(Fmt(o.MarginLeft / MmToPt), 50), _mt = Box(Fmt(o.MarginTop / MmToPt), 50), _mr = Box(Fmt(o.MarginRight / MmToPt), 50), _mb = Box(Fmt(o.MarginBottom / MmToPt), 50), Label("Pages.Unit.MmLtrb"))));
        m.Children.Add(Row("Pages.HeaderFooter.DateFormat", _date = Box(o.DateFormat, 120)));
        m.Children.Add(Row("Pages.HeaderFooter.StartNumber", _start = Box(o.StartPageNumber.ToString(CultureInfo.InvariantCulture), 60)));
        Body.Children.Add(Section("Pages.HeaderFooter.Layout", m));
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: false);
        _range.SetText(o.PageRange);
        Body.Children.Add(_range.Element);
    }

    public HeaderFooterOptions Options { get; private set; } = new();

    protected override bool Accept()
    {
        if (_fields.All(f => string.IsNullOrWhiteSpace(f.Text))) return Error("Pages.Error.HeaderFooterEmpty");
        if (!_style.TryGet(out var style)) return Error("Pages.Error.InvalidStyle");
        if (!TryNumber(_ml, out var ml, 0, 500) || !TryNumber(_mt, out var mt, 0, 500) || !TryNumber(_mr, out var mr, 0, 500) || !TryNumber(_mb, out var mb, 0, 500)
            || !TryInt(_start, out var start, 0, 1_000_000)) return Error("Pages.Error.InvalidNumber");
        if (!_range.TryGetText(out var range)) return Error("Pages.Error.InvalidRange");
        Options = new HeaderFooterOptions
        {
            HeaderLeft = _fields[0].Text, HeaderCenter = _fields[1].Text, HeaderRight = _fields[2].Text,
            FooterLeft = _fields[3].Text, FooterCenter = _fields[4].Text, FooterRight = _fields[5].Text,
            Style = style, MarginLeft = ml * MmToPt, MarginTop = mt * MmToPt, MarginRight = mr * MmToPt, MarginBottom = mb * MmToPt,
            DateFormat = string.IsNullOrWhiteSpace(_date.Text) ? "d" : _date.Text.Trim(), StartPageNumber = start, PageRange = range,
        };
        return true;
    }
}

/// <summary>Numeracja stron: format („Strona X z Y”), pozycja, numer początkowy, zakres; usuwanie istniejącej.</summary>
internal sealed class PageNumbersDialog : PagesDialog
{
    private readonly ComboBox _format = new() { Width = 260, IsEditable = true };
    private readonly ComboBox _h, _v;
    private readonly StyleEditor _style;
    private readonly TextBox _margin, _start;
    private readonly CheckBox _replace;
    private readonly RangePicker _range;

    public PageNumbersDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx, PageNumberOptions? initial, bool hasExisting)
        : base(loc, dialogs, "Pages.PageNumbers.Title")
    {
        var o = initial ?? new PageNumberOptions { Format = loc["Pages.PageNumbers.DefaultFormat"] };
        foreach (var f in new[] { loc["Pages.PageNumbers.DefaultFormat"], "<<strona>>", "<<strona>> / <<stron>>", "- <<strona>> -", "Page <<page>> of <<pages>>" }.Distinct()) _format.Items.Add(f);
        _format.Text = o.Format;
        Body.Children.Add(Row("Pages.PageNumbers.Format", _format));
        Body.Children.Add(Hint("Pages.HeaderFooter.MacroHint"));
        _h = HorizontalCombo(o.Horizontal);
        _v = VerticalCombo(o.Vertical);
        Body.Children.Add(Row("Pages.Watermark.Position", Horizontal(_h, _v)));
        Body.Children.Add(Row("Pages.PageNumbers.Margin", Horizontal(_margin = Box(Fmt(o.Margin / MmToPt), 60), Label("Pages.Unit.Mm"))));
        Body.Children.Add(Row("Pages.HeaderFooter.StartNumber", _start = Box(o.StartNumber.ToString(CultureInfo.InvariantCulture), 60)));
        _style = new StyleEditor(this, o.Style);
        Body.Children.Add(_style.Element);
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: false);
        _range.SetText(o.PageRange);
        Body.Children.Add(_range.Element);
        _replace = Check("Pages.PageNumbers.Replace", true);
        _replace.IsEnabled = hasExisting;
        Body.Children.Add(_replace);
        if (hasExisting)
        {
            var remove = new Button { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            Bind(remove, ContentControl.ContentProperty, "Pages.PageNumbers.RemoveExisting");
            remove.Click += (_, _) => { RemoveRequested = true; DialogResult = true; };
            Buttons.Children.Insert(0, remove);
        }
    }

    public PageNumberOptions Options { get; private set; } = new();
    public bool ReplaceExisting => _replace.IsChecked == true && _replace.IsEnabled;
    public bool RemoveRequested { get; private set; }

    protected override bool Accept()
    {
        if (string.IsNullOrWhiteSpace(_format.Text)) return Error("Pages.Error.FormatEmpty");
        if (!_style.TryGet(out var style)) return Error("Pages.Error.InvalidStyle");
        if (!TryNumber(_margin, out var m, 0, 500) || !TryInt(_start, out var start, 0, 1_000_000)) return Error("Pages.Error.InvalidNumber");
        if (!_range.TryGetText(out var range)) return Error("Pages.Error.InvalidRange");
        Options = new PageNumberOptions { Format = _format.Text, Horizontal = ComboValue<HorizontalPosition>(_h), Vertical = ComboValue<VerticalPosition>(_v), Style = style, Margin = m * MmToPt, StartNumber = start, PageRange = range };
        return true;
    }
}

/// <summary>Numeracja Bates: prefiks, sufiks, liczba cyfr, numer startowy, pozycja; opcjonalnie kolejne pliki.</summary>
internal sealed class BatesDialog : PagesDialog
{
    private readonly TextBox _prefix, _suffix, _digits, _start, _margin;
    private readonly ComboBox _h, _v;
    private readonly StyleEditor _style;
    private readonly ListBox _files = new() { Height = 90, Width = 420 };
    private readonly TextBox _folder = Box(string.Empty, 300);

    public BatesDialog(ILocalizationService loc, IDialogService dialogs, BatesOptions? initial) : base(loc, dialogs, "Pages.Bates.Title")
    {
        var o = initial ?? new BatesOptions();
        Body.Children.Add(Row("Pages.Bates.Prefix", _prefix = Box(o.Prefix, 160)));
        Body.Children.Add(Row("Pages.Bates.Suffix", _suffix = Box(o.Suffix, 160)));
        Body.Children.Add(Row("Pages.Bates.Digits", _digits = Box(o.Digits.ToString(CultureInfo.InvariantCulture), 60)));
        Body.Children.Add(Row("Pages.Bates.Start", _start = Box("1", 100)));
        _h = HorizontalCombo(o.Horizontal);
        _v = VerticalCombo(o.Vertical);
        Body.Children.Add(Row("Pages.Watermark.Position", Horizontal(_h, _v)));
        Body.Children.Add(Row("Pages.PageNumbers.Margin", Horizontal(_margin = Box(Fmt(o.Margin / MmToPt), 60), Label("Pages.Unit.Mm"))));
        _style = new StyleEditor(this, o.Style);
        Body.Children.Add(_style.Element);
        var add = new Button { Padding = new Thickness(10, 2, 10, 2) };
        Bind(add, ContentControl.ContentProperty, "Pages.Dialog.AddFiles");
        add.Click += (_, _) =>
        {
            var files = Dialogs.OpenFiles(T("Pages.Dialog.SelectPdf"), PdfFilters(), multiselect: true);
            if (files is null) return;
            foreach (var f in files) if (!_files.Items.Contains(f)) _files.Items.Add(f);
        };
        var remove = new Button { Padding = new Thickness(10, 2, 10, 2) };
        Bind(remove, ContentControl.ContentProperty, "Pages.Dialog.Remove");
        remove.Click += (_, _) => { if (_files.SelectedItem is { } s) _files.Items.Remove(s); };
        Body.Children.Add(Section("Pages.Bates.MoreFiles", Hint("Pages.Bates.MoreFilesHint"), _files, Horizontal(add, remove),
            Row("Pages.Bates.OutputFolder", FolderPicker(_folder, "Pages.Dialog.SelectFolder"))));
    }

    public BatesOptions Options { get; private set; } = new();
    public long StartNumber { get; private set; }
    public IReadOnlyList<string> MoreFiles => _files.Items.Cast<string>().ToArray();
    public string OutputFolder => _folder.Text.Trim();

    protected override bool Accept()
    {
        if (!TryInt(_digits, out var digits, 1, 18) || !long.TryParse(_start.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) || start < 0
            || !TryNumber(_margin, out var m, 0, 500)) return Error("Pages.Error.InvalidNumber");
        if (!_style.TryGet(out var style)) return Error("Pages.Error.InvalidStyle");
        if (MoreFiles.Count > 0 && (OutputFolder.Length == 0 || !Directory.Exists(OutputFolder))) return Error("Pages.Error.FolderNotFound");
        Options = new BatesOptions { Prefix = _prefix.Text, Suffix = _suffix.Text, Digits = digits, Horizontal = ComboValue<HorizontalPosition>(_h), Vertical = ComboValue<VerticalPosition>(_v), Style = style, Margin = m * MmToPt };
        StartNumber = start;
        return true;
    }
}

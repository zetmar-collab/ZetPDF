using System.Globalization;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Ui;

/// <summary>Wstaw z pliku (Ctrl+Shift+I): plik, zakres stron źródła, miejsce wstawienia.</summary>
internal sealed class InsertFromFileDialog : PagesDialog
{
    private readonly TextBox _file = Box(string.Empty, 300);
    private readonly TextBox _range = Box(string.Empty, 120);
    private readonly PositionPicker _pos;

    public InsertFromFileDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx, string? initialFile = null) : base(loc, dialogs, "Pages.Insert.Title")
    {
        _file.Text = initialFile ?? string.Empty;
        Body.Children.Add(Row("Pages.Dialog.SourceFile", FilePicker(_file, "Pages.Dialog.SelectPdf", PdfFilters)));
        Body.Children.Add(Row("Pages.Dialog.SourceRange", Horizontal(_range, Label("Pages.Dialog.SourceRangeHint"))));
        _pos = new PositionPicker(this, ctx);
        Body.Children.Add(_pos.Element);
    }

    public string FilePath => _file.Text.Trim();
    public string SourceRange => _range.Text.Trim();
    public int InsertAt { get; private set; }

    protected override bool Accept()
    {
        if (!File.Exists(FilePath)) return Error("Pages.Error.FileNotFound");
        if (!_pos.TryGet(out var at)) return Error("Pages.Error.InvalidPage");
        InsertAt = at;
        return true;
    }
}

/// <summary>Pusta strona (Ctrl+Shift+T) i Pusty dokument: rozmiar, orientacja, liczba, miejsce.</summary>
internal sealed class InsertBlankDialog : PagesDialog
{
    private readonly SizePicker _size;
    private readonly TextBox _count = Box("1", 60);
    private readonly PositionPicker? _pos;

    public InsertBlankDialog(ILocalizationService loc, IDialogService dialogs, DocContext? ctx) : base(loc, dialogs, ctx is null ? "Pages.Blank.NewTitle" : "Pages.Blank.Title")
    {
        _size = new SizePicker(this, ctx?.CurrentPageSize ?? PdfSize.A4, allowCurrent: ctx is not null);
        Body.Children.Add(_size.Element);
        Body.Children.Add(Row("Pages.Blank.Count", _count));
        if (ctx is not null)
        {
            _pos = new PositionPicker(this, ctx);
            Body.Children.Add(_pos.Element);
        }
    }

    public PdfSize Size { get; private set; }
    public int Count { get; private set; }
    public int InsertAt { get; private set; }

    protected override bool Accept()
    {
        if (!_size.TryGet(out var s)) return Error("Pages.Error.InvalidSize");
        if (!TryInt(_count, out var n, 1, 1000)) return Error("Pages.Error.InvalidCount", 1, 1000);
        int at = 0;
        if (_pos is not null && !_pos.TryGet(out at)) return Error("Pages.Error.InvalidPage");
        (Size, Count, InsertAt) = (s, n, at);
        return true;
    }
}

/// <summary>Okno wyboru stron (Usuń, Odwróć kolejność, Duplikuj).</summary>
internal sealed class RangeDialog : PagesDialog
{
    private readonly RangePicker _range;
    private readonly string? _okKey;

    public RangeDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx, string titleKey, string? hintKey = null, bool preferSelection = true, string? okKey = null)
        : base(loc, dialogs, titleKey)
    {
        _okKey = okKey;
        if (hintKey is not null) Body.Children.Add(Hint(hintKey));
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection);
        Body.Children.Add(_range.Element);
    }

    protected override string OkKey => _okKey ?? base.OkKey;

    public IReadOnlyList<int> Pages { get; private set; } = Array.Empty<int>();
    public bool IsAll => _range.IsAll;

    protected override bool Accept()
    {
        if (!_range.TryGet(out var p)) return Error("Pages.Error.InvalidRange");
        Pages = p;
        return true;
    }
}

/// <summary>Wyodrębnij: zakres, do jednego nowego dokumentu albo osobnych plików, opcjonalnie usuń z oryginału.</summary>
internal sealed class ExtractDialog : PagesDialog
{
    private readonly RangePicker _range;
    private readonly RadioButton _single, _separate;
    private readonly TextBox _pattern = Box("{name}_{page}", 200);
    private readonly TextBox _folder = Box(string.Empty, 260);
    private readonly CheckBox _delete;

    public ExtractDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx, string? defaultFolder) : base(loc, dialogs, "Pages.Extract.Title")
    {
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: true);
        Body.Children.Add(_range.Element);
        _single = Radio("Pages.Extract.Single", "mode", true);
        _separate = Radio("Pages.Extract.Separate", "mode");
        _folder.Text = defaultFolder ?? string.Empty;
        Body.Children.Add(Section("Pages.Extract.Mode", _single, _separate,
            Row("Pages.Dialog.Folder", FolderPicker(_folder, "Pages.Dialog.SelectFolder")),
            Row("Pages.Dialog.Pattern", _pattern), Hint("Pages.Dialog.PatternHint")));
        _delete = Check("Pages.Extract.DeleteAfter");
        Body.Children.Add(_delete);
        void Sync() => _folder.IsEnabled = _pattern.IsEnabled = _separate.IsChecked == true;
        _single.Checked += (_, _) => Sync();
        _separate.Checked += (_, _) => Sync();
        Sync();
    }

    public IReadOnlyList<int> Pages { get; private set; } = Array.Empty<int>();
    public bool SeparateFiles => _separate.IsChecked == true;
    public string Folder => _folder.Text.Trim();
    public string Pattern => _pattern.Text.Trim();
    public bool DeleteAfter => _delete.IsChecked == true;

    protected override bool Accept()
    {
        if (!_range.TryGet(out var p)) return Error("Pages.Error.InvalidRange");
        if (SeparateFiles && (Folder.Length == 0 || !Directory.Exists(Folder))) return Error("Pages.Error.FolderNotFound");
        Pages = p;
        return true;
    }
}

/// <summary>Zastąp: strony bieżącego dokumentu stronami z pliku (od strony N źródła).</summary>
internal sealed class ReplaceDialog : PagesDialog
{
    private readonly RangePicker _range;
    private readonly TextBox _file = Box(string.Empty, 300);
    private readonly TextBox _start = Box("1", 60);

    public ReplaceDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx) : base(loc, dialogs, "Pages.Replace.Title")
    {
        _range = new RangePicker(this, ctx, allowSubset: false, preferSelection: true);
        Body.Children.Add(_range.Element);
        Body.Children.Add(Row("Pages.Dialog.SourceFile", FilePicker(_file, "Pages.Dialog.SelectPdf", PdfFilters)));
        Body.Children.Add(Row("Pages.Replace.SourceStart", _start));
        Body.Children.Add(Hint("Pages.Replace.Hint"));
    }

    public IReadOnlyList<int> Targets { get; private set; } = Array.Empty<int>();
    public string FilePath => _file.Text.Trim();
    public int SourceStart { get; private set; }

    protected override bool Accept()
    {
        if (!_range.TryGet(out var p)) return Error("Pages.Error.InvalidRange");
        if (!File.Exists(FilePath)) return Error("Pages.Error.FileNotFound");
        if (!TryInt(_start, out var s, 1, 100000)) return Error("Pages.Error.InvalidPage");
        (Targets, SourceStart) = (p, s - 1);
        return true;
    }
}

/// <summary>Przenieś: strony i miejsce docelowe.</summary>
internal sealed class MoveDialog : PagesDialog
{
    private readonly RangePicker _range;
    private readonly PositionPicker _pos;

    public MoveDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx) : base(loc, dialogs, "Pages.Move.Title")
    {
        _range = new RangePicker(this, ctx, allowSubset: false, preferSelection: true);
        Body.Children.Add(_range.Element);
        _pos = new PositionPicker(this, ctx, "Pages.Move.Target");
        Body.Children.Add(_pos.Element);
    }

    public IReadOnlyList<int> Pages { get; private set; } = Array.Empty<int>();
    public int Target { get; private set; }

    protected override bool Accept()
    {
        if (!_range.TryGet(out var p)) return Error("Pages.Error.InvalidRange");
        if (!_pos.TryGet(out var t)) return Error("Pages.Error.InvalidPage");
        (Pages, Target) = (p, t);
        return true;
    }
}

/// <summary>Zamień dwie strony.</summary>
internal sealed class SwapDialog : PagesDialog
{
    private readonly TextBox _a, _b;
    private readonly int _count;

    public SwapDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx) : base(loc, dialogs, "Pages.Swap.Title")
    {
        _count = ctx.PageCount;
        int first = ctx.SelectedPages.Count >= 1 ? ctx.SelectedPages[0] : ctx.CurrentPage;
        int second = ctx.SelectedPages.Count >= 2 ? ctx.SelectedPages[1] : Math.Min(ctx.PageCount - 1, first + 1);
        _a = Box((first + 1).ToString(CultureInfo.InvariantCulture), 60);
        _b = Box((second + 1).ToString(CultureInfo.InvariantCulture), 60);
        Body.Children.Add(Row("Pages.Swap.PageA", _a));
        Body.Children.Add(Row("Pages.Swap.PageB", _b));
    }

    public int PageA { get; private set; }
    public int PageB { get; private set; }

    protected override bool Accept()
    {
        if (!TryInt(_a, out var a, 1, _count) || !TryInt(_b, out var b, 1, _count)) return Error("Pages.Error.InvalidPageOf", _count);
        if (a == b) return Error("Pages.Error.SamePage");
        (PageA, PageB) = (a - 1, b - 1);
        return true;
    }
}

/// <summary>Przeplatanie dwóch plików (np. skany stron nieparzystych i parzystych).</summary>
internal sealed class InterleaveDialog : PagesDialog
{
    private readonly TextBox _file = Box(string.Empty, 300);
    private readonly CheckBox _reverse;

    public InterleaveDialog(ILocalizationService loc, IDialogService dialogs) : base(loc, dialogs, "Pages.Interleave.Title")
    {
        Body.Children.Add(Hint("Pages.Interleave.Hint"));
        Body.Children.Add(Row("Pages.Interleave.Second", FilePicker(_file, "Pages.Dialog.SelectPdf", PdfFilters)));
        _reverse = Check("Pages.Interleave.Reverse", true);
        Body.Children.Add(_reverse);
    }

    public string FilePath => _file.Text.Trim();
    public bool ReverseSecond => _reverse.IsChecked == true;

    protected override bool Accept() => File.Exists(FilePath) || Error("Pages.Error.FileNotFound");
}

/// <summary>Obracanie stron: kierunek i zakres (wszystkie/parzyste/nieparzyste/zakres).</summary>
internal sealed class RotateDialog : PagesDialog
{
    private readonly RangePicker _range;
    private readonly ComboBox _dir;

    public RotateDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx) : base(loc, dialogs, "Pages.Rotate.Title")
    {
        _dir = Combo(new[] { (90, "Pages.Rotate.Right"), (-90, "Pages.Rotate.Left"), (180, "Pages.Rotate.Half") }, 90);
        Body.Children.Add(Row("Pages.Rotate.Direction", _dir));
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: false);
        Body.Children.Add(_range.Element);
    }

    public IReadOnlyList<int> Pages { get; private set; } = Array.Empty<int>();
    public int Degrees => ComboValue<int>(_dir);

    protected override bool Accept()
    {
        if (!_range.TryGet(out var p)) return Error("Pages.Error.InvalidRange");
        Pages = p;
        return true;
    }
}

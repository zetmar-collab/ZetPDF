using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Ui;

/// <summary>Kadruj strony: marginesy (mm) w układzie wyświetlanym, usuwanie białych marginesów, MediaBox.</summary>
internal sealed class CropDialog : PagesDialog
{
    private readonly TextBox _l = Box("0", 60), _t = Box("0", 60), _r = Box("0", 60), _b = Box("0", 60);
    private readonly CheckBox _white, _media;
    private readonly RangePicker _range;

    public CropDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx) : base(loc, dialogs, "Pages.Crop.Title")
    {
        var grid = new StackPanel();
        grid.Children.Add(Row("Pages.Crop.Left", Horizontal(_l, Label("Pages.Unit.Mm")), 120));
        grid.Children.Add(Row("Pages.Crop.Top", Horizontal(_t, Label("Pages.Unit.Mm")), 120));
        grid.Children.Add(Row("Pages.Crop.Right", Horizontal(_r, Label("Pages.Unit.Mm")), 120));
        grid.Children.Add(Row("Pages.Crop.Bottom", Horizontal(_b, Label("Pages.Unit.Mm")), 120));
        Body.Children.Add(Section("Pages.Crop.Margins", grid));
        _white = Check("Pages.Crop.RemoveWhite");
        _white.Checked += (_, _) => grid.IsEnabled = false;
        _white.Unchecked += (_, _) => grid.IsEnabled = true;
        _media = Check("Pages.Crop.MediaBox");
        Body.Children.Add(_white);
        Body.Children.Add(_media);
        Body.Children.Add(Hint("Pages.Crop.Hint"));
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: false);
        Body.Children.Add(_range.Element);
    }

    public CropOptions Options { get; private set; } = new();
    public IReadOnlyList<int> Pages { get; private set; } = Array.Empty<int>();

    protected override bool Accept()
    {
        if (!TryNumber(_l, out var l, 0, 2000) || !TryNumber(_t, out var t, 0, 2000) || !TryNumber(_r, out var r, 0, 2000) || !TryNumber(_b, out var b, 0, 2000))
            return Error("Pages.Error.InvalidNumber");
        if (!_range.TryGet(out var p)) return Error("Pages.Error.InvalidRange");
        Options = new CropOptions { Left = l * MmToPt, Top = t * MmToPt, Right = r * MmToPt, Bottom = b * MmToPt, RemoveWhiteMargins = _white.IsChecked == true, ApplyToMediaBox = _media.IsChecked == true };
        Pages = p;
        return true;
    }
}

/// <summary>Zmień rozmiar stron: format docelowy, skalowanie treści, zakres.</summary>
internal sealed class ResizeDialog : PagesDialog
{
    private readonly SizePicker _size;
    private readonly CheckBox _scale;
    private readonly RangePicker _range;

    public ResizeDialog(ILocalizationService loc, IDialogService dialogs, DocContext ctx) : base(loc, dialogs, "Pages.Resize.Title")
    {
        _size = new SizePicker(this, PdfSize.A4, allowCurrent: false);
        Body.Children.Add(_size.Element);
        _scale = Check("Pages.Resize.Scale", true);
        Body.Children.Add(_scale);
        _range = new RangePicker(this, ctx, allowSubset: true, preferSelection: false);
        Body.Children.Add(_range.Element);
    }

    public ResizeOptions Options { get; private set; } = new();
    public IReadOnlyList<int> Pages { get; private set; } = Array.Empty<int>();

    protected override bool Accept()
    {
        if (!_size.TryGet(out var s)) return Error("Pages.Error.InvalidSize");
        if (!_range.TryGet(out var p)) return Error("Pages.Error.InvalidRange");
        Options = new ResizeOptions { TargetSize = s, ScaleContent = _scale.IsChecked == true };
        Pages = p;
        return true;
    }
}

/// <summary>Spłaszcz: komentarze i/lub pola formularza.</summary>
internal sealed class FlattenDialog : PagesDialog
{
    private readonly CheckBox _annots, _fields, _links;

    public FlattenDialog(ILocalizationService loc, IDialogService dialogs) : base(loc, dialogs, "Pages.Flatten.Title")
    {
        Body.Children.Add(Hint("Pages.Flatten.Hint"));
        _annots = Check("Pages.Flatten.Annotations", true);
        _fields = Check("Pages.Flatten.Fields", true);
        _links = Check("Pages.Flatten.KeepLinks", true);
        Body.Children.Add(_annots);
        Body.Children.Add(_fields);
        Body.Children.Add(_links);
    }

    public FlattenOptions Options { get; private set; } = new();

    protected override bool Accept()
    {
        if (_annots.IsChecked != true && _fields.IsChecked != true) return Error("Pages.Error.NothingSelected");
        Options = new FlattenOptions { Annotations = _annots.IsChecked == true, FormFields = _fields.IsChecked == true, KeepLinks = _links.IsChecked == true };
        return true;
    }
}

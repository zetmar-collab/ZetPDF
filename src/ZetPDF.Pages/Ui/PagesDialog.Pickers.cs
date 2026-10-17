using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using PageRange = ZetPDF.Core.Utilities.PageRange;

namespace ZetPDF.Pages.Ui;

/// <summary>Stan dokumentu dla okien: liczba stron, bieżąca strona, zaznaczone miniatury, rozmiar bieżącej strony.</summary>
internal sealed record DocContext(int PageCount, int CurrentPage, IReadOnlyList<int> SelectedPages, PdfSize CurrentPageSize, string DisplayName);

internal abstract partial class PagesDialog
{
    /// <summary>Wybór stron: wszystkie / bieżąca / zaznaczone / zakres, opcjonalnie tylko parzyste lub nieparzyste.</summary>
    protected sealed class RangePicker
    {
        private readonly DocContext _ctx;
        private readonly RadioButton _all, _current, _selected, _custom;
        private readonly TextBox _text = Box(string.Empty, 160);
        private readonly ComboBox? _subset;

        public RangePicker(PagesDialog d, DocContext ctx, bool allowSubset, bool preferSelection)
        {
            _ctx = ctx;
            _all = d.Radio("Pages.Range.All", "range");
            _current = d.Radio("Pages.Range.Current", "range");
            _selected = d.Radio("Pages.Range.Selected", "range");
            _custom = d.Radio("Pages.Range.Custom", "range");
            bool multi = ctx.SelectedPages.Count > 1;
            _selected.IsEnabled = multi;
            if (preferSelection && multi) _selected.IsChecked = true;
            else if (preferSelection) _current.IsChecked = true;
            else _all.IsChecked = true;
            _text.Text = multi ? PageRange.Format(ctx.SelectedPages) : (ctx.CurrentPage + 1).ToString(CultureInfo.InvariantCulture);
            _text.GotKeyboardFocus += (_, _) => _custom.IsChecked = true;
            var panel = new StackPanel();
            panel.Children.Add(Horizontal(_all, _current, _selected));
            panel.Children.Add(Horizontal(_custom, _text, d.Label("Pages.Range.Hint")));
            if (allowSubset)
            {
                _subset = d.Combo(new[] { (0, "Pages.Range.Subset.All"), (1, "Pages.Range.Subset.Even"), (2, "Pages.Range.Subset.Odd") }, 0, 200);
                panel.Children.Add(d.Row("Pages.Range.Subset", _subset, 120));
            }
            Element = d.Section("Pages.Range.Header", panel);
        }

        public FrameworkElement Element { get; }

        /// <summary>Ustawia zakres tekstowy (np. z zapisanych opcji przy „Aktualizuj”); pusty = wszystkie.</summary>
        public void SetText(string? range)
        {
            if (string.IsNullOrWhiteSpace(range)) { _all.IsChecked = true; return; }
            _text.Text = range;
            _custom.IsChecked = true;
        }

        public bool IsAll => _all.IsChecked == true && (_subset is null || ComboValue<int>(_subset) == 0);

        public bool TryGet(out IReadOnlyList<int> pages)
        {
            pages = Array.Empty<int>();
            IReadOnlyList<int> basePages;
            if (_all.IsChecked == true) basePages = Enumerable.Range(0, _ctx.PageCount).ToArray();
            else if (_current.IsChecked == true) basePages = new[] { _ctx.CurrentPage };
            else if (_selected.IsChecked == true) basePages = _ctx.SelectedPages;
            else if (!PageRange.TryParse(_text.Text, _ctx.PageCount, out basePages)) return false;
            int subset = _subset is null ? 0 : ComboValue<int>(_subset);
            // parzyste/nieparzyste wg numeru strony (1-based)
            pages = basePages.Where(p => subset == 0 || (subset == 1 ? (p + 1) % 2 == 0 : (p + 1) % 2 == 1)).ToArray();
            return pages.Count > 0;
        }

        /// <summary>Zakres jako tekst ("" = wszystkie) dla opcji oznaczeń.</summary>
        public bool TryGetText(out string range)
        {
            range = string.Empty;
            if (!TryGet(out var pages)) return false;
            range = pages.Count == _ctx.PageCount ? string.Empty : PageRange.Format(pages);
            return true;
        }
    }

    /// <summary>Miejsce wstawienia: na początku / na końcu / przed / za stroną N.</summary>
    protected sealed class PositionPicker
    {
        private readonly int _count;
        private readonly ComboBox _mode;
        private readonly TextBox _page;

        public PositionPicker(PagesDialog d, DocContext ctx, string headerKey = "Pages.Position.Header")
        {
            _count = ctx.PageCount;
            _mode = d.Combo(new[] { (0, "Pages.Position.First"), (1, "Pages.Position.Last"), (2, "Pages.Position.Before"), (3, "Pages.Position.After") }, 3, 160);
            _page = Box((ctx.CurrentPage + 1).ToString(CultureInfo.InvariantCulture), 60);
            _mode.SelectionChanged += (_, _) => _page.IsEnabled = ComboValue<int>(_mode) >= 2;
            _ofText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = d.Loc.Format("Pages.Position.PageOfFormat", _count) };
            Element = d.Section(headerKey, Horizontal(_mode, _page, _ofText));
        }

        private readonly TextBlock _ofText;
        public FrameworkElement Element { get; }

        /// <summary>Indeks „wstaw przed” (0..PageCount).</summary>
        public bool TryGet(out int insertAt)
        {
            insertAt = 0;
            switch (ComboValue<int>(_mode))
            {
                case 0: insertAt = 0; return true;
                case 1: insertAt = _count; return true;
            }
            if (!TryInt(_page, out var n, 1, Math.Max(1, _count))) return false;
            insertAt = ComboValue<int>(_mode) == 2 ? n - 1 : n;
            return true;
        }
    }

    /// <summary>Rozmiar strony: formaty, rozmiar bieżącej strony, własny (mm) + orientacja.</summary>
    protected sealed class SizePicker
    {
        private readonly ComboBox _format;
        private readonly TextBox _w = Box("210", 70), _h = Box("297", 70);
        private readonly RadioButton _portrait, _landscape;
        private readonly PdfSize _current;

        public SizePicker(PagesDialog d, PdfSize current, bool allowCurrent)
        {
            _current = current;
            var items = new List<(string, string)> { ("A4", "Pages.Size.A4"), ("A3", "Pages.Size.A3"), ("A5", "Pages.Size.A5"), ("Letter", "Pages.Size.Letter"), ("Legal", "Pages.Size.Legal") };
            if (allowCurrent) items.Insert(0, ("Current", "Pages.Size.Current"));
            items.Add(("Custom", "Pages.Size.Custom"));
            _format = d.Combo(items, allowCurrent ? "Current" : "A4", 200);
            _portrait = d.Radio("Pages.Size.Portrait", "orient", current.Height >= current.Width);
            _landscape = d.Radio("Pages.Size.Landscape", "orient", current.Height < current.Width);
            void Sync()
            {
                bool custom = ComboValue<string>(_format) == "Custom";
                _w.IsEnabled = _h.IsEnabled = custom;
                bool cur = ComboValue<string>(_format) == "Current";
                _portrait.IsEnabled = _landscape.IsEnabled = !cur;
            }
            _format.SelectionChanged += (_, _) => Sync();
            Sync();
            var panel = new StackPanel();
            panel.Children.Add(d.Row("Pages.Size.Format", _format, 120));
            panel.Children.Add(d.Row("Pages.Size.CustomMm", Horizontal(_w, new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }, _h, d.Label("Pages.Unit.Mm")), 120));
            panel.Children.Add(d.Row("Pages.Size.Orientation", Horizontal(_portrait, _landscape), 120));
            Element = d.Section("Pages.Size.Header", panel);
        }

        public FrameworkElement Element { get; }

        public bool TryGet(out PdfSize size)
        {
            size = PdfSize.A4;
            switch (ComboValue<string>(_format))
            {
                case "Current": size = _current; return true;
                case "A3": size = PdfSize.A3; break;
                case "A5": size = PdfSize.A5; break;
                case "Letter": size = PdfSize.Letter; break;
                case "Legal": size = PdfSize.Legal; break;
                case "Custom":
                    if (!TryNumber(_w, out var w, 10, 5000) || !TryNumber(_h, out var h, 10, 5000)) return false;
                    size = new PdfSize(w * MmToPt, h * MmToPt);
                    break;
            }
            bool landscape = _landscape.IsChecked == true;
            if (landscape != size.Width > size.Height) size = new PdfSize(size.Height, size.Width);
            return true;
        }
    }

    /// <summary>Czcionka (systemowa TrueType – osadzana z polskimi znakami), rozmiar, pogrubienie, kursywa, kolor.</summary>
    protected sealed class StyleEditor
    {
        private readonly ComboBox _font;
        private readonly TextBox _size;
        private readonly CheckBox _bold, _italic;
        private readonly ColorField _color;

        public StyleEditor(PagesDialog d, TextStyle style)
        {
            _font = FontCombo(style.FontFamily);
            _size = Box(Fmt(style.FontSize), 60);
            _bold = d.Check("Pages.Style.Bold", style.Bold);
            _italic = d.Check("Pages.Style.Italic", style.Italic);
            _color = new ColorField(style.Color);
            var p = new StackPanel();
            p.Children.Add(d.Row("Pages.Style.Font", _font));
            p.Children.Add(d.Row("Pages.Style.Size", Horizontal(_size, d.Label("Pages.Unit.Pt"), _bold, _italic)));
            p.Children.Add(d.Row("Pages.Style.Color", _color.Element));
            Element = d.Section("Pages.Style.Header", p);
        }

        public FrameworkElement Element { get; }

        public bool TryGet(out TextStyle style)
        {
            style = new TextStyle();
            if (!TryNumber(_size, out var size, 1, 1000) || !_color.TryGet(out var color)) return false;
            var family = string.IsNullOrWhiteSpace(_font.Text) ? "Arial" : _font.Text.Trim();
            style = new TextStyle { FontFamily = family, FontSize = size, Bold = _bold.IsChecked == true, Italic = _italic.IsChecked == true, Color = color };
            return true;
        }
    }

    protected ComboBox HorizontalCombo(HorizontalPosition value) =>
        Combo(new[] { (HorizontalPosition.Left, "Pages.Pos.Left"), (HorizontalPosition.Center, "Pages.Pos.Center"), (HorizontalPosition.Right, "Pages.Pos.Right") }, value, 140);

    protected ComboBox VerticalCombo(VerticalPosition value) =>
        Combo(new[] { (VerticalPosition.Top, "Pages.Pos.Top"), (VerticalPosition.Middle, "Pages.Pos.Middle"), (VerticalPosition.Bottom, "Pages.Pos.Bottom") }, value, 140);
}

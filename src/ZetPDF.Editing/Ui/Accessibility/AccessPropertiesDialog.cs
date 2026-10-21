using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Accessibility;

namespace ZetPDF.Editing.Ui.Accessibility;

/// <summary>Okno „Tytuł, język i tekst alternatywny” (Access.SetProperties oraz naprawa tytułu/języka).</summary>
internal sealed class AccessPropertiesDialog : DialogBase
{
    private static readonly string[] Languages = { "pl-PL", "en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "it-IT", "cs-CZ", "sk-SK", "uk-UA", "ru-RU", "lt-LT" };
    private readonly TextBox _title;
    private readonly ComboBox _lang;
    private readonly CheckBox _display;
    private readonly List<(AccessFigure Figure, TextBox Box)> _figures = new();

    public AccessPropertiesDialog(ILocalizationService loc, string title, string language, IReadOnlyList<AccessFigure> figures, bool showFigures)
        : base(loc, "Edit.Access.Properties.Title", 520)
    {
        _title = Ui.Box(title);
        System.Windows.Automation.AutomationProperties.SetName(_title, loc["Edit.Access.Properties.DocTitle"]);
        _lang = new ComboBox { IsEditable = true, Margin = new Thickness(0, 3, 0, 3), Text = language };
        foreach (var l in Languages) _lang.Items.Add(l);
        _lang.Text = language;
        System.Windows.Automation.AutomationProperties.SetName(_lang, loc["Edit.Access.Properties.Language"]);
        _display = Ui.Check(loc, "Edit.Access.Properties.DisplayDocTitle", true);

        var body = new StackPanel();
        body.Children.Add(Ui.Form(
            (Ui.Label(loc, "Edit.Access.Properties.DocTitle"), _title),
            (Ui.Label(loc, "Edit.Access.Properties.Language"), _lang),
            (null, Ui.Label(loc, "Edit.Access.Properties.LanguageHint", secondary: true)),
            (null, _display)));
        if (showFigures)
        {
            body.Children.Add(Ui.Label(loc, "Edit.Access.Properties.Figures"));
            if (figures.Count == 0)
                body.Children.Add(Ui.Label(loc, "Edit.Access.Properties.NoFigures", secondary: true));
            else
            {
                var rows = new StackPanel();
                foreach (var f in figures)
                {
                    var box = Ui.Box(f.Alt ?? string.Empty);
                    var label = Ui.Text(loc.Format("Edit.Access.Properties.Figure", f.Index + 1, f.PageIndex >= 0 ? (f.PageIndex + 1).ToString(CultureInfo.CurrentCulture) : "?"));
                    System.Windows.Automation.AutomationProperties.SetName(box, label.Text);
                    rows.Children.Add(Ui.Form((label, box)));
                    _figures.Add((f, box));
                }
                body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
        }
        SetBody(body);
        AddOkCancel();
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }

    public AccessProperties Result { get; private set; } = new();

    protected override bool Validate()
    {
        var lang = _lang.Text.Trim();
        if (lang.Length > 0)
        {
            try { _ = CultureInfo.GetCultureInfo(lang); }
            catch (CultureNotFoundException) { ShowError(L.Format("Edit.Access.Properties.InvalidLanguage", lang)); return false; }
        }
        if (_title.Text.Trim().Length == 0) { ShowError(L["Edit.Access.Properties.EmptyTitle"]); return false; }
        var alts = _figures.Where(x => (x.Box.Text ?? string.Empty).Trim() != (x.Figure.Alt ?? string.Empty))
            .ToDictionary(x => x.Figure.Index, x => x.Box.Text.Trim());
        Result = new AccessProperties { Title = _title.Text.Trim(), Language = lang, DisplayDocTitle = _display.IsChecked == true, AltTexts = alts };
        return true;
    }
}

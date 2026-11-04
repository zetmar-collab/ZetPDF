using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Editing.Conversion;

namespace ZetPDF.Editing.Ui.Dialogs;

/// <summary>Adres strony lub lokalny plik HTML, z wyraźną informacją o połączeniu sieciowym. Nic nie jest pobierane przed zatwierdzeniem.</summary>
internal sealed class WebToPdfDialog : DialogBase
{
    private readonly TextBox _source = Ui.Box();

    public WebToPdfDialog(ILocalizationService loc, Func<string?> browse) : base(loc, "Edit.Web.Title", 560)
    {
        System.Windows.Automation.AutomationProperties.SetName(_source, loc["Edit.Web.Source"]);
        var warning = Ui.Label(loc, "Edit.Web.NetworkNotice");
        warning.FontWeight = FontWeights.SemiBold;
        warning.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusWarning);
        var browseButton = Ui.Button(loc, "Edit.Web.Browse", (_, _) => { if (browse() is { } path) _source.Text = path; });
        browseButton.HorizontalAlignment = HorizontalAlignment.Left;
        browseButton.Margin = new Thickness(0, 3, 0, 3);
        SetBody(Ui.Form(
            (null, warning),
            (Ui.Label(loc, "Edit.Web.Source"), _source),
            (null, browseButton),
            (null, Ui.Label(loc, "Edit.Web.LocalNote", secondary: true))));
        AddOkCancel("Edit.Web.Create");
        Loaded += (_, _) => _source.Focus();
    }

    public WebSource? Source { get; private set; }

    protected override bool Validate()
    {
        var error = WebSourceValidator.Validate(_source.Text, out var source);
        Source = source;
        if (error == WebSourceError.None) return true;
        ShowError(L["Edit.Web.Invalid." + error]);
        return false;
    }
}

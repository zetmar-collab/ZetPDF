using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.UI;

/// <summary>
/// Okno przed połączeniem: lista adresów i opcja sieci lokalnej. Opcja jest domyślnie wyłączona i nie jest zapamiętywana: adres pochodzi z certyfikatu, a certyfikat
/// z dokumentu, więc bez zgody nie może kierować zapytań do sieci użytkownika.
/// </summary>
internal sealed class LongTermConfirmDialog : SecurityDialog
{
    private readonly CheckBox _allowLocal;

    public LongTermConfirmDialog(ILocalizationService loc, string text, bool allowLocalNetwork) : base(loc, "Security.LongTerm.Title", 560)
    {
        _allowLocal = Ui.Check(loc, "Security.LongTerm.AllowLocal", allowLocalNetwork);
        var body = new StackPanel();
        body.Children.Add(Ui.Plain(text));
        body.Children.Add(_allowLocal);
        body.Children.Add(Ui.Plain(loc["Security.LongTerm.AllowLocal.Info"], secondary: true));
        SetBody(body);
        AddOkCancel("Security.LongTerm.Connect");
    }

    public bool AllowLocalNetwork => _allowLocal.IsChecked == true;

    /// <summary>Null, gdy użytkownik odmówił; inaczej wybór opcji sieci lokalnej.</summary>
    public static bool? Ask(IDialogService dialogs, ILocalizationService loc, string text, bool allowLocalNetwork)
    {
        var window = new LongTermConfirmDialog(loc, text, allowLocalNetwork);
        return dialogs.ShowDialog(window) == true ? window.AllowLocalNetwork : null;
    }
}

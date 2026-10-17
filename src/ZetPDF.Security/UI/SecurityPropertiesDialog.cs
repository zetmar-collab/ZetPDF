using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.UI;

/// <summary>Właściwości zabezpieczeń: plik źródłowy, ustawienia stosowane przy zapisie, uprawnienia efektywne w ZetPDF.</summary>
internal sealed class SecurityPropertiesDialog : SecurityDialog
{
    public SecurityPropertiesDialog(ILocalizationService loc, IPdfDocumentSession session, bool resaveOwnerOnly) : base(loc, "Security.Properties.Title", 520)
    {
        var info = session.SecurityInfo;
        var save = session.Current.Encryption;
        var body = new StackPanel();

        body.Children.Add(Ui.Group(loc, "Security.Properties.FileGroup", Form(
            (Ui.Text(loc, "Security.Properties.Method"), Ui.Plain(MethodText(loc, info.IsEncrypted ? info.Method : PdfEncryptionMethod.None) +
                (info.IsEncrypted ? " (R" + info.Revision + ")" : string.Empty))),
            (Ui.Text(loc, "Security.Properties.OpenedWith"), Ui.Plain(!info.IsEncrypted ? "–" :
                loc[info.OpenedWithOwnerPassword ? "Security.Properties.OpenedOwner" : info.Password is null ? "Security.Properties.OpenedNoPassword" : "Security.Properties.OpenedUser"])),
            (Ui.Text(loc, "Security.Properties.FilePermissions"), Ui.Plain(PermissionsText(loc, info.IsEncrypted ? info.FilePermissions : DocumentPermissions.All))))));

        body.Children.Add(Ui.Group(loc, "Security.Properties.SaveGroup", Form(
            (Ui.Text(loc, "Security.Properties.Method"), Ui.Plain(MethodText(loc, save?.Method ?? PdfEncryptionMethod.None))),
            (Ui.Text(loc, "Security.Properties.OpenPassword"), Ui.Plain(loc[save?.UserPassword is { Length: > 0 } ? "Security.Properties.Yes" : "Security.Properties.No"])),
            (Ui.Text(loc, "Security.Properties.FilePermissions"), Ui.Plain(PermissionsText(loc, save?.Permissions ?? DocumentPermissions.All))))));

        body.Children.Add(Ui.Group(loc, "Security.Properties.EffectiveGroup", Ui.Plain(PermissionsText(loc, session.Permissions))));
        if (resaveOwnerOnly)
        {
            var warn = Ui.Plain(loc["Core.Encryption.ResaveOwnerOnly"]);
            warn.SetResourceReference(TextBlock.ForegroundProperty, Core.Theming.ThemeKeys.StatusWarning);
            body.Children.Add(warn);
        }
        SetBody(body);
        AddClose();
    }

    public static string MethodText(ILocalizationService loc, PdfEncryptionMethod m) => loc["Security.Method." + m];

    public static string PermissionsText(ILocalizationService loc, DocumentPermissions p)
    {
        if (p == DocumentPermissions.All) return loc["Security.Perm.All"];
        var parts = new List<string>();
        foreach (var flag in new[]
        {
            DocumentPermissions.Print, DocumentPermissions.PrintHighQuality, DocumentPermissions.ModifyContent, DocumentPermissions.CopyContent,
            DocumentPermissions.Annotate, DocumentPermissions.FillForms, DocumentPermissions.ExtractForAccessibility, DocumentPermissions.AssembleDocument,
        })
        {
            if (p.HasFlag(flag)) parts.Add(loc["Security.Perm." + flag]);
        }
        return parts.Count == 0 ? loc["Security.Perm.None"] : string.Join(", ", parts);
    }
}

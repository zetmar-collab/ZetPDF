using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.UI;

/// <summary>Ochrona hasłem: hasło otwierania, hasło uprawnień z ograniczeniami, metoda szyfrowania.</summary>
internal sealed class PasswordProtectDialog : SecurityDialog
{
    private readonly CheckBox _requireOpen;
    private readonly PasswordBox _user = new(), _userConfirm = new();
    private readonly CheckBox _restrict;
    private readonly PasswordBox _owner = new(), _ownerConfirm = new();
    private readonly ComboBox _printing = new();
    private readonly CheckBox _modify, _copy, _annotate, _fill, _assemble, _accessibility;
    private readonly ComboBox _method = new();
    private readonly StackPanel _permPanel;

    public PasswordProtectDialog(ILocalizationService loc, PdfEncryptionSettings? current) : base(loc, "Security.Protect.Title", 500)
    {
        var perms = current?.Permissions ?? DocumentPermissions.All;
        _requireOpen = Ui.Check(loc, "Security.Protect.RequireOpen", current?.UserPassword is not null);
        _restrict = Ui.Check(loc, "Security.Protect.Restrict", current is not null && perms != DocumentPermissions.All);

        foreach (var key in new[] { "Security.Protect.Print.None", "Security.Protect.Print.Low", "Security.Protect.Print.High" })
            _printing.Items.Add(new ComboBoxItem().Loc(ContentControl.ContentProperty, loc, key));
        _printing.SelectedIndex = perms.HasFlag(DocumentPermissions.PrintHighQuality) && perms.HasFlag(DocumentPermissions.Print) ? 2 : perms.HasFlag(DocumentPermissions.Print) ? 1 : 0;
        _modify = Ui.Check(loc, "Security.Protect.Perm.Modify", perms.HasFlag(DocumentPermissions.ModifyContent));
        _copy = Ui.Check(loc, "Security.Protect.Perm.Copy", perms.HasFlag(DocumentPermissions.CopyContent));
        _annotate = Ui.Check(loc, "Security.Protect.Perm.Annotate", perms.HasFlag(DocumentPermissions.Annotate));
        _fill = Ui.Check(loc, "Security.Protect.Perm.FillForms", perms.HasFlag(DocumentPermissions.FillForms));
        _assemble = Ui.Check(loc, "Security.Protect.Perm.Assemble", perms.HasFlag(DocumentPermissions.AssembleDocument));
        _accessibility = Ui.Check(loc, "Security.Protect.Perm.Accessibility", perms.HasFlag(DocumentPermissions.ExtractForAccessibility));

        _method.Items.Add(new ComboBoxItem { Tag = PdfEncryptionMethod.Aes256 }.Loc(ContentControl.ContentProperty, loc, "Security.Protect.Method.Aes256"));
        _method.Items.Add(new ComboBoxItem { Tag = PdfEncryptionMethod.Aes128 }.Loc(ContentControl.ContentProperty, loc, "Security.Protect.Method.Aes128"));
        _method.SelectedIndex = current?.Method == PdfEncryptionMethod.Aes128 ? 1 : 0;

        AutomationName(_user, "Security.Protect.Password");
        AutomationName(_userConfirm, "Security.Protect.Confirm");
        AutomationName(_owner, "Security.Protect.OwnerPassword");
        AutomationName(_ownerConfirm, "Security.Protect.Confirm");

        var openForm = Form((Ui.Text(loc, "Security.Protect.Password"), _user), (Ui.Text(loc, "Security.Protect.Confirm"), _userConfirm));
        var ownerForm = Form((Ui.Text(loc, "Security.Protect.OwnerPassword"), _owner), (Ui.Text(loc, "Security.Protect.Confirm"), _ownerConfirm),
            (Ui.Text(loc, "Security.Protect.Printing"), _printing));
        _permPanel = new StackPanel();
        _permPanel.Children.Add(ownerForm);
        foreach (var c in new[] { _modify, _copy, _annotate, _fill, _assemble, _accessibility }) _permPanel.Children.Add(c);

        var openPanel = new StackPanel();
        openPanel.Children.Add(_requireOpen);
        openPanel.Children.Add(openForm);
        var restrictPanel = new StackPanel();
        restrictPanel.Children.Add(_restrict);
        restrictPanel.Children.Add(_permPanel);

        var body = new StackPanel();
        body.Children.Add(Ui.Group(loc, "Security.Protect.OpenGroup", openPanel));
        body.Children.Add(Ui.Group(loc, "Security.Protect.PermGroup", restrictPanel));
        body.Children.Add(Form((Ui.Text(loc, "Security.Protect.Method"), _method)));
        body.Children.Add(new TextBlock { Margin = new Thickness(0, 8, 0, 0) }.Loc(TextBlock.TextProperty, loc, "Security.Protect.AppliedOnSave"));
        ((TextBlock)body.Children[^1]).TextWrapping = TextWrapping.Wrap;
        ((TextBlock)body.Children[^1]).SetResourceReference(TextBlock.ForegroundProperty, Core.Theming.ThemeKeys.TextSecondary);
        SetBody(body);
        AddOkCancel();

        void Sync()
        {
            openForm.IsEnabled = _requireOpen.IsChecked == true;
            _permPanel.IsEnabled = _restrict.IsChecked == true;
        }
        _requireOpen.Checked += (_, _) => Sync();
        _requireOpen.Unchecked += (_, _) => Sync();
        _restrict.Checked += (_, _) => Sync();
        _restrict.Unchecked += (_, _) => Sync();
        Sync();
        Loaded += (_, _) => (_requireOpen.IsChecked == true ? _user : _owner).Focus();
    }

    private void AutomationName(DependencyObject o, string key) => System.Windows.Automation.AutomationProperties.SetName(o, Loc[key]);

    public PdfEncryptionSettings? Result { get; private set; }

    protected override bool Validate()
    {
        bool open = _requireOpen.IsChecked == true, restrict = _restrict.IsChecked == true;
        if (!open && !restrict) { ShowError(Loc["Security.Protect.Error.NothingSelected"]); return false; }
        var method = (PdfEncryptionMethod)((ComboBoxItem)_method.SelectedItem).Tag;
        string? user = null;
        if (open)
        {
            if (_user.Password.Length == 0) { ShowError(Loc["Security.Protect.Error.Empty"]); return false; }
            if (_user.Password != _userConfirm.Password) { ShowError(Loc["Security.Protect.Error.Mismatch"]); return false; }
            user = _user.Password;
        }
        string owner;
        var perms = DocumentPermissions.All;
        if (restrict)
        {
            if (_owner.Password.Length == 0) { ShowError(Loc["Security.Protect.Error.Empty"]); return false; }
            if (_owner.Password != _ownerConfirm.Password) { ShowError(Loc["Security.Protect.Error.Mismatch"]); return false; }
            if (user is not null && _owner.Password == user) { ShowError(Loc["Security.Protect.Error.SamePasswords"]); return false; }
            owner = _owner.Password;
            perms = DocumentPermissions.None;
            if (_printing.SelectedIndex >= 1) perms |= DocumentPermissions.Print;
            if (_printing.SelectedIndex == 2) perms |= DocumentPermissions.PrintHighQuality;
            if (_modify.IsChecked == true) perms |= DocumentPermissions.ModifyContent;
            if (_copy.IsChecked == true) perms |= DocumentPermissions.CopyContent;
            if (_annotate.IsChecked == true) perms |= DocumentPermissions.Annotate;
            if (_fill.IsChecked == true) perms |= DocumentPermissions.FillForms;
            if (_assemble.IsChecked == true) perms |= DocumentPermissions.AssembleDocument;
            if (_accessibility.IsChecked == true) perms |= DocumentPermissions.ExtractForAccessibility;
        }
        else owner = user!; // bez ograniczeń: hasło otwierania daje pełne uprawnienia
        if (method != PdfEncryptionMethod.Aes256 && ((user ?? string.Empty) + owner).Any(c => c > 255))
        {
            ShowError(Loc["Security.Protect.Error.Aes128Chars"]);
            return false;
        }
        Result = new PdfEncryptionSettings(user, owner, method, perms);
        return true;
    }
}

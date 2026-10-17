using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.UI;

/// <summary>Ustawienia modułu zapamiętywane między uruchomieniami (bez haseł).</summary>
public sealed class SecurityModuleSettings
{
    public string Reason { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ContactInfo { get; set; } = string.Empty;
    public bool ShowName { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowReason { get; set; } = true;
    public bool ShowLocation { get; set; } = true;
    public string? ImagePath { get; set; }
    /// <summary>Ostatni identyfikator: odcisk certyfikatu z magazynu lub ścieżka PFX.</summary>
    public string? LastIdentity { get; set; }
}

/// <summary>Pozycja listy identyfikatorów cyfrowych.</summary>
internal sealed class IdentityItem
{
    public IdentityItem(X509Certificate2 cert) { Certificate = cert; Key = cert.Thumbprint; }
    public IdentityItem(string pfxPath) { PfxPath = pfxPath; Key = pfxPath; }
    public X509Certificate2? Certificate { get; }
    public string? PfxPath { get; }
    public string Key { get; }
    public override string ToString() => Certificate is { } c
        ? $"{c.GetNameInfo(X509NameType.SimpleName, false)} – {c.GetNameInfo(X509NameType.SimpleName, true)} ({c.NotAfter:d})"
        : Path.GetFileName(PfxPath!) + "  (" + PfxPath + ")";
}

/// <summary>Podpisz cyfrowo: identyfikator, powód, miejsce, wygląd, znacznik czasu (tylko na wyraźne życzenie).</summary>
internal sealed class SignDialog : SecurityDialog
{
    private readonly ISignatureService _signatures;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly SecurityModuleSettings _settings;
    private readonly ComboBox _identity = new() { MinWidth = 300 };
    private readonly PasswordBox _pfxPassword = new();
    private readonly ComboBox _reason = new() { IsEditable = true };
    private readonly TextBox _location = new(), _contact = new();
    private readonly CheckBox _showName, _showDate, _showReason, _showLocation, _invisible, _useTsa;
    private readonly TextBox _image = new() { IsReadOnly = true };
    private readonly string _tsaUrl;

    public SignDialog(ILocalizationService loc, ISignatureService signatures, IAppPaths paths, IDialogService dialogs, SecurityModuleSettings settings,
        string tsaUrl, bool hasRectangle) : base(loc, "Security.Sign.Title", 560)
    {
        _signatures = signatures;
        _paths = paths;
        _dialogs = dialogs;
        _settings = settings;
        _tsaUrl = tsaUrl.Trim();

        LoadIdentities();
        var browse = Ui.Button(loc, "Security.Sign.BrowsePfx", (_, _) => BrowsePfx());
        var create = Ui.Button(loc, "Security.Sign.CreateId", (_, _) => CreateId());
        _identity.SelectionChanged += (_, _) => _pfxPassword.IsEnabled = (_identity.SelectedItem as IdentityItem)?.PfxPath is not null;
        System.Windows.Automation.AutomationProperties.SetName(_identity, loc["Security.Sign.Identity"]);
        System.Windows.Automation.AutomationProperties.SetName(_pfxPassword, loc["Security.Sign.PfxPassword"]);

        foreach (var key in new[] { "Security.Sign.Reason.Approve", "Security.Sign.Reason.Author", "Security.Sign.Reason.Reviewed", "Security.Sign.Reason.Agree" })
            _reason.Items.Add(loc[key]);
        _reason.Text = settings.Reason;
        _location.Text = settings.Location;
        _contact.Text = settings.ContactInfo;
        _showName = Ui.Check(loc, "Security.Sign.ShowName", settings.ShowName);
        _showDate = Ui.Check(loc, "Security.Sign.ShowDate", settings.ShowDate);
        _showReason = Ui.Check(loc, "Security.Sign.ShowReason", settings.ShowReason);
        _showLocation = Ui.Check(loc, "Security.Sign.ShowLocation", settings.ShowLocation);
        _invisible = Ui.Check(loc, "Security.Sign.Invisible", !hasRectangle);
        _invisible.IsEnabled = hasRectangle;
        _image.Text = settings.ImagePath ?? string.Empty;

        _useTsa = Ui.Check(loc, "Security.Sign.UseTsa", false);
        _useTsa.IsEnabled = _tsaUrl.Length > 0;
        var tsaInfo = Ui.Plain(_tsaUrl.Length > 0 ? loc.Format("Security.Sign.TsaInfo", _tsaUrl) : loc["Security.Sign.TsaNotConfigured"], secondary: true);

        var idRow = new DockPanel();
        var idButtons = Ui.Row(browse, create);
        DockPanel.SetDock(idButtons, Dock.Right);
        idRow.Children.Add(idButtons);
        idRow.Children.Add(_identity);

        var imageRow = new DockPanel();
        var imgButtons = Ui.Row(Ui.Button(loc, "Core.Button.Browse", (_, _) => BrowseImage()), Ui.Button(loc, "Core.Button.Remove", (_, _) => _image.Text = string.Empty));
        DockPanel.SetDock(imgButtons, Dock.Right);
        imageRow.Children.Add(imgButtons);
        imageRow.Children.Add(_image);

        var body = new StackPanel();
        body.Children.Add(Ui.Group(loc, "Security.Sign.IdentityGroup", Form(
            (Ui.Text(loc, "Security.Sign.Identity"), idRow),
            (Ui.Text(loc, "Security.Sign.PfxPassword"), _pfxPassword))));
        body.Children.Add(Ui.Group(loc, "Security.Sign.DetailsGroup", Form(
            (Ui.Text(loc, "Security.Sign.ReasonLabel"), _reason),
            (Ui.Text(loc, "Security.Sign.LocationLabel"), _location),
            (Ui.Text(loc, "Security.Sign.ContactLabel"), _contact))));
        var appearance = new StackPanel();
        appearance.Children.Add(_invisible);
        var showRow = new WrapPanel();
        foreach (var c in new[] { _showName, _showDate, _showReason, _showLocation }) { c.Margin = new Thickness(0, 3, 14, 3); showRow.Children.Add(c); }
        appearance.Children.Add(showRow);
        appearance.Children.Add(Form((Ui.Text(loc, "Security.Sign.Image"), imageRow)));
        body.Children.Add(Ui.Group(loc, "Security.Sign.AppearanceGroup", appearance));
        var tsaPanel = new StackPanel();
        tsaPanel.Children.Add(_useTsa);
        tsaPanel.Children.Add(tsaInfo);
        body.Children.Add(Ui.Group(loc, "Security.Sign.TsaGroup", tsaPanel));
        body.Children.Add(Ui.Plain(loc["Security.Sign.SaveNote"], secondary: true));
        SetBody(body);
        AddOkCancel("Security.Sign.SignButton");
        _pfxPassword.IsEnabled = (_identity.SelectedItem as IdentityItem)?.PfxPath is not null;
    }

    private void LoadIdentities(string? select = null)
    {
        _identity.Items.Clear();
        try { foreach (var c in _signatures.GetStoreCertificates()) _identity.Items.Add(new IdentityItem(c)); }
        catch (CryptographicException) { }
        if (Directory.Exists(_paths.DigitalIdsDirectory))
            foreach (var f in Directory.EnumerateFiles(_paths.DigitalIdsDirectory, "*.pfx").Concat(Directory.EnumerateFiles(_paths.DigitalIdsDirectory, "*.p12")))
                _identity.Items.Add(new IdentityItem(f));
        if (_settings.LastIdentity is { } last && File.Exists(last) && _identity.Items.Cast<IdentityItem>().All(i => i.Key != last))
            _identity.Items.Add(new IdentityItem(last));
        var want = select ?? _settings.LastIdentity;
        _identity.SelectedItem = _identity.Items.Cast<IdentityItem>().FirstOrDefault(i => i.Key == want) ?? (_identity.Items.Count > 0 ? _identity.Items[0] : null);
    }

    private void BrowsePfx()
    {
        var files = _dialogs.OpenFiles(Loc["Security.Sign.BrowsePfx"], new[] { new FileFilter(Loc["Security.FileFilter.Pfx"], "*.pfx", "*.p12") });
        if (files is not { Count: > 0 }) return;
        var item = _identity.Items.Cast<IdentityItem>().FirstOrDefault(i => string.Equals(i.PfxPath, files[0], StringComparison.OrdinalIgnoreCase));
        if (item is null) { item = new IdentityItem(files[0]); _identity.Items.Add(item); }
        _identity.SelectedItem = item;
        _pfxPassword.Focus();
    }

    private void CreateId()
    {
        var dlg = new CreateDigitalIdDialog(Loc, _signatures);
        if (_dialogs.ShowDialog(dlg) == true && dlg.CreatedPath is { } path) LoadIdentities(path);
    }

    private void BrowseImage()
    {
        var files = _dialogs.OpenFiles(Loc["Security.Sign.Image"], new[] { new FileFilter(Loc["Core.FileFilter.Images"], "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tif", "*.tiff") });
        if (files is { Count: > 0 }) _image.Text = files[0];
    }

    public X509Certificate2? Certificate { get; private set; }
    public CertificateSource Source { get; private set; }
    public string? PfxPath { get; private set; }
    public bool Invisible => _invisible.IsChecked == true;
    public bool UseTsa => _useTsa.IsChecked == true && _tsaUrl.Length > 0;
    public string Reason => _reason.Text.Trim();
    public string Location => _location.Text.Trim();
    public string ContactInfo => _contact.Text.Trim();
    public SignatureAppearance Appearance { get; private set; } = new();

    protected override bool Validate()
    {
        if (_identity.SelectedItem is not IdentityItem item) { ShowError(Loc["Security.Sign.Error.NoCertificate"]); return false; }
        X509Certificate2 cert;
        if (item.PfxPath is { } pfx)
        {
            try { cert = SignatureService.LoadPfx(pfx, _pfxPassword.Password); }
            catch (CryptographicException) { ShowError(Loc["Security.Sign.Error.PfxPassword"]); return false; }
            catch (IOException ex) { ShowError(ex.Message); return false; }
            Source = CertificateSource.PfxFile;
            PfxPath = pfx;
        }
        else
        {
            cert = item.Certificate!;
            Source = CertificateSource.WindowsStore;
        }
        if (!cert.HasPrivateKey) { ShowError(Loc["Security.Sign.Error.NoPrivateKey"]); return false; }
        if (DateTime.Now > cert.NotAfter || DateTime.Now < cert.NotBefore) { ShowError(Loc["Security.Sign.Error.Expired"]); return false; }
        byte[]? image = null;
        if (_image.Text.Length > 0)
        {
            try { image = File.ReadAllBytes(_image.Text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); return false; }
        }
        Certificate = cert;
        Appearance = new SignatureAppearance
        {
            ShowName = _showName.IsChecked == true, ShowDate = _showDate.IsChecked == true,
            ShowReason = _showReason.IsChecked == true, ShowLocation = _showLocation.IsChecked == true, Image = image,
        };
        _settings.Reason = Reason;
        _settings.Location = Location;
        _settings.ContactInfo = ContactInfo;
        _settings.ShowName = Appearance.ShowName;
        _settings.ShowDate = Appearance.ShowDate;
        _settings.ShowReason = Appearance.ShowReason;
        _settings.ShowLocation = Appearance.ShowLocation;
        _settings.ImagePath = _image.Text.Length > 0 ? _image.Text : null;
        _settings.LastIdentity = item.Key;
        return true;
    }
}

/// <summary>Identyfikatory cyfrowe &gt; Utwórz: certyfikat z podpisem własnym zapisany jako PFX.</summary>
internal sealed class CreateDigitalIdDialog : SecurityDialog
{
    private readonly ISignatureService _signatures;
    private readonly TextBox _name = new(), _org = new(), _email = new();
    private readonly ComboBox _years = new();
    private readonly PasswordBox _password = new(), _confirm = new();

    public CreateDigitalIdDialog(ILocalizationService loc, ISignatureService signatures, string? defaultName = null, string? org = null, string? email = null)
        : base(loc, "Security.DigitalId.Create.Title", 460)
    {
        _signatures = signatures;
        _name.Text = defaultName ?? Environment.UserName;
        _org.Text = org ?? string.Empty;
        _email.Text = email ?? string.Empty;
        foreach (var y in new[] { 1, 2, 3, 5, 10 }) _years.Items.Add(y);
        _years.SelectedItem = 5;
        var body = new StackPanel();
        body.Children.Add(Form(
            (Ui.Text(loc, "Security.DigitalId.Name"), _name),
            (Ui.Text(loc, "Security.DigitalId.Organization"), _org),
            (Ui.Text(loc, "Security.DigitalId.Email"), _email),
            (Ui.Text(loc, "Security.DigitalId.Years"), _years),
            (Ui.Text(loc, "Security.DigitalId.Password"), _password),
            (Ui.Text(loc, "Security.Protect.Confirm"), _confirm)));
        body.Children.Add(Ui.Plain(loc["Security.DigitalId.Create.Note"], secondary: true));
        SetBody(body);
        Button? ok = null;
        ok = Ui.Button(loc, "Security.DigitalId.Create.Button", async (_, _) => await CreateAsync(ok!), isDefault: true);
        ButtonsPanel.Children.Add(ok);
        ButtonsPanel.Children.Add(Ui.Button(loc, "Core.Button.Cancel", (_, _) => DialogResult = false, isCancel: true));
    }

    public string? CreatedPath { get; private set; }

    private async Task CreateAsync(Button ok)
    {
        if (_name.Text.Trim().Length == 0) { ShowError(Loc["Security.DigitalId.Error.NameRequired"]); return; }
        if (_password.Password.Length == 0) { ShowError(Loc["Security.DigitalId.Error.PasswordRequired"]); return; }
        if (_password.Password != _confirm.Password) { ShowError(Loc["Security.Protect.Error.Mismatch"]); return; }
        ok.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            CreatedPath = await _signatures.CreateSelfSignedIdAsync(new DigitalIdRequest(_name.Text.Trim(), _org.Text.Trim(), _email.Text.Trim(), (int)_years.SelectedItem, _password.Password));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            Cursor = null;
            ok.IsEnabled = true;
        }
    }
}

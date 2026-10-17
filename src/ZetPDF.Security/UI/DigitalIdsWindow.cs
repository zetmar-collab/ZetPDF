using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.UI;

/// <summary>Identyfikatory cyfrowe: certyfikaty z magazynu Windows (z kluczem prywatnym) i pliki PFX w folderze ZetPDF.</summary>
internal sealed class DigitalIdsWindow : SecurityDialog
{
    private readonly ISignatureService _signatures;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService? _settings;
    private readonly ListBox _list = new() { Height = 200 };
    private readonly TextBlock _details;
    private readonly Button _delete;

    public DigitalIdsWindow(ILocalizationService loc, ISignatureService signatures, IAppPaths paths, IDialogService dialogs, ISettingsService? settings)
        : base(loc, "Security.DigitalId.Title", 620)
    {
        _signatures = signatures;
        _paths = paths;
        _dialogs = dialogs;
        _settings = settings;
        _details = Ui.Plain(string.Empty, secondary: true);
        _details.Margin = new Thickness(0, 8, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Security.DigitalId.Title"]);
        _list.SelectionChanged += (_, _) => ShowDetails();
        _delete = Ui.Button(loc, "Security.DigitalId.Delete", (_, _) => Delete());
        var buttons = Ui.Row(
            Ui.Button(loc, "Security.DigitalId.Create", (_, _) => Create()),
            Ui.Button(loc, "Security.DigitalId.Import", (_, _) => Import()),
            _delete);
        var body = new StackPanel();
        body.Children.Add(Ui.Plain(loc["Security.DigitalId.Intro"], secondary: true));
        body.Children.Add(_list);
        body.Children.Add(buttons);
        body.Children.Add(_details);
        SetBody(body);
        AddClose();
        Reload();
    }

    private void Reload(string? select = null)
    {
        _list.Items.Clear();
        try { foreach (var c in _signatures.GetStoreCertificates()) _list.Items.Add(new IdentityItem(c)); }
        catch (CryptographicException) { }
        if (Directory.Exists(_paths.DigitalIdsDirectory))
            foreach (var f in Directory.EnumerateFiles(_paths.DigitalIdsDirectory, "*.pfx").Concat(Directory.EnumerateFiles(_paths.DigitalIdsDirectory, "*.p12")))
                _list.Items.Add(new IdentityItem(f));
        _list.SelectedItem = _list.Items.Cast<IdentityItem>().FirstOrDefault(i => i.Key == select) ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
        ShowDetails();
    }

    private void ShowDetails()
    {
        var item = _list.SelectedItem as IdentityItem;
        _delete.IsEnabled = item?.PfxPath is not null;
        if (item is null) { _details.Text = Loc["Security.DigitalId.Empty"]; return; }
        if (item.Certificate is { } c)
        {
            var culture = Loc.CurrentCulture;
            _details.Text = string.Join(Environment.NewLine,
                Loc.Format("Security.DigitalId.Detail.Subject", c.Subject),
                Loc.Format("Security.DigitalId.Detail.Issuer", c.Issuer),
                Loc.Format("Security.DigitalId.Detail.Validity", c.NotBefore.ToString("d", culture) + " – " + c.NotAfter.ToString("d", culture)),
                Loc.Format("Security.DigitalId.Detail.Thumbprint", c.Thumbprint),
                Loc["Security.DigitalId.Detail.Store"]);
        }
        else _details.Text = Loc.Format("Security.DigitalId.Detail.File", item.PfxPath);
    }

    private void Create()
    {
        var s = _settings?.Current;
        var dlg = new CreateDigitalIdDialog(Loc, _signatures, s?.AuthorName, s?.Organization, s?.Email);
        if (_dialogs.ShowDialog(dlg) == true && dlg.CreatedPath is { } p) Reload(p);
    }

    private void Import()
    {
        var files = _dialogs.OpenFiles(Loc["Security.DigitalId.Import"], new[] { new FileFilter(Loc["Security.FileFilter.Pfx"], "*.pfx", "*.p12") });
        if (files is not { Count: > 0 }) return;
        try
        {
            Directory.CreateDirectory(_paths.DigitalIdsDirectory);
            var target = Path.Combine(_paths.DigitalIdsDirectory, Path.GetFileName(files[0]));
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(_paths.DigitalIdsDirectory, Path.GetFileNameWithoutExtension(files[0]) + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + Path.GetExtension(files[0]));
            File.Copy(files[0], target);
            Reload(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _dialogs.ShowError(ex); }
    }

    private void Delete()
    {
        if (_list.SelectedItem is not IdentityItem { PfxPath: { } path }) return;
        if (!_dialogs.Confirm(Loc.Format("Security.DigitalId.DeleteConfirm", Path.GetFileName(path)), Loc["Security.DigitalId.Title"])) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _dialogs.ShowError(ex); }
        Reload();
    }
}

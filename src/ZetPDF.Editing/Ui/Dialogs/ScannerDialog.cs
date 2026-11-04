using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Scanning;

namespace ZetPDF.Editing.Ui.Dialogs;

/// <summary>Ustawienia skanowania: urządzenie, rozdzielczość, kolor, podajnik, OCR od razu.</summary>
internal sealed class ScannerDialog : DialogBase
{
    private static readonly int[] Resolutions = { 100, 150, 200, 300, 400, 600 };
    private readonly ComboBox _device = new() { Margin = new Thickness(0, 3, 0, 3) };
    private readonly ComboBox _dpi = new() { Margin = new Thickness(0, 3, 0, 3), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _mode;
    private readonly CheckBox _feeder;
    private readonly CheckBox _ocr;
    private readonly TextBlock _feederNote;

    public ScannerDialog(ILocalizationService loc, IReadOnlyList<ScannerInfo> devices) : base(loc, "Edit.Scan.Title", 480)
    {
        foreach (var d in devices) _device.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d });
        _device.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(_device, loc["Edit.Scan.Device"]);
        foreach (var r in Resolutions) _dpi.Items.Add(new ComboBoxItem { Content = r.ToString(CultureInfo.CurrentCulture), Tag = r });
        _dpi.SelectedIndex = Array.IndexOf(Resolutions, 300);
        System.Windows.Automation.AutomationProperties.SetName(_dpi, loc["Edit.Scan.Dpi"]);
        _mode = Ui.Combo(loc, new[] { (ScanColorMode.Color, "Edit.Scan.Mode.Color"), (ScanColorMode.Gray, "Edit.Scan.Mode.Gray"), (ScanColorMode.Mono, "Edit.Scan.Mode.Mono") }, ScanColorMode.Color);
        System.Windows.Automation.AutomationProperties.SetName(_mode, loc["Edit.Scan.Mode"]);
        _feeder = Ui.Check(loc, "Edit.Scan.Feeder", false);
        _ocr = Ui.Check(loc, "Edit.Scan.Ocr", false);
        _feederNote = Ui.Label(loc, "Edit.Scan.NoFeederNote", secondary: true);
        _device.SelectionChanged += (_, _) => UpdateFeeder();
        UpdateFeeder();
        SetBody(Ui.Form(
            (Ui.Label(loc, "Edit.Scan.Device"), _device),
            (Ui.Label(loc, "Edit.Scan.Dpi"), _dpi),
            (Ui.Label(loc, "Edit.Scan.Mode"), _mode),
            (null, _feeder),
            (null, _feederNote),
            (null, _ocr)));
        AddOkCancel("Edit.Scan.Start");
    }

    private ScannerInfo? Selected => (_device.SelectedItem as ComboBoxItem)?.Tag as ScannerInfo;

    private void UpdateFeeder()
    {
        bool has = Selected?.HasFeeder == true;
        _feeder.IsEnabled = has;
        _feeder.IsChecked = has;
        _feederNote.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool RunOcr => _ocr.IsChecked == true;

    public ScanSettings Settings => new(Selected!.Id, (int)((ComboBoxItem)_dpi.SelectedItem).Tag, Ui.Selected<ScanColorMode>(_mode), _feeder.IsChecked == true);

    protected override bool Validate() => Selected is not null;
}

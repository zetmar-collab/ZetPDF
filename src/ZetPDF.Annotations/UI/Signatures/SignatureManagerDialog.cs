using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI.Signatures;

/// <summary>Menedżer zapisanych podpisów: podgląd, nowy, usuń, użyj.</summary>
internal sealed class SignatureManagerDialog : Window
{
    private readonly SignatureStore _store;
    private readonly ListBox _list;
    private readonly ILocalizationService _loc;
    private readonly IDialogService? _dialogs;
    private readonly string _author;

    public SignatureManagerDialog(ILocalizationService loc, IDialogService? dialogs, SignatureStore store, string author)
    {
        _loc = loc;
        _dialogs = dialogs;
        _store = store;
        _author = author;
        Ui.SetupDialog(this, loc, "Annot.Sign.ManageTitle");
        var root = new DockPanel { Margin = new Thickness(16), Width = 460, Height = 420 };
        var buttons = Ui.ButtonRow(
            Ui.Button(loc, "Annot.Sign.New", (_, _) => New()),
            Ui.Button(loc, "Annot.Sign.Delete", (_, _) => Delete()),
            Ui.Button(loc, "Annot.Sign.Use", (_, _) => Use(), isDefault: true),
            Ui.Button(loc, "Annot.Dialog.Close", (_, _) => DialogResult = false, isCancel: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        _list = new ListBox();
        System.Windows.Automation.AutomationProperties.SetName(_list, loc["Annot.Sign.ManageTitle"]);
        _list.MouseDoubleClick += (_, _) => Use();
        root.Children.Add(_list);
        Content = root;
        Reload();
    }

    public string? SelectedPath { get; private set; }

    private void Reload()
    {
        _list.Items.Clear();
        foreach (var path in _store.List())
        {
            var img = new Image { Height = 60, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4) };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                img.Source = bmp;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { continue; }
            _list.Items.Add(new ListBoxItem { Content = img, Tag = path, ToolTip = Path.GetFileName(path) });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void New()
    {
        var dlg = new SignatureDialog(_loc, _dialogs, _author);
        if (Ui.Show(_dialogs, dlg) == true && dlg.Png is { } png)
        {
            _store.Save(png);
            Reload();
        }
    }

    private void Delete()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: string path }) return;
        if (_dialogs is not null && !_dialogs.Confirm(_loc["Annot.Sign.DeleteConfirm"])) return;
        _store.Delete(path);
        Reload();
    }

    private void Use()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: string path }) return;
        SelectedPath = path;
        DialogResult = true;
    }
}

using System.Diagnostics;
using System.Windows;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Dialogs;

/// <summary>Okno Ustawień (Ctrl+K). Język i motyw zmieniają się na żywo; OK/Zastosuj zapisuje, Anuluj przywraca.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm, IDialogService dialogs, ILocalizationService loc, IAppPaths paths, Action showShortcuts)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        LogFolderBox.Text = paths.LogDirectory;

        var radios = new[] { (ThemeSystem, AppTheme.System), (ThemeLight, AppTheme.Light), (ThemeDark, AppTheme.Dark) };
        foreach (var (radio, theme) in radios)
        {
            radio.IsChecked = vm.Theme == theme;
            radio.Checked += (_, _) => vm.Theme = theme;
        }

        OkButton.Click += (_, _) =>
        {
            if (!vm.Apply()) return;
            DialogResult = true;
        };
        ApplyButton.Click += (_, _) =>
        {
            vm.Apply();
        };
        BrowseTempButton.Click += (_, _) =>
        {
            var folder = dialogs.SelectFolder(loc["Shell.Settings.TempFolder"], string.IsNullOrWhiteSpace(vm.TempFolder) ? null : vm.TempFolder);
            if (folder is not null) vm.TempFolder = folder;
        };
        OpenLogButton.Click += (_, _) => OpenFolder(paths.LogDirectory);
        ShortcutsButton.Click += (_, _) => showShortcuts();
        Closed += (_, _) =>
        {
            // Anuluj / krzyżyk: przywróć język i motyw z ostatniego zapisu (OK/Zastosuj przesuwa punkt odniesienia).
            vm.Revert();
            vm.Detach();
        };
    }

    internal static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception)
        {
            // Eksplorator niedostępny.
        }
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using ZetPDF.App.Licenses;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Dialogs;

/// <summary>Okno "O programie": wersja 0.3.0-test, biblioteki open source i licencje, informacja o braku telemetrii.</summary>
public partial class AboutWindow : Window
{
    private readonly ILocalizationService _loc;

    public AboutWindow(ILocalizationService loc, Action<ThirdPartyLibraries.Library?> showLicenses)
    {
        InitializeComponent();
        _loc = loc;
        LibrariesGrid.ItemsSource = ThirdPartyLibraries.All;
        LibrariesGrid.MouseDoubleClick += (_, _) => showLicenses(LibrariesGrid.SelectedItem as ThirdPartyLibraries.Library);
        LicensesButton.Click += (_, _) => showLicenses(LibrariesGrid.SelectedItem as ThirdPartyLibraries.Library);
        UpdateTexts();
        loc.CultureChanged += OnCulture;
        Closed += (_, _) => loc.CultureChanged -= OnCulture;
    }

    private void OnCulture(object? sender, EventArgs e) => UpdateTexts();

    private void UpdateTexts()
    {
        VersionText.Text = _loc.Format("Shell.About.Version", ThirdPartyLibraries.AppVersion);
        EnvironmentText.Text = _loc.Format("Shell.About.Environment", RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture);
    }
}

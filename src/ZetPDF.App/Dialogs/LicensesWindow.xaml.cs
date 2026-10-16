using System.Windows;
using ZetPDF.App.Licenses;

namespace ZetPDF.App.Dialogs;

/// <summary>Licencje bibliotek: lista bibliotek i pełny tekst licencji (z prawami autorskimi) wybranej pozycji.</summary>
public partial class LicensesWindow : Window
{
    public LicensesWindow(ThirdPartyLibraries.Library? selected = null)
    {
        InitializeComponent();
        LibraryList.ItemsSource = ThirdPartyLibraries.All;
        LibraryList.SelectionChanged += (_, _) =>
        {
            if (LibraryList.SelectedItem is ThirdPartyLibraries.Library lib)
            {
                LicenseText.Text = lib.FullText.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
                LicenseText.ScrollToHome();
            }
        };
        LibraryList.SelectedItem = selected ?? ThirdPartyLibraries.All[0];
        CopyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine + new string('=', 64) + Environment.NewLine + Environment.NewLine,
                    ThirdPartyLibraries.All.Select(l => l.FullText.Replace("\n", Environment.NewLine, StringComparison.Ordinal))));
            }
            catch (Exception)
            {
                // Schowek zajęty.
            }
        };
    }
}

using System.Windows;
using ZetPDF.App.Shell;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Dialogs;

/// <summary>Lista skrótów klawiszowych (F1) z filtrem – z deskryptorów poleceń (katalog + moduły), w bieżącym języku.</summary>
public partial class ShortcutsWindow : Window
{
    public sealed record Row(string Label, string Keys, string SingleKey);

    private readonly ICommandRegistry _registry;
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;

    public ShortcutsWindow(ICommandRegistry registry, ILocalizationService loc, ISettingsService settings)
    {
        InitializeComponent();
        _registry = registry;
        _loc = loc;
        _settings = settings;
        FilterBox.TextChanged += (_, _) => Refresh();
        loc.CultureChanged += OnCulture;
        Closed += (_, _) => loc.CultureChanged -= OnCulture;
        Loaded += (_, _) => FilterBox.Focus();
        Refresh();
    }

    private void OnCulture(object? sender, EventArgs e) => Refresh();

    /// <summary>Wiersze listy (bez duplikatów id), posortowane po nazwie polecenia.</summary>
    public static IReadOnlyList<Row> BuildRows(ICommandRegistry registry, ILocalizationService loc, string? filter)
    {
        var f = CommandSearch.Normalize(filter);
        return registry.Descriptors
            .GroupBy(d => d.Id).Select(g => g.First())
            .Where(d => d.Shortcuts.Count > 0 || !string.IsNullOrWhiteSpace(d.SingleKey))
            .Select(d => new Row(loc[d.TextKey], ShortcutMap.Describe(d, includeSingleKey: false), d.SingleKey.Trim().ToUpperInvariant()))
            .Where(r => f.Length == 0 || CommandSearch.Normalize(r.Label).Contains(f, StringComparison.Ordinal) || CommandSearch.Normalize(r.Keys).Contains(f, StringComparison.Ordinal))
            .OrderBy(r => r.Label, StringComparer.Create(loc.CurrentCulture, ignoreCase: true))
            .ToList();
    }

    private void Refresh()
    {
        ShortcutGrid.ItemsSource = BuildRows(_registry, _loc, FilterBox.Text);
        SingleKeyState.Text = _loc[_settings.Current.SingleKeyShortcuts ? "Shell.Shortcuts.SingleKeyOn" : "Shell.Shortcuts.SingleKeyOff"];
    }
}

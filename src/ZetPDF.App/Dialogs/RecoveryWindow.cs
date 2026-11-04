using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Dialogs;

/// <summary>Lista kopii odzyskiwania po awarii. Zamknięcie okna bez decyzji zostawia kopie do następnego uruchomienia.</summary>
internal sealed class RecoveryWindow : Window
{
    private readonly ILocalizationService _loc;
    private readonly Func<RecoveryInfo, Task<bool>> _recover;
    private readonly Func<RecoveryInfo, bool> _discard;
    private readonly StackPanel _rows = new();

    public RecoveryWindow(ILocalizationService loc, IReadOnlyList<RecoveryInfo> entries, Func<RecoveryInfo, Task<bool>> recover, Func<RecoveryInfo, bool> discard)
    {
        _loc = loc;
        _recover = recover;
        _discard = discard;
        Title = loc["Shell.Recovery.Title"];
        Width = 620;
        Height = 420;
        MinWidth = 460;
        MinHeight = 280;
        AutomationProperties.SetAutomationId(this, "Shell.RecoveryWindow");

        var close = new Button { Content = loc["Shell.Recovery.Later"], IsCancel = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var footer = new Border { Padding = new Thickness(16, 12, 16, 12), Child = close };
        footer.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
        DockPanel.SetDock(footer, Dock.Bottom);

        var intro = new TextBlock { Text = loc["Shell.Recovery.Intro"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 14, 16, 10) };
        DockPanel.SetDock(intro, Dock.Top);

        foreach (var entry in entries) _rows.Children.Add(CreateRow(entry));
        var list = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows, Margin = new Thickness(16, 0, 16, 12) };

        Content = new DockPanel { Children = { footer, intro, list } };
    }

    /// <summary>Liczba kopii, o których użytkownik jeszcze nie zdecydował.</summary>
    internal int RemainingCount => _rows.Children.Count;

    private FrameworkElement CreateRow(RecoveryInfo entry)
    {
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = entry.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.OriginalPath is { } path) details.Children.Add(Secondary(path));
        var state = _loc[entry.State == RecoveryDocumentState.NeverSaved ? "Shell.Recovery.State.NeverSaved" : "Shell.Recovery.State.Modified"];
        var saved = _loc.Format("Shell.Recovery.Saved", entry.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        details.Children.Add(Secondary(state + " · " + saved));
        if (entry.IsProtected) details.Children.Add(Secondary(_loc["Shell.Recovery.Protected"]));

        var row = new Border { Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
        row.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Border);

        var recover = new Button { Content = _loc["Shell.Recovery.Recover"], MinWidth = 88, IsDefault = _rows.Children.Count == 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var discard = new Button { Content = _loc["Shell.Recovery.Discard"], MinWidth = 88, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(recover, _loc["Shell.Recovery.Recover"] + ": " + entry.DisplayName);
        AutomationProperties.SetName(discard, _loc["Shell.Recovery.Discard"] + ": " + entry.DisplayName);
        recover.Click += async (_, _) =>
        {
            IsEnabled = false; // odzyskiwanie może trwać i pytać o hasło
            try
            {
                if (await _recover(entry)) RemoveRow(row);
            }
            finally
            {
                IsEnabled = true;
            }
        };
        discard.Click += (_, _) =>
        {
            if (_discard(entry)) RemoveRow(row);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { recover, discard } };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Child = new DockPanel { Children = { buttons, details } };
        return row;
    }

    private TextBlock Secondary(string text)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, ToolTip = text };
        t.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        return t;
    }

    private void RemoveRow(FrameworkElement row)
    {
        _rows.Children.Remove(row);
        if (_rows.Children.Count == 0) Close();
    }
}

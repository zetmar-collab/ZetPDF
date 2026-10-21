using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.ReadAloud;

/// <summary>
/// Małe okno narzędziowe czytania na głos (niemodalne): głos, głośność, szybkość, przyciski czytania/pauzy/stopu.
/// Zamknięcie okna wyłącza tryb czytania.
/// </summary>
internal sealed class ReadAloudPanel : Window
{
    private readonly ReadAloudService _service;
    private readonly ILocalizationService _loc;
    private readonly ICommandRegistry _registry;
    private readonly ComboBox _voices;
    private readonly TextBlock _state;

    public ReadAloudPanel(ReadAloudService service, ILocalizationService loc, ICommandRegistry registry)
    {
        _service = service;
        _loc = loc;
        _registry = registry;
        this.Loc(TitleProperty, loc, "Viewer.Read.Title");
        Width = 380; // pełna polska etykieta „Automatycznie (język dokumentu lub interfejsu)” w polu głosu (recenzent)
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        this.Themed(BackgroundProperty, ThemeKeys.SurfaceRaised).Themed(ForegroundProperty, ThemeKeys.TextPrimary);

        var settings = service.Settings;
        _voices = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(_voices, loc["Viewer.Read.Voice"]);
        var auto = new ComboBoxItem { Tag = null }.Loc(ContentControl.ContentProperty, loc, "Viewer.Read.VoiceAuto");
        _voices.Items.Add(auto);
        foreach (var v in service.Voices)
        {
            var item = new ComboBoxItem { Tag = v.Name, Content = v.Name + " (" + v.Culture.Name + ")" };
            _voices.Items.Add(item);
            if (v.Name == settings.Voice) _voices.SelectedItem = item;
        }
        if (_voices.SelectedItem is null) _voices.SelectedIndex = 0;
        _voices.SelectionChanged += (_, _) =>
        {

            var name = (_voices.SelectedItem as ComboBoxItem)?.Tag as string;
            _service.UpdateSettings(s => s.Voice = name);
        };

        var volume = SliderRow(0, 100, settings.Volume, 10, v => _service.UpdateSettings(s => s.Volume = v));
        var rate = SliderRow(-10, 10, settings.Rate, 1, v => _service.UpdateSettings(s => s.Rate = v));

        var buttons = new UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(CommandButton("", "Viewer.Read.Button.Page", CommandIds.Read.CurrentPage));
        buttons.Children.Add(CommandButton("", "Viewer.Read.Button.FromPage", CommandIds.Read.FromPage));
        buttons.Children.Add(CommandButton("", "Viewer.Read.Button.Pause", CommandIds.Read.Pause));
        buttons.Children.Add(CommandButton("", "Viewer.Read.Button.Stop", CommandIds.Read.Stop));

        _state = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        _state.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Ui.Label(loc, "Viewer.Read.Voice", 0));
        root.Children.Add(_voices);
        root.Children.Add(Ui.Label(loc, "Viewer.Read.Volume", 0));
        root.Children.Add(volume);
        root.Children.Add(Ui.Label(loc, "Viewer.Read.Rate", 0));
        root.Children.Add(rate);
        root.Children.Add(buttons);
        root.Children.Add(_state);
        Content = root;

        _service.StateChanged += OnStateChanged;
        _loc.CultureChanged += OnCultureChanged;
        Closed += (_, _) =>
        {
            _service.StateChanged -= OnStateChanged;
            _loc.CultureChanged -= OnCultureChanged;
        };
        UpdateState();
    }

    private Slider SliderRow(int min, int max, int value, int tick, Action<int> changed)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = tick, IsSnapToTickEnabled = true, TickPlacement = TickPlacement.BottomRight, Margin = new Thickness(0, 2, 0, 6), SmallChange = 1, LargeChange = tick };
        s.ValueChanged += (_, e) => { changed((int)Math.Round(e.NewValue)); };
        return s;
    }

    private Button CommandButton(string glyph, string tipKey, string commandId)
    {
        var b = Ui.IconButton(glyph, _loc, tipKey, (_, _) => { });
        b.Command = _registry.GetHandler(commandId);
        b.Height = 32;
        return b;
    }

    private void OnCultureChanged(object? sender, EventArgs e) => UpdateState();
    private void OnStateChanged(object? sender, EventArgs e) => UpdateState();

    private void UpdateState()
    {
        _state.Text = _service.State switch
        {
            ReadAloudState.Speaking => _loc.Format("Viewer.Read.State.Speaking", _service.CurrentPage + 1, _service.VoiceInUse ?? "?"),
            ReadAloudState.Paused => _loc.Format("Viewer.Read.State.Paused", _service.CurrentPage + 1),
            _ => _loc[_service.Voices.Count == 0 ? "Viewer.Read.Unavailable" : "Viewer.Read.State.Stopped"],
        };
    }
}

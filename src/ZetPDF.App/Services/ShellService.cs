using System.Windows;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Services;

/// <summary>
/// Implementacja <see cref="IShellService"/>: przechowuje stan powłoki (tryb odczytu, pełny ekran, panel boczny,
/// zwinięta wstążka) i powiadamia okno główne, które stosuje zmiany w interfejsie.
/// Kontrakt z renderer-viewer: polecenia View.ReadingMode / View.FullScreen (moduł Viewer) ustawiają
/// <see cref="IsReadingMode"/> / <see cref="IsFullScreen"/>; powłoka chowa wstążkę, panele itd.
/// Jeśli Viewer nie zarejestruje tych poleceń, powłoka rejestruje własne (ShellCommands).
/// </summary>
public sealed class ShellService : IShellService
{
    private bool _readingMode;
    private bool _fullScreen;
    private bool _sidePanelVisible = true;
    private bool _ribbonMinimized;

    public Window MainWindow => Application.Current.MainWindow;

    public bool IsReadingMode
    {
        get => _readingMode;
        set => Set(ref _readingMode, value);
    }

    public bool IsFullScreen
    {
        get => _fullScreen;
        set => Set(ref _fullScreen, value);
    }

    public bool IsSidePanelVisible
    {
        get => _sidePanelVisible;
        set => Set(ref _sidePanelVisible, value);
    }

    public bool IsRibbonMinimized
    {
        get => _ribbonMinimized;
        set => Set(ref _ribbonMinimized, value);
    }

    public event EventHandler? ShellStateChanged;

    /// <summary>Komunikat paska stanu (null = domyślny).</summary>
    public event EventHandler<string?>? StatusRequested;

    /// <summary>Prośba o pokazanie karty kontekstowej (TabId, Visible).</summary>
    public event EventHandler<(string TabId, bool Visible)>? ContextualTabRequested;

    /// <summary>Prośba o pokazanie panelu bocznego o danym id.</summary>
    public event EventHandler<string>? PanelRequested;

    public void ShowPanel(string panelId) => PanelRequested?.Invoke(this, panelId);

    public void SetContextualTabVisible(string tabId, bool visible) => ContextualTabRequested?.Invoke(this, (tabId, visible));

    public void SetStatus(string? text) => StatusRequested?.Invoke(this, text);

    private void Set(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        ShellStateChanged?.Invoke(this, EventArgs.Empty);
    }
}

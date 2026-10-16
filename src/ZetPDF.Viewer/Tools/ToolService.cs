using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.Tools;

/// <summary>
/// Aktywne narzędzie (jedno na aplikację). Przy zmianie aktywnego widoku poprzedni widok dostaje
/// OnDeactivated, nowy OnActivated – narzędzia modułów nie muszą śledzić kart same.
/// </summary>
public sealed class ToolService : IToolService
{
    private readonly HandTool _default;
    private IDocumentView? _view;

    public ToolService(HandTool hand, IViewerService viewer)
    {
        _default = hand;
        ActiveTool = hand;
        _view = viewer.ActiveView;
        viewer.ActiveViewChanged += (_, v) =>
        {
            if (ReferenceEquals(v, _view)) return;
            if (_view is not null) SafeDeactivate(ActiveTool, _view);
            _view = v;
            if (_view is not null) ActiveTool.OnActivated(_view);
        };
    }

    public IViewerTool ActiveTool { get; private set; }
    public event EventHandler? ActiveToolChanged;

    public void Activate(IViewerTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (ReferenceEquals(tool, ActiveTool))
        {
            ActiveToolChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var old = ActiveTool;
        if (_view is not null) SafeDeactivate(old, _view);
        ActiveTool = tool;
        if (_view is not null) tool.OnActivated(_view);
        ActiveToolChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ActivateDefault() => Activate(_default);

    public bool IsActive(string toolId) => string.Equals(ActiveTool.Id, toolId, StringComparison.Ordinal);

    private static void SafeDeactivate(IViewerTool tool, IDocumentView view)
    {
        try { tool.OnDeactivated(view); }
        catch (ObjectDisposedException) { /* widok zamknięty */ }
    }
}

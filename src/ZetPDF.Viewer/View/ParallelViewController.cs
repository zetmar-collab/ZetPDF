using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.Viewer.View;

/// <summary>
/// Widok równoległy: drugi otwarty dokument pokazany obok (lub pod) dokumentem aktywnej karty – w tym samym oknie,
/// wewnątrz widoku karty (host kart należy do powłoki, więc para żyje w <see cref="DocumentView"/>).
/// Drugi dokument ma własny widok PDFium (podgląd, narzędzie Ręka). Opcjonalna synchronizacja przewijania i powiększenia.
/// </summary>
public sealed class ParallelViewController
{
    private readonly IServiceProvider _services;
    private readonly ILocalizationService _loc;
    private readonly Dictionary<DocumentView, ParallelPair> _pairs = new();

    public ParallelViewController(IServiceProvider services)
    {
        _services = services;
        _loc = services.GetRequiredService<ILocalizationService>();
        services.GetRequiredService<IDocumentManager>().DocumentClosed += OnDocumentClosed;
    }

    /// <summary>Zmiana par lub synchronizacji (odświeżenie przełączników wstążki).</summary>
    public event EventHandler? Changed;

    internal ParallelPair? GetPair(DocumentView? primary) => primary is not null && _pairs.TryGetValue(primary, out var p) ? p : null;

    internal ParallelPair Open(DocumentView primary, IPdfDocumentSession other, Orientation orientation)
    {
        Close(primary);
        var companion = new DocumentView(other, _services) { IsCompanion = true };
        var pair = new ParallelPair(primary, companion, orientation);
        primary.SetSideContent(BuildSide(pair), orientation);
        _pairs[primary] = pair;
        Changed?.Invoke(this, EventArgs.Empty);
        return pair;
    }

    internal void Close(DocumentView primary)
    {
        if (!_pairs.Remove(primary, out var pair)) return;
        pair.Sync = false;
        primary.SetSideContent(null, pair.Orientation);
        pair.Companion.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetSync(ParallelPair pair, bool on)
    {
        pair.Sync = on;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private FrameworkElement BuildSide(ParallelPair pair)
    {
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextPrimary);
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(IPdfDocumentSession.DisplayName)) { Source = pair.Companion.Session, Mode = System.Windows.Data.BindingMode.OneWay });
        var close = Ui.IconButton("", _loc, "Viewer.Parallel.Close", (_, _) => Close(pair.Primary));
        var header = new DockPanel { Height = 28, LastChildFill = true };
        header.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.Surface);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(pair.Companion.Element);
        System.Windows.Automation.AutomationProperties.SetName(root, pair.Companion.Session.DisplayName);
        return root;
    }

    private void OnDocumentClosed(object? sender, IPdfDocumentSession session)
    {
        foreach (var pair in _pairs.Values.Where(p => ReferenceEquals(p.Primary.Session, session) || ReferenceEquals(p.Companion.Session, session)).ToList())
            Close(pair.Primary);
    }
}

/// <summary>Para widoków (dokument karty + drugi dokument) z synchronizacją przewijania (przyrostowo) i powiększenia.</summary>
internal sealed class ParallelPair
{
    private bool _sync;
    private bool _busy;
    private Point _lastPrimary;
    private Point _lastCompanion;

    public ParallelPair(DocumentView primary, DocumentView companion, Orientation orientation)
    {
        Primary = primary;
        Companion = companion;
        Orientation = orientation;
    }

    public DocumentView Primary { get; }
    public DocumentView Companion { get; }
    public Orientation Orientation { get; }

    public bool Sync
    {
        get => _sync;
        set
        {
            if (_sync == value) return;
            _sync = value;
            if (value)
            {
                Primary.Scrolled += OnPrimaryScrolled;
                Companion.Scrolled += OnCompanionScrolled;
                Primary.ViewStateChanged += OnPrimaryState;
                Companion.ViewStateChanged += OnCompanionState;
                Run(() => { if (Companion.IsDocumentLoaded) Companion.Zoom = Primary.Zoom; });
            }
            else
            {
                Primary.Scrolled -= OnPrimaryScrolled;
                Companion.Scrolled -= OnCompanionScrolled;
                Primary.ViewStateChanged -= OnPrimaryState;
                Companion.ViewStateChanged -= OnCompanionState;
            }
        }
    }

    private void Run(Action action)
    {
        _busy = true;
        try { action(); }
        finally
        {
            _busy = false;
            _lastPrimary = Primary.ScrollOffset;
            _lastCompanion = Companion.ScrollOffset;
        }
    }

    private void OnPrimaryScrolled(object? sender, EventArgs e) => Follow(Primary, Companion, ref _lastPrimary);
    private void OnCompanionScrolled(object? sender, EventArgs e) => Follow(Companion, Primary, ref _lastCompanion);

    private void Follow(DocumentView source, DocumentView target, ref Point last)
    {
        if (_busy) return;
        var now = source.ScrollOffset;
        var delta = now - last;
        last = now;
        if (delta.X == 0 && delta.Y == 0) return;
        Run(() => target.ScrollTo(target.ScrollOffset + delta));
    }

    private void OnPrimaryState(object? sender, EventArgs e) => FollowZoom(Primary, Companion);
    private void OnCompanionState(object? sender, EventArgs e) => FollowZoom(Companion, Primary);

    private void FollowZoom(DocumentView source, DocumentView target)
    {
        if (_busy || !target.IsDocumentLoaded || !source.IsDocumentLoaded) return;
        if (Math.Abs(source.Zoom - target.Zoom) < 1e-6 && source.ViewRotation == target.ViewRotation) return;
        Run(() =>
        {
            target.Zoom = source.Zoom;
            target.ViewRotation = source.ViewRotation;
        });
    }
}

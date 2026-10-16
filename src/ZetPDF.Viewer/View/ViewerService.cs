using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.View;

/// <summary>
/// Śledzi widoki sesji i aktywny widok (na podstawie <see cref="IDocumentManager.ActiveDocument"/>).
/// Widok zamykanego dokumentu jest zwalniany (uchwyty PDFium zamykane deterministycznie).
/// </summary>
public sealed class ViewerService : IViewerService
{
    private readonly Dictionary<IPdfDocumentSession, IDocumentView> _views = new();
    private readonly IDocumentManager _documents;

    public ViewerService(IDocumentManager documents)
    {
        _documents = documents;
        documents.ActiveDocumentChanged += (_, d) => SetActive(d is null ? null : GetView(d));
        documents.DocumentClosed += (_, d) =>
        {
            if (!_views.Remove(d, out var v)) return;
            if (ReferenceEquals(ActiveView, v)) SetActive(null);
            v.Dispose();
        };
    }

    public IDocumentView? ActiveView { get; private set; }
    public event EventHandler<IDocumentView?>? ActiveViewChanged;

    public IDocumentView? GetView(IPdfDocumentSession session) => _views.TryGetValue(session, out var v) ? v : null;

    /// <summary>Wszystkie żywe widoki (testy, diagnostyka).</summary>
    public IReadOnlyCollection<IDocumentView> Views => _views.Values;

    internal void Track(IDocumentView view)
    {
        if (_views.TryGetValue(view.Session, out var old) && !ReferenceEquals(old, view)) old.Dispose();
        _views[view.Session] = view;
        if (ReferenceEquals(_documents.ActiveDocument, view.Session)) SetActive(view);
    }

    private void SetActive(IDocumentView? view)
    {
        if (ReferenceEquals(view, ActiveView)) return;
        ActiveView = view;
        ActiveViewChanged?.Invoke(this, view);
    }
}

/// <summary>Tworzy <see cref="DocumentView"/> dla sesji i rejestruje go w <see cref="ViewerService"/>.</summary>
public sealed class DocumentViewFactory : IDocumentViewFactory
{
    private readonly ViewerService _viewer;
    private readonly IServiceProvider _services;

    public DocumentViewFactory(ViewerService viewer, IServiceProvider services)
    {
        _viewer = viewer;
        _services = services;
    }

    public IDocumentView CreateView(IPdfDocumentSession session)
    {
        var view = new DocumentView(session, _services);
        _viewer.Track(view);
        return view;
    }
}

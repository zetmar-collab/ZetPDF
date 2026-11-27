using Microsoft.Extensions.Logging;
using ZetPDF.Core.Contracts;

namespace ZetPDF.App.Shell;

/// <summary>
/// Przywracanie sesji (opcja w Ustawieniach, domyślnie wyłączona): przy zamykaniu programu zapamiętuje otwarte pliki, bieżącą
/// stronę i aktywną kartę; przy starcie bez plików w linii poleceń otwiera je ponownie. Dokumenty bez pliku i pliki, których już
/// nie ma, są pomijane. Hasła nie są zapamiętywane – plik zaszyfrowany zapyta o nie jak przy zwykłym otwieraniu.
/// </summary>
public sealed class SessionRestore
{
    private readonly IDocumentManager _documents;
    private readonly ISettingsService _settings;
    private readonly IServiceProvider _services;
    private readonly ILogger<SessionRestore> _log;

    public SessionRestore(IDocumentManager documents, ISettingsService settings, IServiceProvider services, ILogger<SessionRestore> log)
    {
        _documents = documents;
        _settings = settings;
        _services = services;
        _log = log;
    }

    private IViewerService? Viewer => _services.GetService(typeof(IViewerService)) as IViewerService;

    /// <summary>Zapisuje bieżące karty (przy zamykaniu programu). Wyłączona opcja czyści listę.</summary>
    public void Capture()
    {
        var entries = new List<SessionEntry>();
        if (_settings.Current.RestoreSession)
        {
            var viewer = Viewer;
            foreach (var d in _documents.Documents)
            {
                if (d.FilePath is not { } path) continue;
                int page = 0;
                try { page = viewer?.GetView(d)?.CurrentPageIndex ?? 0; }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _log.LogDebug(ex, "No view for session entry"); }
                entries.Add(new SessionEntry { Path = path, Page = Math.Max(0, page), Active = ReferenceEquals(d, _documents.ActiveDocument) });
                if (entries.Count >= AppSettings.MaxSessionEntries) break;
            }
        }
        _settings.Update(s => s.LastSession = entries);
    }

    /// <summary>Otwiera pliki z ostatniej sesji, przechodzi do zapamiętanych stron i aktywuje zapamiętaną kartę.</summary>
    public async Task RestoreAsync(Func<IEnumerable<string>, Task> open)
    {
        var s = _settings.Current;
        if (!s.RestoreSession || s.LastSession.Count == 0) return;
        var entries = s.LastSession.Where(e => File.Exists(e.Path)).ToList();
        if (entries.Count == 0) return;
        await open(entries.Select(e => e.Path));

        IPdfDocumentSession? active = null;
        var viewer = Viewer;
        foreach (var e in entries)
        {
            var d = _documents.Documents.FirstOrDefault(x => string.Equals(x.FilePath, Path.GetFullPath(e.Path), StringComparison.OrdinalIgnoreCase));
            if (d is null) continue;
            if (e.Active) active = d;
            if (e.Page > 0 && viewer?.GetView(d) is { } view)
            {
                try { if (view.PageCount > 0) view.GoToPage(Math.Min(e.Page, view.PageCount - 1)); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _log.LogDebug(ex, "Cannot restore page"); }
            }
        }
        if (active is not null) _documents.ActiveDocument = active;
    }
}

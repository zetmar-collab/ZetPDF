using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Core.Services;

/// <summary>
/// Autozapis: co ustawiony czas zapisuje zmienione dokumenty do folderu odzyskiwania (<see cref="RecoveryStore"/>), w tle.
/// Kopię usuwa po zapisie dokumentu (także Cofnij do wersji zapisanej), po zamknięciu i przy normalnym wyjściu. Metody
/// (poza samym zapisem na dysk) działają na wątku UI, jak <see cref="IDocumentManager"/>.
/// </summary>
public sealed class AutoSaveService
{
    public const int MinMinutes = 1, MaxMinutes = 120;

    private sealed class Tracked(string id)
    {
        public string Id { get; } = id;
        public long Version { get; set; } = long.MinValue;
        public string? Path { get; set; }
        public volatile bool Cancelled;
    }

    private readonly IDocumentManager _documents;
    private readonly ISettingsService _settings;
    private readonly ILogger<AutoSaveService> _log;
    private readonly Dictionary<IPdfDocumentSession, Tracked> _tracked = new();
    private readonly long _processStart = RecoveryStore.CurrentProcessStartUtcTicks();
    private DispatcherTimer? _timer;
    private bool _started, _running;

    public AutoSaveService(IDocumentManager documents, IAppPaths paths, ISettingsService settings, ILogger<AutoSaveService> log)
    {
        _documents = documents;
        _settings = settings;
        _log = log;
        Store = new RecoveryStore(paths.RecoveryDirectory);
    }

    public RecoveryStore Store { get; }

    /// <summary>Uruchamia śledzenie i zegar (wątek UI). Tryby diagnostyczne tego nie wołają.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _documents.DocumentOpened += OnDocumentOpened;
        _documents.DocumentClosed += OnDocumentClosed;
        foreach (var d in _documents.Documents) d.StateChanged += OnStateChanged;
        _settings.Changed += OnSettingsChanged;
        ApplySettings();
    }

    /// <summary>Zatrzymuje autozapis. deleteFiles = normalne wyjście (bez tego kopie zostają, np. przy zamykaniu Windows).</summary>
    public void Stop(bool deleteFiles)
    {
        if (_started)
        {
            _started = false;
            _documents.DocumentOpened -= OnDocumentOpened;
            _documents.DocumentClosed -= OnDocumentClosed;
            foreach (var d in _documents.Documents) d.StateChanged -= OnStateChanged;
            _settings.Changed -= OnSettingsChanged;
            _timer?.Stop();
            _timer = null;
        }
        if (!deleteFiles) return;
        foreach (var t in _tracked.Values) t.Cancelled = true;
        _tracked.Clear();
        Store.DeleteOwn();
    }

    /// <summary>Jeden przebieg autozapisu; zmienione dokumenty zapisywane kolejno w tle. Równoległy przebieg jest pomijany.</summary>
    public async Task SaveNowAsync(CancellationToken ct = default)
    {
        if (_running || !_settings.Current.AutoSaveEnabled) return;
        _running = true;
        try
        {
            foreach (var session in _documents.Documents.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                await SaveOneAsync(session, ct);
            }
        }
        finally
        {
            _running = false;
        }
    }

    private async Task SaveOneAsync(IPdfDocumentSession session, CancellationToken ct)
    {
        if (!session.IsModified)
        {
            Drop(session);
            return;
        }
        var snapshot = session.Current;
        if (!_tracked.TryGetValue(session, out var tracked))
        {
            tracked = new Tracked(RecoveryStore.NewId());
            _tracked[session] = tracked;
        }
        if (tracked.Version == snapshot.Version && tracked.Path == session.FilePath) return;

        var source = (_documents as DocumentManager)?.GetRecoverySource(session) ?? default;
        var info = new RecoveryInfo
        {
            Id = tracked.Id,
            ProcessId = Environment.ProcessId,
            ProcessStartUtcTicks = _processStart,
            OriginalPath = session.FilePath,
            DisplayName = session.DisplayName,
            SavedUtc = DateTime.UtcNow,
            State = session.FilePath is null ? RecoveryDocumentState.NeverSaved : RecoveryDocumentState.Modified,
            IsProtected = snapshot.Encryption is not null,
            Permissions = session.Permissions,
            SourceLength = source.Length,
            SourceModifiedUtcTicks = source.ModifiedUtcTicks,
            SourceSigned = source.Signed,
        };
        var path = session.FilePath;
        try
        {
            bool written = await Task.Run(() => Store.Write(info, snapshot.GetSharedBytes(), () => !tracked.Cancelled, ct), ct);
            if (written)
            {
                tracked.Version = snapshot.Version;
                tracked.Path = path;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bez okien: autozapis działa w tle, następny przebieg spróbuje ponownie (np. po zwolnieniu miejsca na dysku).
            _log.LogWarning(ex, "Autosave failed for {Id}", tracked.Id);
        }
    }

    private void Drop(IPdfDocumentSession session)
    {
        if (!_tracked.Remove(session, out var tracked)) return;
        tracked.Cancelled = true;
        Store.Delete(tracked.Id);
    }

    private void OnDocumentOpened(object? sender, IPdfDocumentSession d) => d.StateChanged += OnStateChanged;

    private void OnDocumentClosed(object? sender, IPdfDocumentSession d)
    {
        d.StateChanged -= OnStateChanged;
        Drop(d);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (sender is IPdfDocumentSession { IsModified: false } d) Drop(d);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings();

    private void ApplySettings()
    {
        var s = _settings.Current;
        if (!s.AutoSaveEnabled)
        {
            _timer?.Stop();
            _timer = null;
            foreach (var d in _tracked.Keys.ToArray()) Drop(d);
            return;
        }
        var interval = TimeSpan.FromMinutes(Math.Clamp(s.AutoSaveMinutes, MinMinutes, MaxMinutes));
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        else if (_timer.Interval != interval) _timer.Interval = interval;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        try { await SaveNowAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "Autosave pass failed"); }
    }
}

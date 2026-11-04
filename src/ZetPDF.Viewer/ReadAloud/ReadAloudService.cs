using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;

namespace ZetPDF.Viewer.ReadAloud;

public enum ReadAloudState { Stopped, Speaking, Paused }

/// <summary>Ustawienia czytania (sekcja "ReadAloud" w settings.json).</summary>
public sealed class ReadAloudSettings
{
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;
    /// <summary>Głos wybrany przez użytkownika; null = automatycznie wg języka dokumentu/interfejsu.</summary>
    public string? Voice { get; set; }
}

/// <summary>
/// Czytanie na głos: strona po stronie przez <see cref="ISpeechEngine"/> (w tle – synteza nie blokuje UI),
/// podświetlanie czytanego słowa na nakładce widoku, przewijanie do czytanego miejsca, głos dobierany do /Lang
/// dokumentu albo języka interfejsu. Zatrzymuje się przy zamknięciu lub zmianie dokumentu.
/// </summary>
public sealed class ReadAloudService : IDisposable
{
    public const string SettingsKey = "ReadAloud";

    private readonly IServiceProvider _services;
    private readonly IDocumentManager _documents;
    private readonly ILocalizationService _loc;
    private readonly ILogger? _log;
    private ISpeechEngine? _engine;
    private bool _engineFailed;
    private IDocumentView? _view;
    private ReadAloudOverlay? _overlay;
    private SpokenPage? _page;
    private int _utterance;
    private int _generation;
    private bool _continue;
    private bool _active;
    private ReadAloudState _state;
    private string? _autoVoice;
    private (IPdfDocumentSession Session, long Version, string? Lang)? _langCache;

    public ReadAloudService(IServiceProvider services)
    {
        _services = services;
        _documents = services.GetRequiredService<IDocumentManager>();
        _loc = services.GetRequiredService<ILocalizationService>();
        _log = services.GetService<ILoggerFactory>()?.CreateLogger<ReadAloudService>();
        _documents.DocumentClosed += (_, s) => { if (ReferenceEquals(_view?.Session, s)) Stop(); };
    }

    public ReadAloudState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Tryb czytania włączony (Read.Activate). Wyłączenie zatrzymuje czytanie.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (!value) Stop();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>Słowo czytane w tej chwili (strona i prostokąty w punktach strony) – dla nakładki i testów.</summary>
    public (int Page, IReadOnlyList<PdfRect> Rects)? Highlight { get; private set; }

    /// <summary>Czytana strona (−1, gdy nic nie jest czytane).</summary>
    public int CurrentPage => _state == ReadAloudState.Stopped ? -1 : _page?.PageIndex ?? -1;

    /// <summary>Widok, którego dokument jest czytany.</summary>
    public IDocumentView? View => _view;

    public ReadAloudSettings Settings => _services.GetService<ISettingsService>()?.GetModuleSettings<ReadAloudSettings>(SettingsKey) ?? _fallbackSettings;
    private readonly ReadAloudSettings _fallbackSettings = new();

    public void UpdateSettings(Action<ReadAloudSettings> change)
    {
        var s = Settings;
        change(s);
        if (_services.GetService<ISettingsService>() is { } svc) svc.SetModuleSettings(SettingsKey, s);
        if (_engine is { } e) ApplySettings(e, s);
    }

    /// <summary>Głosy zainstalowane w systemie (pusta lista, gdy synteza niedostępna).</summary>
    public IReadOnlyList<SpeechVoice> Voices => Engine()?.Voices ?? Array.Empty<SpeechVoice>();

    /// <summary>Głos użyty w bieżącym czytaniu.</summary>
    public string? VoiceInUse => _engine?.CurrentVoice;

    private ISpeechEngine? Engine()
    {
        if (_engine is not null || _engineFailed) return _engine;
        try
        {
            _engine = _services.GetRequiredService<ISpeechEngineFactory>().Create();
            _engine.WordStarted += OnWordStarted;
            _engine.Ended += OnEnded;
            ApplySettings(_engine, Settings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _engineFailed = true;
            _log?.LogError(ex, "Speech synthesis unavailable");
        }
        return _engine;
    }

    private static void ApplySettings(ISpeechEngine e, ReadAloudSettings s)
    {
        e.Rate = s.Rate;
        e.Volume = s.Volume;
    }

    /// <summary>Czyta stronę widoku; <paramref name="continueToEnd"/> = dalej do końca dokumentu.</summary>
    public async Task ReadAsync(IDocumentView view, int pageIndex, bool continueToEnd)
    {
        Stop();
        var engine = Engine();
        if (engine is null)
        {
            _services.GetService<IDialogService>()?.ShowMessage(_loc["Viewer.Read.Unavailable"], _loc["Viewer.Read.Title"], MessageSeverity.Warning);
            return;
        }
        IsActive = true;
        _view = view;
        _continue = continueToEnd;
        int gen = ++_generation;
        view.Session.Changed += OnSessionChanged;
        _overlay = new ReadAloudOverlay(this);
        view.AddOverlay(_overlay);
        await ChooseVoiceAsync(view.Session, engine);
        if (gen != _generation) return;
        await StartPageAsync(pageIndex, gen);
    }

    private async Task ChooseVoiceAsync(IPdfDocumentSession session, ISpeechEngine engine)
    {
        var settings = Settings;
        string? lang = await DocumentLanguageAsync(session);
        var choice = ChooseVoice(engine.Voices, settings.Voice, lang, _loc.CurrentCulture);
        if (choice.Voice is { } v) engine.SelectVoice(v.Name);
        _autoVoice = choice.Voice?.Name;
        if (choice.Fallback && engine.Voices.Count > 0)
        {
            string wanted = !string.IsNullOrWhiteSpace(lang) ? lang! : _loc.CurrentCulture.Name;
            _services.GetService<IShellService>()?.SetStatus(_loc.Format("Viewer.Read.NoVoiceForLanguage", wanted, engine.CurrentVoice ?? "?"));
        }
    }

    private async Task<string?> DocumentLanguageAsync(IPdfDocumentSession session)
    {
        var snap = session.Current;
        if (_langCache is { } c && ReferenceEquals(c.Session, session) && c.Version == snap.Version) return c.Lang;
        string? lang = null;
        try
        {
            var bytes = snap.GetSharedBytes();
            lang = await Task.Run(() => PdfSharpEdit.Read(bytes, d => d.Internals.Catalog.Elements.GetString("/Lang")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Cannot read /Lang");
        }
        lang = string.IsNullOrWhiteSpace(lang) ? null : lang.Trim();
        _langCache = (session, snap.Version, lang);
        return lang;
    }

    /// <summary>
    /// Wybór głosu: wybrany przez użytkownika (jeśli zainstalowany) → zgodny z językiem dokumentu (/Lang) →
    /// zgodny z językiem interfejsu → domyślny (Fallback = true, gdy nie znaleziono głosu dla żądanego języka).
    /// </summary>
    public static (SpeechVoice? Voice, bool Fallback) ChooseVoice(IReadOnlyList<SpeechVoice> voices, string? userVoice, string? documentLanguage, CultureInfo uiCulture)
    {
        if (!string.IsNullOrEmpty(userVoice) && voices.FirstOrDefault(v => v.Name == userVoice) is { } chosen) return (chosen, false);
        static string Two(string tag) => tag.Split('-', '_')[0].Trim().ToLowerInvariant();
        SpeechVoice? Match(string tag)
        {
            var exact = voices.FirstOrDefault(v => string.Equals(v.Culture.Name, tag, StringComparison.OrdinalIgnoreCase));
            return exact ?? voices.FirstOrDefault(v => Two(v.Culture.Name) == Two(tag));
        }
        if (!string.IsNullOrWhiteSpace(documentLanguage))
        {
            if (Match(documentLanguage) is { } d) return (d, false);
            return (null, true);
        }
        if (Match(uiCulture.Name) is { } u) return (u, false);
        return (null, true);
    }

    private async Task StartPageAsync(int pageIndex, int gen)
    {
        var view = _view;
        var engine = _engine;
        if (view is null || engine is null) return;
        while (pageIndex < view.PageCount)
        {
            PageText text;
            try { text = await view.GetPageTextAsync(pageIndex); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.LogWarning(ex, "Cannot read page text");
                text = PageText.Empty(pageIndex);
            }
            if (gen != _generation) return;
            var spoken = SpokenPage.Build(text);
            if (spoken.IsEmpty)
            {
                if (_continue) { pageIndex++; continue; }
                _services.GetService<IShellService>()?.SetStatus(_loc.Format("Viewer.Read.EmptyPage", pageIndex + 1));
                Finish();
                return;
            }
            _page = spoken;
            Highlight = null;
            if (view.CurrentPageIndex != pageIndex) view.GoToPage(pageIndex);
            _services.GetService<IShellService>()?.SetStatus(_loc.Format("Viewer.Read.Status", pageIndex + 1, view.PageCount));
            _utterance = engine.Speak(spoken.Text);
            State = ReadAloudState.Speaking;
            return;
        }
        Finish();
    }

    private void OnWordStarted(object? sender, SpeechWordEventArgs e)
    {
        if (e.Utterance != _utterance || _page is not { } page || _view is not { } view) return;
        var rects = page.RectsFor(e.Position, e.Length);
        Highlight = rects.Count > 0 ? (page.PageIndex, rects) : null;
        if (rects.Count > 0) view.ScrollIntoView(page.PageIndex, rects[0]);
        view.InvalidateOverlays();
    }

    private void OnEnded(object? sender, SpeechEndedEventArgs e)
    {
        if (e.Utterance != _utterance) return;
        if (e.Error is not null) _log?.LogWarning(e.Error, "Speech error");
        if (e.Cancelled || _page is null) return;
        if (_continue && _view is { } v && _page.PageIndex + 1 < v.PageCount)
        {
            _ = StartPageAsync(_page.PageIndex + 1, _generation);
            return;
        }
        Finish();
    }

    /// <summary>Wstrzymuje albo wznawia czytanie.</summary>
    public void TogglePause()
    {
        if (_engine is null) return;
        if (State == ReadAloudState.Speaking) { _engine.Pause(); State = ReadAloudState.Paused; }
        else if (State == ReadAloudState.Paused) { _engine.Resume(); State = ReadAloudState.Speaking; }
    }

    public void Stop()
    {
        _generation++;
        _utterance = 0;
        try { _engine?.Cancel(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log?.LogWarning(ex, "Speech cancel failed"); }
        Finish();
    }

    private void Finish()
    {
        if (_view is { } v)
        {
            v.Session.Changed -= OnSessionChanged;
            if (_overlay is not null) v.RemoveOverlay(_overlay);
        }
        _overlay = null;
        _view = null;
        _page = null;
        Highlight = null;
        State = ReadAloudState.Stopped;
    }

    private void OnSessionChanged(object? sender, DocumentChangedEventArgs e) => Stop();

    public void Dispose()
    {
        Stop();
        _engine?.Dispose();
        _engine = null;
    }
}

using System.Speech.Synthesis;

namespace ZetPDF.Viewer.ReadAloud;

/// <summary>
/// Silnik mowy na System.Speech (SAPI 5). Głosy wyłącznie lokalne (bez sieci). Sprawdzone w publikacji
/// single-file (.NET 8, win-x64): synteza, SpeakProgress i SetOutputToWaveStream działają.
/// </summary>
public sealed class SystemSpeechEngine : ISpeechEngine
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly SynchronizationContext? _context;
    private readonly Dictionary<Prompt, int> _prompts = new();
    private readonly object _gate = new();
    private string? _pendingVoice;
    private int _next;
    private bool _disposed;

    /// <param name="waveOutput">Strumień WAV zamiast głośnika (testy bez dźwięku); null = domyślne urządzenie audio.</param>
    public SystemSpeechEngine(Stream? waveOutput = null) : this(waveOutput, silent: false) { }

    private SystemSpeechEngine(Stream? waveOutput, bool silent)
    {
        _context = SynchronizationContext.Current;
        if (silent) _synth.SetOutputToNull();
        else if (waveOutput is not null) _synth.SetOutputToWaveStream(waveOutput);
        _synth.SpeakProgress += OnProgress;
        _synth.SpeakCompleted += OnCompleted;
        Voices = _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => new SpeechVoice(v.VoiceInfo.Name, v.VoiceInfo.Culture))
            .ToList();
    }

    /// <summary>Silnik bez dźwięku: synteza i zdarzenia jak zwykle, wyjście do pustego urządzenia.</summary>
    public static SystemSpeechEngine CreateSilent() => new(null, silent: true);

    public IReadOnlyList<SpeechVoice> Voices { get; }

    public string? CurrentVoice => _pendingVoice ?? SafeVoiceName();

    private string? SafeVoiceName()
    {
        try { return _synth.Voice?.Name; }
        catch (InvalidOperationException) { return null; }
    }

    public void SelectVoice(string name) => _pendingVoice = name;

    public int Rate
    {
        get => _synth.Rate;
        set => _synth.Rate = Math.Clamp(value, -10, 10);
    }

    public int Volume
    {
        get => _synth.Volume;
        set => _synth.Volume = Math.Clamp(value, 0, 100);
    }

    public int Speak(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingVoice is { } v && _synth.State == SynthesizerState.Ready)
        {
            try { _synth.SelectVoice(v); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            _pendingVoice = null;
        }
        if (_synth.State == SynthesizerState.Paused) _synth.Resume();
        var prompt = new Prompt(text);
        int id;
        lock (_gate)
        {
            id = ++_next;
            _prompts[prompt] = id;
        }
        _synth.SpeakAsync(prompt);
        return id;
    }

    public void Pause()
    {
        if (_synth.State == SynthesizerState.Speaking) _synth.Pause();
    }

    public void Resume()
    {
        if (_synth.State == SynthesizerState.Paused) _synth.Resume();
    }

    public void Cancel()
    {
        if (_synth.State == SynthesizerState.Paused) _synth.Resume();
        _synth.SpeakAsyncCancelAll();
    }

    public event EventHandler<SpeechWordEventArgs>? WordStarted;
    public event EventHandler<SpeechEndedEventArgs>? Ended;

    private int IdOf(Prompt prompt, bool remove)
    {
        lock (_gate)
        {
            if (!_prompts.TryGetValue(prompt, out int id)) return 0;
            if (remove) _prompts.Remove(prompt);
            return id;
        }
    }

    private void OnProgress(object? sender, SpeakProgressEventArgs e)
    {
        int id = IdOf(e.Prompt, remove: false);
        if (id == 0) return;
        var args = new SpeechWordEventArgs(id, e.CharacterPosition, e.CharacterCount);
        Post(() => WordStarted?.Invoke(this, args));
    }

    private void OnCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        int id = IdOf(e.Prompt, remove: true);
        if (id == 0) return;
        var args = new SpeechEndedEventArgs(id, e.Cancelled, e.Error);
        Post(() => Ended?.Invoke(this, args));
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        if (_context is null) action();
        else _context.Post(_ => { if (!_disposed) action(); }, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { _synth.SpeakAsyncCancelAll(); }
        catch (InvalidOperationException) { }
        _disposed = true;
        _synth.SpeakProgress -= OnProgress;
        _synth.SpeakCompleted -= OnCompleted;
        _synth.Dispose();
    }
}

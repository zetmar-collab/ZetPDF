using System.Globalization;

namespace ZetPDF.Viewer.ReadAloud;

/// <summary>Głos syntezatora (lokalny, zainstalowany w systemie).</summary>
public sealed record SpeechVoice(string Name, CultureInfo Culture);

/// <summary>Początek wypowiadania słowa: pozycja i długość w tekście przekazanym do <see cref="ISpeechEngine.Speak"/>.</summary>
public sealed class SpeechWordEventArgs : EventArgs
{
    public SpeechWordEventArgs(int utterance, int position, int length) { Utterance = utterance; Position = position; Length = length; }
    public int Utterance { get; }
    public int Position { get; }
    public int Length { get; }
}

/// <summary>Koniec wypowiedzi (dokończona albo anulowana).</summary>
public sealed class SpeechEndedEventArgs : EventArgs
{
    public SpeechEndedEventArgs(int utterance, bool cancelled, Exception? error = null) { Utterance = utterance; Cancelled = cancelled; Error = error; }
    public int Utterance { get; }
    public bool Cancelled { get; }
    public Exception? Error { get; }
}

/// <summary>
/// Syntezator mowy (abstrakcja nad System.Speech – w testach atrapa). Mówi asynchronicznie (nie blokuje wywołującego);
/// zdarzenia są podnoszone w kontekście synchronizacji wątku, który utworzył silnik (wątek UI).
/// </summary>
public interface ISpeechEngine : IDisposable
{
    IReadOnlyList<SpeechVoice> Voices { get; }
    /// <summary>Nazwa głosu, którym silnik będzie mówił.</summary>
    string? CurrentVoice { get; }
    void SelectVoice(string name);
    /// <summary>Szybkość −10..10 (0 = normalna).</summary>
    int Rate { get; set; }
    /// <summary>Głośność 0..100.</summary>
    int Volume { get; set; }
    /// <summary>Rozpoczyna wypowiedź; zwraca jej identyfikator (zdarzenia go niosą).</summary>
    int Speak(string text);
    void Pause();
    void Resume();
    /// <summary>Anuluje wszystkie wypowiedzi (każda dostaje <see cref="Ended"/> z Cancelled = true).</summary>
    void Cancel();
    event EventHandler<SpeechWordEventArgs>? WordStarted;
    event EventHandler<SpeechEndedEventArgs>? Ended;
}

/// <summary>Tworzy silnik mowy przy pierwszym użyciu czytania (w testach podmieniany w DI).</summary>
public interface ISpeechEngineFactory
{
    ISpeechEngine Create();
}

/// <summary>Domyślna fabryka: System.Speech (SAPI 5, głosy lokalne, wyjście na domyślne urządzenie audio).</summary>
public sealed class SystemSpeechEngineFactory : ISpeechEngineFactory
{
    public ISpeechEngine Create() => new SystemSpeechEngine();
}

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.TestData;
using ZetPDF.Viewer.ReadAloud;

namespace ZetPDF.Viewer.Tests;

/// <summary>Atrapa syntezatora: zapisuje wypowiedzi, słowa i koniec emituje test.</summary>
internal sealed class FakeSpeechEngine : ISpeechEngine
{
    public List<string> Spoken { get; } = new();
    public List<string> SelectedVoices { get; } = new();
    public int Current { get; private set; }
    public bool Paused { get; private set; }
    public int Cancels { get; private set; }
    public IReadOnlyList<SpeechVoice> Voices { get; set; } = new[]
    {
        new SpeechVoice("Zira", CultureInfo.GetCultureInfo("en-US")),
        new SpeechVoice("Paulina", CultureInfo.GetCultureInfo("pl-PL")),
    };
    public string? CurrentVoice => SelectedVoices.LastOrDefault() ?? Voices.FirstOrDefault()?.Name;
    public void SelectVoice(string name) => SelectedVoices.Add(name);
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;
    public int Speak(string text) { Spoken.Add(text); return ++Current; }
    public void Pause() => Paused = true;
    public void Resume() => Paused = false;
    public void Cancel() { Cancels++; Ended?.Invoke(this, new SpeechEndedEventArgs(Current, true)); }
    public event EventHandler<SpeechWordEventArgs>? WordStarted;
    public event EventHandler<SpeechEndedEventArgs>? Ended;

    /// <summary>Emituje słowo (pierwsze wystąpienie <paramref name="word"/> w bieżącej wypowiedzi).</summary>
    public void Word(string word)
    {
        int pos = Spoken[^1].IndexOf(word, StringComparison.Ordinal);
        WordStarted?.Invoke(this, new SpeechWordEventArgs(Current, pos, word.Length));
    }

    public void Finish() => Ended?.Invoke(this, new SpeechEndedEventArgs(Current, false));
    public void Dispose() { }
}

internal sealed class FakeSpeechFactory : ISpeechEngineFactory
{
    public FakeSpeechEngine Engine { get; } = new();
    public ISpeechEngine Create() => Engine;
}

[Collection(PdfiumCollection.Name)]
public class ReadAloudTests
{
    [Fact]
    public void Spoken_page_joins_lines_collapses_spaces_and_maps_characters_to_boxes()
    {
        var chars = new List<TextChar>();
        string text = "Zażółć  gęś-\r\nlą jaźń\r\nKoniec";
        for (int i = 0; i < text.Length; i++)
        {
            bool gen = text[i] is '\r' or '\n';
            chars.Add(new TextChar(text[i], gen ? default : new PdfRect(10 + i * 5, i < 14 ? 700 : 680, 14 + i * 5, i < 14 ? 712 : 692), 12, gen));
        }
        var page = SpokenPage.Build(new PageText(0, text, chars));
        Assert.Equal("Zażółć gęślą jaźń Koniec", page.Text);
        int pos = page.Text.IndexOf("gęślą", StringComparison.Ordinal);
        var rects = page.RectsFor(pos, 5);
        Assert.Equal(2, rects.Count); // słowo przeniesione do następnej linii → dwa prostokąty
        Assert.Equal(-1, page.SourceIndex[6]); // spacja zwinięta (wstawiona)
        Assert.True(SpokenPage.Build(PageText.Empty(3)).IsEmpty);
    }

    [Fact]
    public void Voice_is_chosen_by_document_language_then_interface_language()
    {
        var voices = new FakeSpeechEngine().Voices;
        var pl = CultureInfo.GetCultureInfo("pl-PL");
        Assert.Equal("Paulina", ReadAloudService.ChooseVoice(voices, null, "pl", CultureInfo.GetCultureInfo("en")).Voice!.Name);
        Assert.Equal("Zira", ReadAloudService.ChooseVoice(voices, null, "en-GB", pl).Voice!.Name);
        Assert.Equal("Paulina", ReadAloudService.ChooseVoice(voices, null, null, pl).Voice!.Name);
        Assert.Equal("Zira", ReadAloudService.ChooseVoice(voices, "Zira", "pl-PL", pl).Voice!.Name);
        var (none, fallback) = ReadAloudService.ChooseVoice(voices, null, "de-DE", pl);
        Assert.Null(none);
        Assert.True(fallback);
    }

    [Fact]
    public void Read_from_page_speaks_pages_in_order_highlights_words_and_stops_on_close() => Sta.Run(async () =>
    {
        var factory = new FakeSpeechFactory();
        using var host = new ViewerHost(s => s.AddSingleton<ISpeechEngineFactory>(factory));
        var pdf = PdfSharpEdit.Modify(SamplePdfFactory.Text(3), d => d.Internals.Catalog.Elements.SetString("/Lang", "pl-PL"));
        var (session, view) = await host.OpenAsync(pdf);
        var registry = host.Services.GetRequiredService<ICommandRegistry>();
        var read = host.Services.GetRequiredService<ReadAloudService>();
        var engine = factory.Engine;

        Assert.False(registry.GetHandler(CommandIds.Read.Stop)!.CanExecute(null));
        view.GoToPage(1);
        Sta.Layout(view.Element);
        Assert.True(registry.TryExecute(CommandIds.Read.FromPage));
        await Sta.WaitFor(() => engine.Spoken.Count == 1, what: "page 2 spoken");
        Assert.True(read.IsActive);
        Assert.True(((IToggleCommand)registry.GetHandler(CommandIds.Read.Activate)!).IsChecked);
        Assert.Equal(ReadAloudState.Speaking, read.State);
        Assert.Equal("Paulina", engine.SelectedVoices.Last()); // /Lang pl-PL
        Assert.StartsWith("Strona 2 Zażółć gęślą jaźń", engine.Spoken[0]);

        engine.Word("gęślą");
        Assert.NotNull(read.Highlight);
        Assert.Equal(1, read.Highlight!.Value.Page);
        var r = Assert.Single(read.Highlight.Value.Rects);
        Assert.InRange(r.Width, 20, 80);

        // pauza / wznowienie
        Assert.True(registry.TryExecute(CommandIds.Read.Pause));
        Assert.Equal(ReadAloudState.Paused, read.State);
        Assert.True(engine.Paused);
        Assert.True(registry.TryExecute(CommandIds.Read.Pause));
        Assert.Equal(ReadAloudState.Speaking, read.State);

        // koniec strony → następna strona, widok przewija się do niej
        engine.Finish();
        await Sta.WaitFor(() => engine.Spoken.Count == 2, what: "page 3 spoken");
        Assert.StartsWith("Strona 3", engine.Spoken[1]);
        Assert.Equal(2, view.CurrentPageIndex);
        Assert.Equal(2, read.CurrentPage);

        // zamknięcie dokumentu zatrzymuje czytanie
        host.Documents.Close(session);
        Assert.Equal(ReadAloudState.Stopped, read.State);
        Assert.Null(read.Highlight);
        Assert.True(engine.Cancels > 0);
        view.Dispose();
    });

    [Fact]
    public void Read_current_page_stops_at_page_end_and_stop_command_cancels() => Sta.Run(async () =>
    {
        var factory = new FakeSpeechFactory();
        using var host = new ViewerHost(s => s.AddSingleton<ISpeechEngineFactory>(factory));
        var (_, view) = await host.OpenAsync(SamplePdfFactory.Text(2));
        var registry = host.Services.GetRequiredService<ICommandRegistry>();
        var read = host.Services.GetRequiredService<ReadAloudService>();
        Assert.True(registry.TryExecute(CommandIds.Read.CurrentPage));
        await Sta.WaitFor(() => factory.Engine.Spoken.Count == 1, what: "spoken");
        factory.Engine.Finish();
        Assert.Equal(ReadAloudState.Stopped, read.State);
        Assert.Single(factory.Engine.Spoken);

        Assert.True(registry.TryExecute(CommandIds.Read.CurrentPage));
        await Sta.WaitFor(() => factory.Engine.Spoken.Count == 2, what: "spoken again");
        Assert.True(registry.TryExecute(CommandIds.Read.Stop));
        Assert.Equal(ReadAloudState.Stopped, read.State);
        Assert.True(registry.TryExecute(CommandIds.Read.Activate)); // wyłącza tryb
        Assert.False(read.IsActive);
        view.Dispose();
    });

    [Fact]
    public void System_speech_synthesizes_polish_text_to_wave_stream_with_word_progress() => Sta.Run(async () =>
    {
        using var wave = new MemoryStream();
        SystemSpeechEngine engine;
        try { engine = new SystemSpeechEngine(wave); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return; // brak SAPI – nic do sprawdzenia
        }
        using (engine)
        {
            if (engine.Voices.Count == 0) return;
            if (engine.Voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "pl") is { } pl) engine.SelectVoice(pl.Name);
            var words = new List<(int Pos, int Len)>();
            bool ended = false;
            engine.WordStarted += (_, e) => words.Add((e.Position, e.Length));
            engine.Ended += (_, e) => ended = !e.Cancelled;
            const string text = "Zażółć gęślą jaźń. Strona druga.";
            engine.Speak(text);
            await Sta.WaitFor(() => ended, 30000, "speech completed");
            Assert.True(words.Count >= 3, $"words: {words.Count}");
            Assert.Contains(words, w => text.Substring(w.Pos, w.Len) == "gęślą");
            Assert.True(wave.Length > 10000, $"wave {wave.Length}");
        }
    });

    [Fact]
    public void Silent_engine_synthesizes_with_word_progress_without_audio_device() => Sta.Run(async () =>
    {
        SystemSpeechEngine engine;
        try { engine = SystemSpeechEngine.CreateSilent(); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return; // brak SAPI
        }
        using (engine)
        {
            if (engine.Voices.Count == 0) return;
            int words = 0;
            bool ended = false;
            engine.WordStarted += (_, _) => words++;
            engine.Ended += (_, e) => ended = !e.Cancelled;
            engine.Speak("Zażółć gęślą jaźń.");
            await Sta.WaitFor(() => ended, 30000, "silent speech completed");
            Assert.True(words >= 2, $"words: {words}");
        }
    });

    [Fact]
    public void Test_host_uses_silent_speech_factory()
    {
        using var host = new ViewerHost(initialize: false);
        Assert.IsType<SilentSpeechEngineFactory>(host.Services.GetRequiredService<ISpeechEngineFactory>());
    }
}

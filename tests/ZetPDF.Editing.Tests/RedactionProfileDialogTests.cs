using System.Runtime.ExceptionServices;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Services;
using ZetPDF.Editing.Ui.Dialogs;

namespace ZetPDF.Editing.Tests;

/// <summary>Okno „Szukaj i redaguj” z profilami: wczytanie profilu do pól i zapis pól jako profilu.</summary>
public class RedactionProfileDialogTests
{
    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object> _modules = new();
        public AppSettings Current { get; } = new();
        public event EventHandler? Changed;
        public void Update(Action<AppSettings> change) { change(Current); Changed?.Invoke(this, EventArgs.Empty); }
        public T GetModuleSettings<T>(string key) where T : class, new() =>
            _modules.TryGetValue(key, out var v) ? System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(v))! : new T();
        public void SetModuleSettings<T>(string key, T value) where T : class, new() => _modules[key] = value;
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void Loaded_profile_fills_fields_and_fields_round_trip_to_profile() => Sta(() =>
    {
        var loc = new LocalizationService();
        loc.RegisterResources(EditingModule.Strings);
        var store = new RedactionProfileStore(new MemorySettings());
        var profile = new RedactionProfile { Name = "Umowy", Phrases = new[] { "Jan Kowalski", "Zażółć" }, Patterns = SensitiveDataPattern.Nip | SensitiveDataPattern.IbanPl, MatchCase = true };
        Assert.True(store.Save(profile));

        var dlg = new SearchRedactDialog(loc, _ => Task.FromResult<IReadOnlyList<RedactionCandidate>>(Array.Empty<RedactionCandidate>()), store);
        dlg.LoadProfile(store.All.Single());
        var back = dlg.CurrentProfile("Umowy");
        Assert.Equal(profile.Phrases, back.Phrases);
        Assert.Equal(profile.Patterns, back.Patterns);
        Assert.True(back.MatchCase);
        Assert.False(back.WholeWord);
        Batch2UiTests.Layout(dlg);
    });
}

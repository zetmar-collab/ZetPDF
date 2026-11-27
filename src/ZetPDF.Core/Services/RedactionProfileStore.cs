using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Services;

/// <summary>Zapisany zestaw wyszukiwania do redakcji: frazy, wzorce danych wrażliwych i opcje dopasowania.</summary>
public sealed record RedactionProfile
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Phrases { get; init; } = Array.Empty<string>();
    public SensitiveDataPattern Patterns { get; init; }
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }

    public RedactionSearch ToSearch() => new()
    {
        Phrases = Phrases,
        Patterns = Patterns,
        Options = (MatchCase ? TextSearchOptions.MatchCase : 0) | (WholeWord ? TextSearchOptions.WholeWord : 0),
    };
}

/// <summary>
/// Profile redakcji w ustawieniach programu (moduł „Editing.RedactionProfiles”): okno „Szukaj i redaguj” i krok Kreatora działań
/// „Redakcja według profilu”. Nazwy bez rozróżniania wielkości liter; limity liczby profili, fraz i długości chronią plik ustawień.
/// Frazy mogą być danymi wrażliwymi – plik ustawień leży w profilu użytkownika Windows (%APPDATA%).
/// </summary>
public sealed class RedactionProfileStore
{
    public const string SettingsKey = "Editing.RedactionProfiles";
    public const int MaxProfiles = 50, MaxPhrases = 500, MaxPhraseLength = 500, MaxNameLength = 80;

    private sealed class Stored
    {
        public List<RedactionProfile> Profiles { get; set; } = new();
    }

    private readonly ISettingsService _settings;

    public RedactionProfileStore(ISettingsService settings) => _settings = settings;

    public IReadOnlyList<RedactionProfile> All =>
        (_settings.GetModuleSettings<Stored>(SettingsKey).Profiles ?? new())
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select(Normalize)
            .GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.Last())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxProfiles)
            .ToList();

    public RedactionProfile? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Zapisuje (lub zastępuje profil o tej samej nazwie). False, gdy nazwa pusta albo osiągnięto limit profili.</summary>
    public bool Save(RedactionProfile profile)
    {
        var p = Normalize(profile);
        if (p.Name.Length == 0) return false;
        var list = All.Where(x => !string.Equals(x.Name, p.Name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (list.Count >= MaxProfiles) return false;
        list.Add(p);
        _settings.SetModuleSettings(SettingsKey, new Stored { Profiles = list });
        return true;
    }

    public void Delete(string name)
    {
        var list = All.Where(x => !string.Equals(x.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        _settings.SetModuleSettings(SettingsKey, new Stored { Profiles = list });
    }

    private static RedactionProfile Normalize(RedactionProfile p)
    {
        var name = (p.Name ?? string.Empty).Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        var phrases = (p.Phrases ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Where(s => s.Length <= MaxPhraseLength)
            .Distinct(StringComparer.Ordinal).Take(MaxPhrases).ToList();
        return p with { Name = name, Phrases = phrases, Patterns = p.Patterns & SensitiveDataPattern.All };
    }
}

using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Logging;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Localization;

/// <summary>
/// Implementacja <see cref="ILocalizationService"/>: agreguje zasoby .resx wszystkich modułów
/// (kolejność rejestracji; klucze mają prefiksy modułów, więc nie powinny się powtarzać – test to sprawdza).
/// Zmiana kultury podnosi PropertyChanged("Item[]") – wiązania z <see cref="LocExtension"/> odświeżają się na żywo.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private readonly List<ResourceManager> _resources = new();
    private readonly HashSet<string> _reportedMissing = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly ILogger<LocalizationService>? _log;
    private CultureInfo _culture;

    public LocalizationService(ILogger<LocalizationService>? log = null, CultureInfo? initial = null)
    {
        _log = log;
        _culture = initial ?? ResolveCulture(null);
        RegisterResources(CoreResources.Manager);
        Current = this;
    }

    /// <summary>Instancja dla rozszerzenia znaczników XAML (ustawiana w konstruktorze; jedna na aplikację).</summary>
    public static LocalizationService? Current { get; private set; }

    public static IReadOnlyList<CultureInfo> Cultures { get; } = new[] { new CultureInfo("pl"), new CultureInfo("en") };

    public IReadOnlyList<CultureInfo> SupportedCultures => Cultures;

    public CultureInfo CurrentCulture => _culture;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;

    /// <summary>Język: ustawienie ("pl"/"en") lub język systemu (pl dla pl-*, inaczej en).</summary>
    public static CultureInfo ResolveCulture(string? setting)
    {
        if (!string.IsNullOrWhiteSpace(setting))
        {
            var match = Cultures.FirstOrDefault(c => string.Equals(c.Name, setting, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "pl" ? Cultures[0] : Cultures[1];
    }

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            ResourceManager[] managers;
            lock (_gate) managers = _resources.ToArray();
            foreach (var rm in managers)
            {
                string? s;
                try { s = rm.GetString(key, _culture); }
                catch (MissingManifestResourceException) { continue; }
                if (s is not null) return s;
            }
            lock (_gate)
            {
                if (_reportedMissing.Add(key)) _log?.LogWarning("Missing localization key {Key} ({Culture})", key, _culture.Name);
            }
            return "[" + key + "]";
        }
    }

    public string Format(string key, params object?[] args) => string.Format(_culture, this[key], args);

    public bool HasKey(string key)
    {
        ResourceManager[] managers;
        lock (_gate) managers = _resources.ToArray();
        return managers.Any(rm =>
        {
            try { return rm.GetString(key, CultureInfo.InvariantCulture) is not null; }
            catch (MissingManifestResourceException) { return false; }
        });
    }

    public IReadOnlyCollection<string> AllKeys
    {
        get
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            ResourceManager[] managers;
            lock (_gate) managers = _resources.ToArray();
            foreach (var rm in managers)
            {
                ResourceSet? set;
                try { set = rm.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true); }
                catch (MissingManifestResourceException) { continue; }
                if (set is null) continue;
                foreach (DictionaryEntry e in set) keys.Add((string)e.Key);
            }
            return keys;
        }
    }

    public void RegisterResources(ResourceManager resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        lock (_gate)
        {
            if (_resources.Any(r => r.BaseName == resources.BaseName)) return;
            _resources.Add(resources);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var match = Cultures.FirstOrDefault(c => c.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName) ?? Cultures[1];
        if (Equals(match, _culture)) return;
        _culture = match;
        CultureInfo.CurrentUICulture = match;
        CultureInfo.DefaultThreadCurrentUICulture = match;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Zasoby rdzenia (Core\Resources\Strings*.resx).</summary>
public static class CoreResources
{
    public static ResourceManager Manager { get; } = new("ZetPDF.Core.Resources.Strings", typeof(CoreResources).Assembly);
}

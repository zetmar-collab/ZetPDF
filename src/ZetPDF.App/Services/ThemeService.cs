using System.Windows;
using System.Windows.Media;
using ControlzEx.Theming;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Services;

/// <summary>
/// Motywy: podmienia słownik Themes/Light|Dark.xaml (pędzle ThemeKeys) i motyw Fluent.Ribbon.
/// Motyw wstążki jest generowany w locie z koloru akcentu ze słownika ZetPDF (RuntimeThemeGenerator),
/// więc nie ma kolorów poza słownikami motywów. "Jak w systemie" – rejestr AppsUseLightTheme + nasłuch
/// SystemEvents.UserPreferenceChanged. Wybór zapisywany w ustawieniach.
/// </summary>
public sealed class ThemeService : IThemeService, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly ISettingsService _settings;
    private readonly ILogger<ThemeService>? _log;
    private readonly UserPreferenceChangedEventHandler _systemHandler;
    private ResourceDictionary? _current;
    private bool _applied;

    public ThemeService(ISettingsService settings, ILogger<ThemeService>? log = null)
    {
        _settings = settings;
        _log = log;
        _systemHandler = (_, e) =>
        {
            if (Theme != AppTheme.System) return;
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(force: false));
        };
        SystemEvents.UserPreferenceChanged += _systemHandler;
    }

    public AppTheme Theme { get; private set; } = AppTheme.System;
    public AppTheme EffectiveTheme { get; private set; } = AppTheme.Light;
    public event EventHandler? ThemeChanged;

    public void SetTheme(AppTheme theme)
    {
        Theme = theme;
        if (_settings.Current.Theme != theme) _settings.Update(s => s.Theme = theme);
        Apply(force: true);
    }

    /// <summary>Czy Windows używa jasnego motywu aplikacji (brak klucza = jasny).</summary>
    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Rozwija System do Light/Dark.</summary>
    public static AppTheme Resolve(AppTheme theme, bool systemIsLight) =>
        theme == AppTheme.System ? (systemIsLight ? AppTheme.Light : AppTheme.Dark) : theme;

    private void Apply(bool force)
    {
        var app = Application.Current;
        if (app is null) return;
        var effective = Resolve(Theme, IsSystemLight());
        if (!force && _applied && effective == EffectiveTheme) return;
        var changed = !_applied || effective != EffectiveTheme;
        EffectiveTheme = effective;
        _applied = true;
        if (!changed && _current is not null)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var dark = effective == AppTheme.Dark;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/ZetPDF;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        var merged = app.Resources.MergedDictionaries;
        var old = _current ?? merged.FirstOrDefault(IsZetPdfThemeDictionary);
        int index = old is null ? 0 : merged.IndexOf(old);
        if (old is not null) merged.Remove(old);
        merged.Insert(Math.Clamp(index, 0, merged.Count), dict);
        _current = dict;

        ApplyRibbonTheme(app, dict, dark);
        WindowThemer.ApplyToAllWindows(dark);
        _log?.LogInformation("Theme applied: {Theme} (effective {Effective})", Theme, effective);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsZetPdfThemeDictionary(ResourceDictionary d)
    {
        var s = d.Source?.OriginalString;
        return s is not null && (s.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyRibbonTheme(Application app, ResourceDictionary dict, bool dark)
    {
        var baseColor = dark ? ThemeManager.BaseColorDarkConst : ThemeManager.BaseColorLightConst;
        try
        {
            if (dict[ThemeKeys.Accent] is SolidColorBrush accent)
            {
                var generated = RuntimeThemeGenerator.Current.GenerateRuntimeTheme(baseColor, accent.Color);
                if (generated is not null)
                {
                    ThemeManager.Current.ChangeTheme(app, generated);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Runtime ribbon theme generation failed; using built-in scheme");
        }
        ThemeManager.Current.ChangeTheme(app, baseColor + ".Teal");
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= _systemHandler;
}

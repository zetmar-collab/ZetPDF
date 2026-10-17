using System.Globalization;
using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Engine;
using ZetPDF.Pages.Optimizer;
using ZetPDF.Pages.Sanitize;
using ZetPDF.Pages.Stamping;

namespace ZetPDF.Pages;

/// <summary>
/// Moduł ZetPDF.Pages (właściciel: pages-engine): operacje na stronach, oznaczenia stron, scalanie/dzielenie,
/// optymalizator, skala szarości, oczyszczanie. Rejestruje usługi kontraktów i obsługę 40 poleceń wstążki.
/// </summary>
public sealed class PagesModule : IFeatureModule
{
    private static ILocalizationService? _loc;

    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Pages.Resources.Strings", typeof(PagesModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Pages;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPageOperations, PageOperations>();
        services.AddSingleton<IStampingService, StampingService>();
        services.AddSingleton<IOptimizerService, OptimizerService>();
        services.AddSingleton<ISanitizeService, SanitizeService>();
    }

    public void Initialize(IServiceProvider services)
    {
        _loc = services.GetRequiredService<ILocalizationService>();
        _loc.RegisterResources(Strings);
        var registry = services.GetRequiredService<ICommandRegistry>();
        new Commands.PagesCommands(services).Register(registry);
    }

    /// <summary>Tekst z zasobów modułu w bieżącym języku (komunikaty silnika).</summary>
    internal static string Text(string key)
    {
        if (_loc is not null && _loc.HasKey(key)) return _loc[key];
        try { return Strings.GetString(key, CultureInfo.CurrentUICulture) ?? key; }
        catch (MissingManifestResourceException) { return key; }
    }

    internal static string Format(string key, params object?[] args) =>
        string.Format(_loc?.CurrentCulture ?? CultureInfo.CurrentUICulture, Text(key), args);
}

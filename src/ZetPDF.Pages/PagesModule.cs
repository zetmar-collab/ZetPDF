using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages;

/// <summary>
/// Moduł ZetPDF.Pages (właściciel: pages-engine). Rejestracja w App jest stała (ModuleCatalog) – tu tylko wypełniamy usługi i polecenia.
/// </summary>
public sealed class PagesModule : IFeatureModule
{
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
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        // pages-engine: tu podpinamy obsługę poleceń z katalogu, np.:
        // var registry = services.GetRequiredService<ICommandRegistry>();
        // registry.Register(CommandIds.Pages.Delete, new ActiveDocumentCommand(...));
        // Polecenia bez obsługi są na wstążce wyszarzone.
    }
}

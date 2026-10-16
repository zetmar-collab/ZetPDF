using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Rendering;

/// <summary>
/// Moduł ZetPDF.Rendering (właściciel: renderer-viewer). Rejestracja w App jest stała (ModuleCatalog) – tu tylko wypełniamy usługi i polecenia.
/// </summary>
public sealed class RenderingModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Rendering.Resources.Strings", typeof(RenderingModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Rendering;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPdfRenderer, PdfiumRenderer>();
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        // renderer-viewer: tu podpinamy obsługę poleceń z katalogu, np.:
        // var registry = services.GetRequiredService<ICommandRegistry>();
        // registry.Register(CommandIds.File.Print, new ActiveDocumentCommand(...));
        // Polecenia bez obsługi są na wstążce wyszarzone.
    }
}

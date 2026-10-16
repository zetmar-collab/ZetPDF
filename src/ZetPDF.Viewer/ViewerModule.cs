using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer;

/// <summary>
/// Moduł ZetPDF.Viewer (właściciel: renderer-viewer). Rejestracja w App jest stała (ModuleCatalog) – tu tylko wypełniamy usługi i polecenia.
/// </summary>
public sealed class ViewerModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Viewer.Resources.Strings", typeof(ViewerModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Viewer;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ViewerService>();
        services.AddSingleton<IViewerService>(sp => sp.GetRequiredService<ViewerService>());
        services.AddSingleton<IDocumentViewFactory, PlaceholderDocumentViewFactory>();
        services.AddSingleton<IToolService, ToolService>();
        services.AddSingleton<IBookmarkService, BookmarkService>();
        services.AddSingleton<IPrintService, PrintService>();
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        // renderer-viewer: tu podpinamy obsługę poleceń z katalogu, np.:
        // var registry = services.GetRequiredService<ICommandRegistry>();
        // registry.Register(CommandIds.View.ZoomIn, new ActiveDocumentCommand(...));
        // Polecenia bez obsługi są na wstążce wyszarzone.
    }
}

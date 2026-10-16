using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;

namespace ZetPDF.Core.Services;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Rejestruje usługi rdzenia: ścieżki, dziennik plikowy, ustawienia, ostatnie pliki, zapis plików,
    /// lokalizację, rejestr poleceń i paneli, zarządcę dokumentów, metadane.
    /// </summary>
    public static IServiceCollection AddZetPdfCore(this IServiceCollection services, IAppPaths? paths = null)
    {
        paths ??= new AppPaths();
        services.AddSingleton(paths);
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new FileLoggerProvider(paths.LogDirectory));
        });
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IRecentFilesService, RecentFilesService>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<LocalizationService>(sp => new LocalizationService(
            sp.GetService<ILogger<LocalizationService>>(),
            LocalizationService.ResolveCulture(sp.GetRequiredService<ISettingsService>().Current.Language)));
        services.AddSingleton<ILocalizationService>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddSingleton<CommandRegistry>();
        services.AddSingleton<ICommandRegistry>(sp => sp.GetRequiredService<CommandRegistry>());
        services.AddSingleton<IPanelRegistry, PanelRegistry>();
        services.AddSingleton<IDocumentManager, DocumentManager>();
        services.AddSingleton<IMetadataService, MetadataService>();
        return services;
    }
}

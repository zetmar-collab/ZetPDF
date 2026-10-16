using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Ocr;

/// <summary>
/// Moduł ZetPDF.Ocr (właściciel: edit-convert-ocr). Rejestracja w App jest stała (ModuleCatalog) – tu tylko wypełniamy usługi i polecenia.
/// </summary>
public sealed class OcrModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Ocr.Resources.Strings", typeof(OcrModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Ocr;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TesseractBootstrap>();
        services.AddSingleton<IOcrService, TesseractOcrService>();
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        // edit-convert-ocr: tu podpinamy obsługę poleceń z katalogu, np.:
        // var registry = services.GetRequiredService<ICommandRegistry>();
        // registry.Register(CommandIds.Ocr.RecognizeDocument, new ActiveDocumentCommand(...));
        // Polecenia bez obsługi są na wstążce wyszarzone.
    }
}

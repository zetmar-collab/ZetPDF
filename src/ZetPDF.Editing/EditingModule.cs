using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing;

/// <summary>
/// Moduł ZetPDF.Editing (właściciel: edit-convert-ocr). Rejestracja w App jest stała (ModuleCatalog) – tu tylko wypełniamy usługi i polecenia.
/// </summary>
public sealed class EditingModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Editing.Resources.Strings", typeof(EditingModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Editing;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ITextEditService, TextEditService>();
        services.AddSingleton<IObjectEditService, ObjectEditService>();
        services.AddSingleton<ILinkService, LinkService>();
        services.AddSingleton<IConversionService, ConversionService>();
        services.AddSingleton<IRedactionService, RedactionService>();
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        // edit-convert-ocr: tu podpinamy obsługę poleceń z katalogu, np.:
        // var registry = services.GetRequiredService<ICommandRegistry>();
        // registry.Register(CommandIds.Edit.EditText, new ActiveDocumentCommand(...));
        // Polecenia bez obsługi są na wstążce wyszarzone.
    }
}

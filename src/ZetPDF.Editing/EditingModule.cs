using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Ui;

namespace ZetPDF.Editing;

/// <summary>
/// Moduł ZetPDF.Editing (właściciel: edit-convert-ocr): edycja tekstu i obiektów, łącza, zakładki z nagłówków,
/// konwersje, redakcja oraz okna OCR (silnik OCR: ZetPDF.Ocr).
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
        services.AddSingleton<IConversionService>(sp => new ConversionService(sp.GetRequiredService<IPdfRenderer>(), sp.GetService<ILocalizationService>()));
        services.AddSingleton<IRedactionService>(sp => new RedactionService(sp.GetRequiredService<IPdfRenderer>(), sp.GetService<ILocalizationService>()));
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        var registry = services.GetService<ICommandRegistry>();
        if (registry is not null) new EditingCommands(new EditingContext(services), registry).RegisterAll();
    }
}

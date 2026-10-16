using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations;

/// <summary>
/// Moduł ZetPDF.Annotations (właściciel: annotations-forms). Rejestracja w App jest stała (ModuleCatalog) – tu tylko wypełniamy usługi i polecenia.
/// </summary>
public sealed class AnnotationsModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Annotations.Resources.Strings", typeof(AnnotationsModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Annotations;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAnnotationService, AnnotationService>();
        services.AddSingleton<IFormService, FormService>();
        services.AddSingleton<IAttachmentService, AttachmentService>();
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        // annotations-forms: tu podpinamy obsługę poleceń z katalogu, np.:
        // var registry = services.GetRequiredService<ICommandRegistry>();
        // registry.Register(CommandIds.Comment.Highlight, new ActiveDocumentCommand(...));
        // Polecenia bez obsługi są na wstążce wyszarzone.
    }
}

using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Annotations.UI;
using ZetPDF.Annotations.UI.Panels;
using ZetPDF.Annotations.UI.Tools;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations;

/// <summary>
/// Moduł ZetPDF.Annotations (właściciel: annotations-forms): komentarze z wyglądem /AP, panel Komentarze z odpowiedziami
/// i stanem, XFDF/FDF, wypełnianie i tworzenie pól AcroForm, dane FDF/XFDF/CSV, Wypełnij i podpisz, panel Załączniki.
/// </summary>
public sealed class AnnotationsModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Annotations.Resources.Strings", typeof(AnnotationsModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Annotations;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new AnnotationService(sp.GetService<ILocalizationService>(), sp.GetService<ISettingsService>()));
        services.AddSingleton<IAnnotationService>(sp => sp.GetRequiredService<AnnotationService>());
        services.AddSingleton<FormService>();
        services.AddSingleton<IFormService>(sp => sp.GetRequiredService<FormService>());
        services.AddSingleton<AttachmentService>();
        services.AddSingleton<IAttachmentService>(sp => sp.GetRequiredService<AttachmentService>());
        services.AddSingleton<AnnotContext>(sp => new AnnotContext(sp));
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        var ctx = services.GetRequiredService<AnnotContext>();
        var select = new SelectAnnotationTool(ctx);
        if (services.GetService<ICommandRegistry>() is { } registry) new AnnotationCommands(ctx, registry, select).RegisterAll();
        if (services.GetService<IPanelRegistry>() is { } panels)
        {
            panels.Register(new PanelDescriptor(PanelIds.Comments, "Annot.Panel.Title", "", 30, _ => new CommentsPanel(ctx)));
            panels.Register(new PanelDescriptor(PanelIds.Attachments, "Annot.Attach.Title", "", 40, _ => new AttachmentsPanel(ctx)));
        }
        new FormFillController(ctx, select).Start();
    }
}

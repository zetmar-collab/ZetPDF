using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Security.Signatures;

namespace ZetPDF.Security;

/// <summary>
/// Moduł ZetPDF.Security (właściciel: security-signatures): ochrona hasłem, usuwanie zabezpieczeń, właściwości
/// zabezpieczeń, podpisy cyfrowe (PFX/magazyn Windows, widoczne pole, TSA, DocTimeStamp), weryfikacja i panel Podpisy,
/// identyfikatory cyfrowe, porównanie dokumentów, Kreator działań.
/// </summary>
public sealed class SecurityModule : IFeatureModule
{
    /// <summary>Zasoby tekstów modułu (Resources\Strings.resx + Strings.pl.resx).</summary>
    public static ResourceManager Strings { get; } = new("ZetPDF.Security.Resources.Strings", typeof(SecurityModule).Assembly);

    public FeatureModuleId Id => FeatureModuleId.Security;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SecurityService>();
        services.AddSingleton<ISecurityService>(sp => sp.GetRequiredService<SecurityService>());
        services.AddSingleton<ITimestampClient, HttpTimestampClient>();
        services.AddSingleton<ISignatureService>(sp => new SignatureService(sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<ITimestampClient>(), sp.GetService<ILocalizationService>()));
        services.AddSingleton<ICompareService>(sp => new CompareService(sp));
        services.AddSingleton<IActionWizard>(sp => new ActionWizard(sp, sp.GetRequiredService<IAppPaths>()));
    }

    public void Initialize(IServiceProvider services)
    {
        PdfStructureGuard.EncryptedVerifier = Pdf.CosDocument.AllObjectsWithinDepthLimit;
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        var registry = services.GetService<ICommandRegistry>();
        if (registry is not null && services.GetService<IDocumentManager>() is not null)
            new SecurityCommands(services, registry).RegisterAll();
    }
}

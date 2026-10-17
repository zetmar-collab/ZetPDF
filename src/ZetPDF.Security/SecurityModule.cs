using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security;

/// <summary>
/// Moduł ZetPDF.Security (właściciel: security-signatures). Rejestracja w App jest stała (ModuleCatalog) – tu tylko wypełniamy usługi i polecenia.
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
        services.AddSingleton<Signatures.ITimestampClient, Signatures.HttpTimestampClient>();
        services.AddSingleton<ISignatureService, SignatureService>();
        services.AddSingleton<ICompareService, CompareService>();
        services.AddSingleton<IActionWizard, ActionWizard>();
    }

    public void Initialize(IServiceProvider services)
    {
        services.GetRequiredService<ILocalizationService>().RegisterResources(Strings);
        // security-signatures: tu podpinamy obsługę poleceń z katalogu, np.:
        // var registry = services.GetRequiredService<ICommandRegistry>();
        // registry.Register(CommandIds.Security.PasswordProtect, new ActiveDocumentCommand(...));
        // Polecenia bez obsługi są na wstążce wyszarzone.
    }
}

using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.Tests;

/// <summary>Test dymny szkieletu – właściciel modułu dopisuje tu testy swojego silnika.</summary>
public class ModuleSmokeTests
{
    [Fact]
    public void Module_registers_its_contracts()
    {
        var services = new ServiceCollection();
        new ZetPDF.Security.SecurityModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(ISecurityService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISignatureService));
        Assert.Contains(services, d => d.ServiceType == typeof(ICompareService));
        Assert.Contains(services, d => d.ServiceType == typeof(IActionWizard));
    }
}

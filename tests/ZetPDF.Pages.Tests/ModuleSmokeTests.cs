using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Tests;

/// <summary>Test dymny szkieletu – właściciel modułu dopisuje tu testy swojego silnika.</summary>
public class ModuleSmokeTests
{
    [Fact]
    public void Module_registers_its_contracts()
    {
        var services = new ServiceCollection();
        new ZetPDF.Pages.PagesModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(IPageOperations));
        Assert.Contains(services, d => d.ServiceType == typeof(IStampingService));
        Assert.Contains(services, d => d.ServiceType == typeof(IOptimizerService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISanitizeService));
    }
}

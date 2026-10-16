using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Tests;

/// <summary>Test dymny szkieletu – właściciel modułu dopisuje tu testy swojego silnika.</summary>
public class ModuleSmokeTests
{
    [Fact]
    public void Module_registers_its_contracts()
    {
        var services = new ServiceCollection();
        new ZetPDF.Editing.EditingModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(ITextEditService));
        Assert.Contains(services, d => d.ServiceType == typeof(IObjectEditService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILinkService));
        Assert.Contains(services, d => d.ServiceType == typeof(IConversionService));
        Assert.Contains(services, d => d.ServiceType == typeof(IRedactionService));
    }
}

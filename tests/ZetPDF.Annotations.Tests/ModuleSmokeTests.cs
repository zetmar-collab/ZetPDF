using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Tests;

/// <summary>Test dymny szkieletu – właściciel modułu dopisuje tu testy swojego silnika.</summary>
public class ModuleSmokeTests
{
    [Fact]
    public void Module_registers_its_contracts()
    {
        var services = new ServiceCollection();
        new ZetPDF.Annotations.AnnotationsModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(IAnnotationService));
        Assert.Contains(services, d => d.ServiceType == typeof(IFormService));
        Assert.Contains(services, d => d.ServiceType == typeof(IAttachmentService));
    }
}

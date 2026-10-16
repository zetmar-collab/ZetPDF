using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.Tests;

/// <summary>Test dymny szkieletu – właściciel modułu dopisuje tu testy swojego silnika.</summary>
public class ModuleSmokeTests
{
    [Fact]
    public void Module_registers_its_contracts()
    {
        var services = new ServiceCollection();
        new ZetPDF.Viewer.ViewerModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(IDocumentViewFactory));
        Assert.Contains(services, d => d.ServiceType == typeof(IViewerService));
        Assert.Contains(services, d => d.ServiceType == typeof(IToolService));
        Assert.Contains(services, d => d.ServiceType == typeof(IBookmarkService));
        Assert.Contains(services, d => d.ServiceType == typeof(IPrintService));
    }
}

public class RenderingSmokeTests
{
    [Fact]
    public void Rendering_module_registers_renderer()
    {
        var services = new ServiceCollection();
        new ZetPDF.Rendering.RenderingModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(IPdfRenderer));
    }
}

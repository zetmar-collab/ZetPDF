using ZetPDF.App;

namespace ZetPDF.App.Tests;

/// <summary>Test dymny szkieletu – ui-shell dopisuje testy powłoki (motywy, lokalizacja, skróty).</summary>
public class ShellSmokeTests
{
    [Fact]
    public void Module_catalog_lists_all_modules() => Assert.Equal(7, ModuleCatalog.CreateAll().Count);
}

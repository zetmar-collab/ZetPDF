using System.Collections;
using System.Globalization;
using System.Resources;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.Core.Theming;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Testy szkieletu (architekt). Tester rozbudowuje ten projekt o testy funkcji etapów 1 i 2.
/// </summary>
public class SkeletonIntegrationTests
{
    private static ServiceProvider BuildAll(out IReadOnlyList<IFeatureModule> modules)
    {
        modules = ZetPDF.App.ModuleCatalog.CreateAll();
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        foreach (var m in modules) m.ConfigureServices(s);
        s.AddSingleton<ZetPDF.Viewer.ReadAloud.ISpeechEngineFactory, ZetPDF.Viewer.ReadAloud.SilentSpeechEngineFactory>(); // bez dźwięku w testach
        return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    public static IEnumerable<object[]> ContractTypes() => new[]
    {
        typeof(IDocumentManager), typeof(ICommandRegistry), typeof(IPanelRegistry), typeof(ILocalizationService), typeof(ISettingsService),
        typeof(IRecentFilesService), typeof(IAppPaths), typeof(IFileService), typeof(IMetadataService),
        typeof(IPdfRenderer), typeof(IDocumentViewFactory), typeof(IViewerService), typeof(IToolService), typeof(IBookmarkService), typeof(IPrintService),
        typeof(IPageOperations), typeof(IStampingService), typeof(IOptimizerService), typeof(ISanitizeService),
        typeof(IAnnotationService), typeof(IFormService), typeof(IAttachmentService),
        typeof(ITextEditService), typeof(IObjectEditService), typeof(ILinkService), typeof(IConversionService), typeof(IRedactionService),
        typeof(IOcrService), typeof(ISecurityService), typeof(ISignatureService), typeof(ICompareService), typeof(IActionWizard),
    }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void Every_contract_has_a_registered_implementation(Type contract)
    {
        using var sp = BuildAll(out _);
        Assert.NotNull(sp.GetService(contract));
    }

    [Fact]
    public void Integration_hosts_never_speak_to_audio_device()
    {
        using var host = new ZetPDF.IntegrationTests.Infrastructure.TestHost();
        Assert.IsType<ZetPDF.Viewer.ReadAloud.SilentSpeechEngineFactory>(host.Get<ZetPDF.Viewer.ReadAloud.ISpeechEngineFactory>());
        using var sp = BuildAll(out _);
        Assert.IsType<ZetPDF.Viewer.ReadAloud.SilentSpeechEngineFactory>(sp.GetRequiredService<ZetPDF.Viewer.ReadAloud.ISpeechEngineFactory>());
    }

    [Fact]
    public void Each_module_id_appears_once()
    {
        var modules = ZetPDF.App.ModuleCatalog.CreateAll();
        Assert.Equal(modules.Count, modules.Select(m => m.Id).Distinct().Count());
        Assert.Equal(7, modules.Count);
    }

    private static readonly (string Assembly, string BaseName)[] ResourceSets =
    {
        ("ZetPDF.Core", "ZetPDF.Core.Resources.Strings"),
        ("ZetPDF", "ZetPDF.App.Resources.Strings"),
        ("ZetPDF.Rendering", "ZetPDF.Rendering.Resources.Strings"),
        ("ZetPDF.Viewer", "ZetPDF.Viewer.Resources.Strings"),
        ("ZetPDF.Pages", "ZetPDF.Pages.Resources.Strings"),
        ("ZetPDF.Annotations", "ZetPDF.Annotations.Resources.Strings"),
        ("ZetPDF.Editing", "ZetPDF.Editing.Resources.Strings"),
        ("ZetPDF.Ocr", "ZetPDF.Ocr.Resources.Strings"),
        ("ZetPDF.Security", "ZetPDF.Security.Resources.Strings"),
    };

    private static Dictionary<string, string?> Read(ResourceManager rm, CultureInfo c) =>
        rm.GetResourceSet(c, true, false)!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value);

    [Fact]
    public void All_resource_files_have_matching_pl_and_en_keys_without_duplicates_across_modules()
    {
        _ = BuildAll(out _); // wymusza załadowanie zestawów
        var all = new Dictionary<string, string>();
        foreach (var (asmName, baseName) in ResourceSets)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName)
                      ?? System.Reflection.Assembly.Load(asmName);
            var rm = new ResourceManager(baseName, asm);
            var en = Read(rm, CultureInfo.InvariantCulture);
            var pl = Read(rm, new CultureInfo("pl"));
            Assert.True(en.Count > 0, baseName);
            var missingPl = en.Keys.Except(pl.Keys).ToList();
            var missingEn = pl.Keys.Except(en.Keys).ToList();
            Assert.True(missingPl.Count == 0, $"{baseName}: brak w pl: {string.Join(", ", missingPl)}");
            Assert.True(missingEn.Count == 0, $"{baseName}: brak w en: {string.Join(", ", missingEn)}");
            foreach (var (k, v) in en.Concat(pl)) Assert.False(string.IsNullOrWhiteSpace(v), $"{baseName}: pusty {k}");
            foreach (var k in en.Keys)
            {
                Assert.False(all.TryGetValue(k, out var other), $"Klucz {k} w {baseName} i {other}");
                all[k] = baseName;
            }
        }
    }

    [Fact]
    public void Every_catalog_command_tab_and_group_has_a_label()
    {
        var rm = new ResourceManager("ZetPDF.App.Resources.Strings", typeof(ZetPDF.App.ModuleCatalog).Assembly);
        var en = Read(rm, CultureInfo.InvariantCulture);
        foreach (var c in RibbonCatalog.Commands) Assert.True(en.ContainsKey(c.TextKey), c.TextKey);
        foreach (var t in RibbonCatalog.Tabs)
        {
            Assert.True(en.ContainsKey(t.TextKey), t.TextKey);
            foreach (var g in t.Groups) Assert.True(en.ContainsKey(g.TextKey), g.TextKey);
        }
    }

    [Fact]
    public void Theme_dictionaries_define_every_theme_key()
    {
        var dir = FindRepoFile(Path.Combine("src", "ZetPDF.App", "Themes"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in new[] { "Light.xaml", "Dark.xaml" })
        {
            var keys = XDocument.Load(Path.Combine(dir, file)).Root!.Elements().Select(e => (string?)e.Attribute(x + "Key")).ToHashSet();
            foreach (var k in ThemeKeys.AllBrushes.Concat(ThemeKeys.AllFonts)) Assert.True(keys.Contains(k), $"{file}: brak {k}");
        }
    }

    internal static string FindRepoFile(string relative)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidate = Path.Combine(d.FullName, relative);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException(relative);
    }
}

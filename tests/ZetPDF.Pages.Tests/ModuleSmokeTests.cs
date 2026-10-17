using System.Resources;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Pages.Tests;

public class ModuleSmokeTests
{
    [Fact]
    public void Module_registers_its_contracts()
    {
        var services = new ServiceCollection();
        new PagesModule().ConfigureServices(services);
        Assert.Contains(services, d => d.ServiceType == typeof(IPageOperations));
        Assert.Contains(services, d => d.ServiceType == typeof(IStampingService));
        Assert.Contains(services, d => d.ServiceType == typeof(IOptimizerService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISanitizeService));
    }

    [Fact]
    public void Every_pages_command_from_catalog_has_a_handler()
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        s.AddSingleton<IDialogService, NoDialogs>();
        new ZetPDF.Rendering.RenderingModule().ConfigureServices(s);
        var module = new PagesModule();
        module.ConfigureServices(s);
        using var sp = s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        module.Initialize(sp);
        var registry = sp.GetRequiredService<ICommandRegistry>();
        var ours = RibbonCatalog.Commands.Where(c => c.Module == FeatureModuleId.Pages).Select(c => c.Id).ToList();
        Assert.Equal(40, ours.Count);
        Assert.All(ours, id => Assert.NotNull(registry.GetHandler(id)));
    }

    [Fact]
    public void Resources_have_the_same_keys_in_english_and_polish()
    {
        var rm = PagesModule.Strings;
        var en = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var pl = rm.GetResourceSet(new System.Globalization.CultureInfo("pl"), true, false)!.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        Assert.Equal(en.Keys.OrderBy(k => k), pl.Keys.OrderBy(k => k));
        Assert.All(en.Keys, k => Assert.StartsWith("Pages.", k));
        // formaty z argumentami: ta sama liczba miejsc {n}
        foreach (var k in en.Keys)
            Assert.Equal(System.Text.RegularExpressions.Regex.Matches(en[k], @"\{\d\}").Count, System.Text.RegularExpressions.Regex.Matches(pl[k], @"\{\d\}").Count);
    }

    private sealed class NoDialogs : IDialogService
    {
        public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null) => null;
        public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null) => null;
        public string? SelectFolder(string title, string? initialDirectory = null) => null;
        public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) { }
        public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) => false;
        public YesNoCancel AskYesNoCancel(string text, string? title = null) => YesNoCancel.Cancel;
        public string? AskPassword(string documentName, bool isRetry) => null;
        public Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true) => Task.FromResult(false);
        public bool? ShowDialog(Window dialog) => false;
        public void ShowError(Exception exception, string? contextText = null) { }
    }
}

using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Editing.Indexing;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Polecenia indeksu z wstążki: informacja o tekście na dysku przy budowaniu i okno zarządzania.</summary>
public class IndexCommandsTests
{
    [Fact]
    public void Build_prompt_tells_about_text_on_disk_and_manage_opens_window() => Sta.Run(async () =>
    {
        using var host = new TestHost(withShellFakes: true, initializeModules: true);
        var loc = host.Get<ILocalizationService>();
        var registry = host.Get<ICommandRegistry>();
        var folder = Path.GetDirectoryName(TestPaths.WriteSample(SamplePdfFactory.Text(1)))!;
        host.Dialogs.FolderToSelect = folder;

        Assert.True(registry.TryExecute(CommandIds.Index.BuildFolder));
        var index = host.Get<FullTextIndex>();
        await Sta.WaitFor(() => File.Exists(index.IndexFileFor(folder)), what: "indeks");

        Assert.Contains(host.Dialogs.Questions, q => q.Contains(loc["Edit.Index.Privacy"], StringComparison.Ordinal) && q.Contains(folder, StringComparison.Ordinal));

        await Sta.WaitFor(() => registry.GetHandler(CommandIds.Index.Manage)!.CanExecute(null), what: "polecenie aktywne");
        Assert.True(registry.TryExecute(CommandIds.Index.Manage));
        await Sta.WaitFor(() => host.Dialogs.DialogsShown.Any(t => t.Name == "IndexManageWindow"), what: "okno zarządzania");
        Assert.Empty(host.Dialogs.Errors);
    });
}

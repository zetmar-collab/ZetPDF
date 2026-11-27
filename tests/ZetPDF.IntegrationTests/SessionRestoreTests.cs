using Microsoft.Extensions.Logging.Abstractions;
using ZetPDF.App.Shell;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Przywracanie sesji: zapamiętanie otwartych plików przy zamykaniu i ponowne otwarcie przy starcie.</summary>
public class SessionRestoreTests
{
    private static SessionRestore Restore(TestHost host) =>
        new(host.Documents, host.Get<ISettingsService>(), host.Services, NullLogger<SessionRestore>.Instance);

    private static string Sample(string name, int pages = 2)
    {
        var path = Path.Combine(TestPaths.NewDirectory(), name);
        File.WriteAllBytes(path, SamplePdfFactory.Text(pages));
        return path;
    }

    [Fact]
    public async Task Capture_and_restore_reopens_files_and_active_tab_skipping_missing_and_unsaved()
    {
        var a = Sample("pierwszy ą.pdf");
        var b = Sample("drugi.pdf");
        var gone = Sample("usunięty.pdf");
        List<SessionEntry> saved;
        using (var host = new TestHost(withShellFakes: true))
        {
            host.Get<ISettingsService>().Update(s => s.RestoreSession = true);
            await host.Documents.OpenAsync(a);
            var second = await host.Documents.OpenAsync(b);
            await host.Documents.OpenAsync(gone);
            await host.Documents.CreateNewAsync(SamplePdfFactory.Text(1), "nowy.pdf"); // bez pliku – pomijany
            host.Documents.ActiveDocument = second;
            Restore(host).Capture();
            saved = host.Get<ISettingsService>().Current.LastSession;
        }
        Assert.Equal(3, saved.Count);
        Assert.True(saved.Single(e => e.Path.EndsWith("drugi.pdf")).Active);
        File.Delete(gone);

        using var next = new TestHost(withShellFakes: true);
        next.Get<ISettingsService>().Update(s => { s.RestoreSession = true; s.LastSession = saved; });
        var opened = new List<string>();
        await Restore(next).RestoreAsync(async paths =>
        {
            foreach (var p in paths) { opened.Add(p); await next.Documents.OpenAsync(p); }
        });
        Assert.Equal(new[] { a, b }, opened);
        Assert.EndsWith("drugi.pdf", next.Documents.ActiveDocument!.FilePath);
    }

    [Fact]
    public async Task Disabled_option_clears_list_and_restores_nothing()
    {
        using var host = new TestHost(withShellFakes: true);
        var settings = host.Get<ISettingsService>();
        settings.Update(s => s.LastSession = new() { new SessionEntry { Path = Sample("x.pdf") } });
        await host.Documents.OpenAsync(Sample("y.pdf"));
        Restore(host).Capture();
        Assert.Empty(settings.Current.LastSession);

        bool called = false;
        await Restore(host).RestoreAsync(_ => { called = true; return Task.CompletedTask; });
        Assert.False(called);
    }
}

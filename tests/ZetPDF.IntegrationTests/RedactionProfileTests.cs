using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.Security;
using ZetPDF.Security.Actions;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Profile redakcji: zapis w ustawieniach i krok Kreatora działań „Redakcja według profilu”.</summary>
public class RedactionProfileTests
{
    [Fact]
    public void Store_saves_replaces_normalizes_and_deletes_profiles()
    {
        using var host = new TestHost();
        var store = host.Get<RedactionProfileStore>();
        Assert.True(store.Save(new RedactionProfile { Name = "  Kadry ą ", Phrases = new[] { " Jan Kowalski ", "", "Jan Kowalski" }, Patterns = SensitiveDataPattern.Pesel | (SensitiveDataPattern)1024 }));
        var p = Assert.Single(store.All);
        Assert.Equal("Kadry ą", p.Name);
        Assert.Equal(new[] { "Jan Kowalski" }, p.Phrases);
        Assert.Equal(SensitiveDataPattern.Pesel, p.Patterns);

        Assert.True(store.Save(new RedactionProfile { Name = "KADRY Ą", Patterns = SensitiveDataPattern.Email, WholeWord = true }));
        p = Assert.Single(store.All);
        Assert.Equal(SensitiveDataPattern.Email, p.Patterns);
        Assert.Equal(TextSearchOptions.WholeWord, store.Find("kadry ą")!.ToSearch().Options);

        Assert.False(store.Save(new RedactionProfile { Name = "   " }));
        store.Delete("Kadry ą");
        Assert.Empty(store.All);
    }

    [Fact]
    public void Store_refuses_more_than_limit()
    {
        using var host = new TestHost();
        var store = host.Get<RedactionProfileStore>();
        for (int i = 0; i < RedactionProfileStore.MaxProfiles; i++) Assert.True(store.Save(new RedactionProfile { Name = "P" + i, Patterns = SensitiveDataPattern.Nip }));
        Assert.False(store.Save(new RedactionProfile { Name = "Nadmiarowy", Patterns = SensitiveDataPattern.Nip }));
        Assert.True(store.Save(new RedactionProfile { Name = "P0", Patterns = SensitiveDataPattern.Email })); // zastąpienie istniejącego
    }

    [Fact]
    public async Task Wizard_redact_step_removes_profile_matches_from_text()
    {
        using var host = new TestHost();
        var sample = SamplePdfFactory.WithSensitiveData();
        host.Get<RedactionProfileStore>().Save(new RedactionProfile { Name = "Dane osobowe", Patterns = SensitiveDataPattern.Pesel | SensitiveDataPattern.Email });
        var action = new ActionDefinition
        {
            Id = ActionWizard.NewId(),
            Name = "Redakcja wsadowa",
            Steps = new[] { ActionWizard.Step(ActionStepTypes.Redact, new RedactStepOptions { Profile = "dane OSOBOWE" }) },
        };
        var input = TestPaths.WriteSample(sample.Pdf!, "dane wrażliwe ą.pdf");
        var report = await host.Get<IActionWizard>().RunAsync(action, new[] { input }, new ActionRunOptions { OutputDirectory = TestPaths.NewDirectory(), FileNameSuffix = "_zred" });
        var r = Assert.Single(report.Files);
        Assert.True(r.Success, r.Error);
        var text = await Inspect.PdfiumText(File.ReadAllBytes(r.OutputPath!));
        Assert.DoesNotContain(sample.Pesel, text);
        Assert.DoesNotContain(sample.Email, text);
        Assert.Contains(sample.Nip, text); // poza profilem
    }

    [Fact]
    public async Task Wizard_redact_step_with_missing_profile_reports_error()
    {
        using var host = new TestHost();
        var action = new ActionDefinition
        {
            Id = ActionWizard.NewId(),
            Name = "Brak profilu",
            Steps = new[] { ActionWizard.Step(ActionStepTypes.Redact, new RedactStepOptions { Profile = "nie istnieje" }) },
        };
        var input = TestPaths.WriteSample(SamplePdfFactory.Text(1), "a.pdf");
        var report = await host.Get<IActionWizard>().RunAsync(action, new[] { input }, new ActionRunOptions { OutputDirectory = TestPaths.NewDirectory() });
        var r = Assert.Single(report.Files);
        Assert.False(r.Success);
        Assert.Contains("nie istnieje", r.Error);
    }
}

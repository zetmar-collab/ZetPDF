using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.Security;
using ZetPDF.TestData;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Test dymny Kreatora działań po scaleniu: wbudowane działania na PRAWDZIWYCH usługach z DI (wszystkie moduły
/// z ModuleCatalog, bez atrap) na dwóch plikach z TestData.
/// </summary>
public class ActionWizardSmokeTests
{
    private static ServiceProvider BuildAll()
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        foreach (var m in ZetPDF.App.ModuleCatalog.CreateAll()) m.ConfigureServices(s);
        return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static string Latin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static string AllText(byte[] pdf)
    {
        using var pig = PigDocument.Open(pdf);
        return string.Join("\n", pig.GetPages().Select(p => p.Text));
    }

    private static async Task<(IReadOnlyList<byte[]> Outputs, string[] Inputs)> RunAsync(ServiceProvider sp, string actionId, string suffix)
    {
        var wizard = sp.GetRequiredService<IActionWizard>();
        Assert.IsType<ActionWizard>(wizard);
        var action = (await wizard.GetActionsAsync()).Single(a => a.Id == actionId);
        var dir = TestPaths.NewDirectory();
        var inputs = new[]
        {
            TestPaths.WriteSample(SamplePdfFactory.WithMetadataAttachmentAndJavaScript(), "metadane i skrypt.pdf"),
            TestPaths.WriteSample(SamplePdfFactory.Scanned(), "skan zażółć.pdf"),
        };
        var report = await wizard.RunAsync(action, inputs, new ActionRunOptions { OutputDirectory = Path.Combine(dir, "wynik ąę"), FileNameSuffix = suffix });
        Assert.True(report.Failed == 0, string.Join("; ", report.Files.Where(f => !f.Success).Select(f => f.InputPath + ": " + f.Error)));
        Assert.Equal(2, report.Succeeded);
        var outputs = new List<byte[]>();
        var renderer = sp.GetRequiredService<IPdfRenderer>();
        foreach (var f in report.Files)
        {
            Assert.NotEqual(f.InputPath, f.OutputPath);
            var bytes = await File.ReadAllBytesAsync(f.OutputPath!);
            using var doc = await renderer.OpenAsync(bytes); // wynik otwiera się w PDFium
            Assert.True(doc.PageCount >= 1);
            var bmp = await doc.RenderPageAsync(0, new RenderRequest(0.5));
            Assert.True(bmp.Width > 0);
            outputs.Add(bytes);
        }
        return (outputs, inputs);
    }

    [Fact]
    public async Task Prepare_for_distribution_removes_metadata_scripts_attachments_and_adds_watermark_and_header()
    {
        using var sp = BuildAll();
        var (outputs, _) = await RunAsync(sp, ActionWizard.DistributeId, "_dystrybucja");
        var meta = sp.GetRequiredService<IMetadataService>();
        var attachments = sp.GetRequiredService<IAttachmentService>();
        var stamping = sp.GetRequiredService<IStampingService>();
        for (int i = 0; i < outputs.Count; i++)
        {
            var pdf = outputs[i];
            var m = await meta.GetAsync(pdf);
            Assert.Equal(string.Empty, m.Title);
            Assert.Equal(string.Empty, m.Subject);
            Assert.Equal(string.Empty, m.Keywords);
            Assert.DoesNotContain("/JavaScript", Latin1(pdf));
            Assert.Empty(await attachments.GetAttachmentsAsync(pdf));
            var marks = await stamping.DetectMarksAsync(pdf);
            Assert.True(marks.HasFlag(PageMarkKind.Watermark), "watermark");
            Assert.True(marks.HasFlag(PageMarkKind.HeaderFooter), "header/footer");
            var text = AllText(pdf);
            Assert.True(text.Contains("POUFNE") || text.Contains("CONFIDENTIAL"), "watermark text");
        }
        Assert.Contains("metadane i skrypt", AllText(outputs[0])); // <<file>> w nagłówku
    }

    [Fact]
    public async Task Optimize_for_web_produces_valid_not_larger_files()
    {
        using var sp = BuildAll();
        var (outputs, inputs) = await RunAsync(sp, ActionWizard.WebId, "_web");
        for (int i = 0; i < outputs.Count; i++)
        {
            var original = await File.ReadAllBytesAsync(inputs[i]);
            Assert.True(outputs[i].Length <= original.Length, $"{outputs[i].Length} > {original.Length}");
            using var a = PigDocument.Open(original);
            using var b = PigDocument.Open(outputs[i]);
            Assert.Equal(a.NumberOfPages, b.NumberOfPages);
        }
        Assert.Contains("Strona 1", AllText(outputs[0]));
    }

    [Fact]
    public async Task Archive_fills_missing_title_keeps_existing_and_removes_javascript()
    {
        using var sp = BuildAll();
        var (outputs, _) = await RunAsync(sp, ActionWizard.ArchiveId, "_archiwum");
        var meta = sp.GetRequiredService<IMetadataService>();
        Assert.Equal("Tajny tytuł", (await meta.GetAsync(outputs[0])).Title); // tylko puste pola są uzupełniane
        Assert.Equal("skan zażółć", (await meta.GetAsync(outputs[1])).Title);
        Assert.All(outputs, pdf => Assert.DoesNotContain("/JavaScript", Latin1(pdf)));
        // archiwizacja nie usuwa załączników ani komentarzy
        Assert.NotEmpty(await sp.GetRequiredService<IAttachmentService>().GetAttachmentsAsync(outputs[0]));
    }
}

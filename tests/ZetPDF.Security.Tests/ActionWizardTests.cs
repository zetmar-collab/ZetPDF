using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Services;
using ZetPDF.Security.Actions;
using ZetPDF.TestData;

namespace ZetPDF.Security.Tests;

public class ActionWizardTests
{
    private sealed class FakeSanitize : ISanitizeService
    {
        public int Calls;
        public Task<SanitizeResult> SanitizeAsync(byte[] pdf, SanitizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var r = PdfSharpEdit.Modify(pdf, d => d.Info.Subject = "oczyszczony JS=" + options.JavaScript);
            return Task.FromResult(new SanitizeResult(r, 0, 0, 0, options.JavaScript ? 1 : 0, 0, 0));
        }
    }

    private sealed class FakeStamping : IStampingService
    {
        public List<string> Log { get; } = new();
        public Task<byte[]> AddWatermarkAsync(byte[] pdf, WatermarkOptions o, IProgress<OperationProgress>? p = null, CancellationToken ct = default)
        { lock (Log) Log.Add("wm:" + o.Text); return Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.Info.Keywords += "[wm:" + o.Text + "]")); }
        public Task<byte[]> AddHeaderFooterAsync(byte[] pdf, HeaderFooterOptions o, IProgress<OperationProgress>? p = null, CancellationToken ct = default)
        { lock (Log) Log.Add("hf:" + o.HeaderLeft); return Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.Info.Keywords += "[hf:" + o.HeaderLeft + "]")); }
        public Task<byte[]> RemoveWatermarksAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> AddBackgroundAsync(byte[] pdf, BackgroundOptions o, IProgress<OperationProgress>? p = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> RemoveBackgroundsAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> RemoveHeaderFooterAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> AddPageNumbersAsync(byte[] pdf, PageNumberOptions o, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BatesResult> AddBatesAsync(byte[] pdf, BatesOptions o, long startNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> RemoveBatesAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PageMarkKind> DetectMarksAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> RemovePageNumbersAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<T?> GetStoredOptionsAsync<T>(byte[] pdf, PageMarkKind kind, CancellationToken ct = default) where T : class => throw new NotImplementedException();
    }

    private sealed class NotReadyOptimizer : IOptimizerService
    {
        public Task<OptimizeResult> OptimizeAsync(byte[] pdf, OptimizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> ConvertToGrayscaleAsync(byte[] pdf, bool rasterize, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static (ActionWizard Wizard, FakeSanitize Sanitize, FakeStamping Stamping, AppPaths Paths) Create()
    {
        var paths = TestEnv.Paths();
        var sanitize = new FakeSanitize();
        var stamping = new FakeStamping();
        var services = new ServiceCollection()
            .AddSingleton<IAppPaths>(paths)
            .AddSingleton<IFileService>(new FileService(paths))
            .AddSingleton<ISanitizeService>(sanitize)
            .AddSingleton<IStampingService>(stamping)
            .AddSingleton<IOptimizerService, NotReadyOptimizer>()
            .AddSingleton<ISecurityService, SecurityService>()
            .AddSingleton<IMetadataService, MetadataService>()
            .BuildServiceProvider();
        return (new ActionWizard(services, paths), sanitize, stamping, paths);
    }

    [Fact]
    public async Task Prepare_for_distribution_runs_on_many_files_with_polish_paths_without_touching_originals()
    {
        var (wizard, sanitize, stamping, _) = Create();
        var dir = TestPaths.NewDirectory();
        var inputs = new[] { "zażółć gęślą.pdf", "Raport roczny ąę.pdf", "trzeci plik.pdf" }
            .Select((n, i) => { var p = Path.Combine(dir, n); File.WriteAllBytes(p, SamplePdfFactory.Text(i + 1)); return p; }).ToList();
        var hashes = inputs.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToList();

        var action = (await wizard.GetActionsAsync()).Single(a => a.Id == ActionWizard.DistributeId);
        var progress = new List<OperationProgress>();
        var report = await wizard.RunAsync(action, inputs, new ActionRunOptions { OutputDirectory = dir }, new SyncProgress(progress.Add));

        Assert.Equal(3, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.Equal(3, sanitize.Calls);
        Assert.Contains("hf:zażółć gęślą", stamping.Log); // makro <<file>> podstawione nazwą pliku
        for (int i = 0; i < inputs.Count; i++)
        {
            Assert.Equal(hashes[i], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputs[i])))); // oryginał bez zmian
            var outPath = report.Files[i].OutputPath!;
            Assert.NotEqual(inputs[i], outPath, StringComparer.OrdinalIgnoreCase);
            Assert.EndsWith(" (1).pdf", outPath);
            var meta = await new MetadataService().GetAsync(File.ReadAllBytes(outPath));
            Assert.StartsWith("oczyszczony", meta.Subject);
            Assert.Contains("[wm:", meta.Keywords);
        }
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Output_folder_with_suffix_errors_are_reported_per_file()
    {
        var (wizard, _, _, _) = Create();
        var dir = TestPaths.NewDirectory();
        var good = Path.Combine(dir, "dobry.pdf");
        File.WriteAllBytes(good, SamplePdfFactory.Text(1));
        var encrypted = Path.Combine(dir, "zaszyfrowany.pdf");
        File.WriteAllBytes(encrypted, SamplePdfFactory.EncryptedAes256());
        var broken = Path.Combine(dir, "uszkodzony.pdf");
        File.WriteAllBytes(broken, SamplePdfFactory.NotAPdf());
        var outDir = Path.Combine(dir, "wynik żółty");

        var action = new ActionDefinition
        {
            Id = ActionWizard.NewId(), Name = "Archiwum",
            Steps = new[]
            {
                ActionWizard.Step(ActionStepTypes.Metadata, new MetadataStepOptions { Author = "Zespół ąę" }),
                ActionWizard.Step(ActionStepTypes.Encrypt, new EncryptStepOptions()),
            },
        };
        Assert.True(ActionWizard.NeedsPasswords(action));
        var run = ActionWizard.WithPasswords(action, "u", "o");
        var report = await wizard.RunAsync(run, new[] { good, encrypted, broken }, new ActionRunOptions { OutputDirectory = outDir, FileNameSuffix = "_arch" });

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(2, report.Failed);
        var ok = report.Files[0];
        Assert.Equal(Path.Combine(outDir, "dobry_arch.pdf"), ok.OutputPath);
        var info = await new SecurityService().AnalyzeAsync(File.ReadAllBytes(ok.OutputPath!), "u");
        Assert.True(info.IsEncrypted);
        Assert.False(string.IsNullOrEmpty(report.Files[1].Error));
        Assert.False(string.IsNullOrEmpty(report.Files[2].Error));

        // drugi przebieg nie nadpisuje istniejącego wyniku
        var again = await wizard.RunAsync(run, new[] { good }, new ActionRunOptions { OutputDirectory = outDir, FileNameSuffix = "_arch" });
        Assert.Equal(Path.Combine(outDir, "dobry_arch (1).pdf"), again.Files[0].OutputPath);
        // za zgodą – nadpisuje
        var overwrite = await wizard.RunAsync(run, new[] { good }, new ActionRunOptions { OutputDirectory = outDir, FileNameSuffix = "_arch", OverwriteExisting = true });
        Assert.Equal(Path.Combine(outDir, "dobry_arch.pdf"), overwrite.Files[0].OutputPath);
    }

    [Fact]
    public async Task Missing_module_gives_readable_error_and_cancellation_stops()
    {
        var (wizard, _, _, _) = Create();
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(1));
        var web = (await wizard.GetActionsAsync()).Single(a => a.Id == ActionWizard.WebId);
        var report = await wizard.RunAsync(web, new[] { path }, new ActionRunOptions { OutputDirectory = Path.GetDirectoryName(path)! });
        Assert.Equal(1, report.Failed);
        Assert.Equal(SecurityModule.Strings.GetString("Security.Action.Error.NotAvailable", System.Globalization.CultureInfo.CurrentUICulture), report.Files[0].Error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wizard.RunAsync(web, new[] { path }, new ActionRunOptions { OutputDirectory = Path.GetDirectoryName(path)! }, null, cts.Token));
    }

    [Fact]
    public async Task Save_load_delete_action_json_without_passwords()
    {
        var (wizard, _, _, paths) = Create();
        var action = new ActionDefinition
        {
            Id = ActionWizard.NewId(), Name = "Moje działanie ąę",
            Steps = new[]
            {
                ActionWizard.Step(ActionStepTypes.Watermark, new WatermarkOptions { Text = "KOPIA", Opacity = 0.5, Style = new TextStyle { Color = PdfColor.Red } }),
                ActionWizard.Step(ActionStepTypes.Encrypt, new EncryptStepOptions { UserPassword = "tajne", OwnerPassword = "tajne2" }),
            },
        };
        await wizard.SaveActionAsync(action);
        var json = File.ReadAllText(Directory.GetFiles(paths.ActionsDirectory, "*.json").Single());
        Assert.DoesNotContain("tajne", json);

        var loaded = (await wizard.GetActionsAsync()).Single(a => a.Id == action.Id);
        Assert.Equal("Moje działanie ąę", loaded.Name);
        var wm = ActionWizard.ReadOptions<WatermarkOptions>(loaded.Steps[0]);
        Assert.Equal("KOPIA", wm.Text);
        Assert.Equal(PdfColor.Red, wm.Style.Color);
        Assert.Equal(3, (await wizard.GetActionsAsync()).Count(a => a.IsBuiltIn));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wizard.SaveActionAsync(loaded with { Id = ActionWizard.ArchiveId, IsBuiltIn = true }));

        await wizard.DeleteActionAsync(action.Id);
        Assert.DoesNotContain(await wizard.GetActionsAsync(), a => a.Id == action.Id);
        Assert.All(wizard.StepTypes.Values, k => Assert.True(SecurityModule.Strings.GetString(k) is not null, k));
    }

    private sealed class SyncProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> _a;
        public SyncProgress(Action<OperationProgress> a) => _a = a;
        public void Report(OperationProgress value) => _a(value);
    }
}

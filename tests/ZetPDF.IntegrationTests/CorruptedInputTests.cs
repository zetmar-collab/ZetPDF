using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.Security;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Uszkodzone pliki: brak zawieszenia i brak wyjątków „programistycznych” (NullReference, IndexOutOfRange…).
/// Otwarcie kończy się <see cref="PdfCorruptException"/> albo (PDFium naprawia plik) działającą sesją.
/// </summary>
public class CorruptedInputTests
{
    public static IEnumerable<object[]> BrokenNames() => ExtraSamples.Broken().Select(b => new object[] { b.Name });

    private static byte[] Broken(string name) => ExtraSamples.Broken().Single(b => b.Name == name).Data;

    /// <summary>Wyjątki świadczące o błędzie w kodzie, a nie o złych danych.</summary>
    internal static void AssertNotProgrammingError(Exception? ex, string what)
    {
        if (ex is null) return;
        var e = ex is AggregateException ag && ag.InnerExceptions.Count == 1 ? ag.InnerException! : ex;
        Assert.False(e is NullReferenceException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidCastException or AccessViolationException or StackOverflowException,
            $"{what}: {e.GetType().FullName}: {e.Message}\n{e.StackTrace}");
    }

    private static async Task<Exception?> Capture(Func<Task> f, int timeoutMs = 60000)
    {
        var task = Task.Run(f);
        var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(done == task, "operacja zawiesiła się na uszkodzonym pliku");
        try { await task; return null; }
        catch (Exception ex) { return ex; }
    }

    [Theory]
    [MemberData(nameof(BrokenNames))]
    public async Task Open_gives_domain_exception_or_working_session(string name)
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(Broken(name), $"uszkodzony {name}.pdf");
        IPdfDocumentSession? session = null;
        var ex = await Capture(async () => session = await host.Documents.OpenAsync(path));
        if (ex is not null)
        {
            Assert.True(ex is PdfCorruptException, $"oczekiwano PdfCorruptException, jest {ex.GetType().FullName}: {ex.Message}");
            Assert.Empty(host.Documents.Documents);
            return;
        }
        Assert.NotNull(session);
        // Sesja z naprawionego pliku: operacje modułów działają albo kończą się czytelnym błędem, dokument się nie psuje.
        foreach (var opName in new[] { "Pages.Rotate", "Pages.Watermark", "Annot.Note", "Edit.AddText", "Pages.Sanitize", "Core.Metadata" })
        {
            var op = Operations.Get(opName);
            var before = session!.Current;
            var err = await Capture(() => session.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.All, op.Permission)));
            AssertNotProgrammingError(err, $"{name} / {opName}");
            if (err is not null) Assert.Same(before, session.Current);
            else Assert.True(await Inspect.PdfiumPageCount(session.Current.GetBytes()) >= 1);
        }
    }

    [Theory]
    [MemberData(nameof(BrokenNames))]
    public async Task Renderer_probe_reports_corrupt_without_throwing_and_open_throws_domain_exception(string name)
    {
        using var host = new TestHost();
        var r = host.Get<IPdfRenderer>();
        var data = Broken(name);
        PdfProbeResult? probe = null;
        var ex = await Capture(async () => probe = await r.ProbeAsync(data));
        Assert.Null(ex);
        if (probe!.Status == PdfProbeStatus.Ok) return; // naprawiony przez PDFium
        Assert.Equal(PdfProbeStatus.Corrupt, probe.Status);
        var openEx = await Capture(async () => { using var _ = await r.OpenAsync(data); });
        Assert.IsType<PdfCorruptException>(openEx);
    }

    [Fact]
    public async Task Services_given_broken_bytes_fail_cleanly()
    {
        using var host = new TestHost();
        var bad = SamplePdfFactory.Corrupted();
        var garbage = ExtraSamples.Broken().Single(b => b.Name == "losowe bajty z nagłówkiem").Data;
        foreach (var data in new[] { bad, garbage })
        {
            var calls = new (string, Func<Task>)[]
            {
                ("IPageOperations.RotateAsync", () => host.Get<IPageOperations>().RotateAsync(data, new[] { 0 }, 90)),
                ("IStampingService.AddWatermarkAsync", () => host.Get<IStampingService>().AddWatermarkAsync(data, new WatermarkOptions { Text = "X" })),
                ("ISanitizeService.SanitizeAsync", () => host.Get<ISanitizeService>().SanitizeAsync(data, new SanitizeOptions())),
                ("IOptimizerService.OptimizeAsync", () => host.Get<IOptimizerService>().OptimizeAsync(data, new OptimizeOptions())),
                ("IAnnotationService.GetAnnotationsAsync", () => host.Get<IAnnotationService>().GetAnnotationsAsync(data)),
                ("IFormService.GetFieldsAsync", () => host.Get<IFormService>().GetFieldsAsync(data)),
                ("IAttachmentService.GetAttachmentsAsync", () => host.Get<IAttachmentService>().GetAttachmentsAsync(data)),
                ("ITextEditService.GetTextBlocksAsync", () => host.Get<ITextEditService>().GetTextBlocksAsync(data, 0)),
                ("ILinkService.GetLinksAsync", () => host.Get<ILinkService>().GetLinksAsync(data)),
                ("IConversionService.PdfToTextAsync", () => host.Get<IConversionService>().PdfToTextAsync(data, new TextExportOptions())),
                ("IRedactionService.FindAsync", () => host.Get<IRedactionService>().FindAsync(data, new RedactionSearch { Patterns = SensitiveDataPattern.All })),
                ("IOcrService.RecognizeAsync", () => host.Get<IOcrService>().RecognizeAsync(data, new OcrOptions())),
                ("ISignatureService.ValidateAsync", () => host.Get<ISignatureService>().ValidateAsync(data, null)),
                ("ICompareService.CompareAsync", () => host.Get<ICompareService>().CompareAsync(data, "a", SamplePdfFactory.Text(1), "b", new CompareOptions())),
                ("IBookmarkService.GetBookmarksAsync", () => host.Get<IBookmarkService>().GetBookmarksAsync(data)),
                ("IMetadataService.GetAsync", () => host.Get<IMetadataService>().GetAsync(data)),
                ("ISecurityService.AnalyzeAsync", () => host.Get<ISecurityService>().AnalyzeAsync(data, null)),
            };
            foreach (var (what, call) in calls)
                AssertNotProgrammingError(await Capture(call), what);
        }
    }

    [Fact]
    public async Task Broken_comment_and_form_data_files_are_rejected_without_changing_document()
    {
        using var host = new TestHost();
        var pdf = SamplePdfFactory.WithForm();
        foreach (var junk in new[] { "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots><highlight page=\"x\"", "%FDF-1.2\n1 0 obj <<", "\0\0\0garbage" })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(junk);
            AssertNotProgrammingError(await Capture(() => host.Get<IAnnotationService>().ImportCommentsAsync(pdf, new MemoryStream(bytes))), "ImportComments " + junk);
            foreach (var f in new[] { FormDataFormat.Xfdf, FormDataFormat.Fdf, FormDataFormat.Csv })
                AssertNotProgrammingError(await Capture(() => host.Get<IFormService>().ImportDataAsync(pdf, new MemoryStream(bytes), f)), $"ImportData {f} {junk}");
        }
    }

    [Fact]
    public async Task Action_wizard_reports_broken_files_and_processes_the_good_ones()
    {
        using var host = new TestHost();
        var wizard = host.Get<IActionWizard>();
        var action = (await wizard.GetActionsAsync()).Single(a => a.Id == ActionWizard.WebId);
        var good = TestPaths.WriteSample(SamplePdfFactory.Text(2), "dobry ą.pdf");
        var brokenBytes = SamplePdfFactory.Corrupted();
        var broken = TestPaths.WriteSample(brokenBytes, "zepsuty ę.pdf");
        var notPdf = TestPaths.WriteSample(SamplePdfFactory.NotAPdf(), "nie pdf ś.pdf");
        var encrypted = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "szyfrowany ż.pdf");
        var outDir = Path.Combine(TestPaths.NewDirectory(), "wyniki ąę");
        ActionRunReport? report = null;
        var ex = await Capture(async () => report = await wizard.RunAsync(action, new[] { good, broken, notPdf, encrypted }, new ActionRunOptions { OutputDirectory = outDir, FileNameSuffix = "_w" }));
        Assert.Null(ex);
        Assert.Equal(4, report!.Files.Count);
        Assert.True(report.Files.Single(f => f.InputPath == good).Success);
        foreach (var p in new[] { notPdf, encrypted })
        {
            var r = report.Files.Single(f => f.InputPath == p);
            Assert.False(r.Success, p);
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
        }
        var b = report.Files.Single(f => f.InputPath == broken);
        if (b.Success) Assert.True(await Inspect.PdfiumPageCount(File.ReadAllBytes(b.OutputPath!)) >= 1);
        Assert.Equal(brokenBytes, File.ReadAllBytes(broken)); // oryginały nietknięte
    }

    [Fact]
    public async Task Merge_insert_and_combine_with_broken_source_fail_cleanly()
    {
        using var host = new TestHost();
        var broken = TestPaths.WriteSample(ExtraSamples.Broken().Single(b => b.Name == "losowe bajty z nagłówkiem").Data, "zepsuty źródłowy.pdf");
        var good = TestPaths.WriteSample(SamplePdfFactory.Text(1), "dobry.pdf");
        AssertNotProgrammingError(await Capture(() => host.Get<IPageOperations>().MergeAsync(new[] { new MergeSource(good), new MergeSource(broken) }, new MergeOptions())), "Merge");
        AssertNotProgrammingError(await Capture(() => host.Get<IPageOperations>().InsertFromFileAsync(SamplePdfFactory.Text(1), broken, null, null, 0)), "InsertFromFile");
        AssertNotProgrammingError(await Capture(() => host.Get<IConversionService>().CombineFilesAsync(new[] { good, broken })), "CombineFiles");
        var img = Path.Combine(TestPaths.NewDirectory(), "zły obraz.png");
        File.WriteAllBytes(img, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        AssertNotProgrammingError(await Capture(() => host.Get<IConversionService>().ImagesToPdfAsync(new[] { img }, new ImageToPdfOptions())), "ImagesToPdf");
    }
}

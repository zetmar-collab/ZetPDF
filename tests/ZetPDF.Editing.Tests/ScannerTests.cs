using System.Runtime.InteropServices;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Editing.Scanning;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

/// <summary>Atrapa skanera: logika przepływu i konwersja. Prawdziwego skanowania tu nie ma (brak urządzenia).</summary>
public class ScannerTests
{
    private sealed class FakeScanner : IScannerBackend
    {
        public bool Available = true;
        public int FeederPages = 3;
        public Exception? Fail;
        public readonly List<ScanSettings> Calls = new();
        public int? ApartmentSeen;
        public bool IsAvailable => Available;
        public IReadOnlyList<ScannerInfo> ListDevices() => new[] { new ScannerInfo("id1", "Skaner łąka", true) };
        public IReadOnlyList<byte[]> Scan(ScanSettings settings, CancellationToken ct)
        {
            ApartmentSeen = (int)Thread.CurrentThread.GetApartmentState();
            Calls.Add(settings);
            if (Fail is not null) throw Fail;
            return Enumerable.Range(0, settings.UseFeeder ? FeederPages : 1).Select(_ => TestImages.Png(40, 60, alpha: false)).ToList();
        }
    }

    private static Func<Func<CancellationToken, Task>, Task<bool>> Direct => async step => { await step(CancellationToken.None); return true; };

    private static ScanSettings Settings(bool feeder) => new("id1", 300, ScanColorMode.Gray, feeder);

    [Fact]
    public async Task Feeder_scans_all_pages_in_one_pass_on_an_sta_thread()
    {
        var fake = new FakeScanner();
        var pages = await new ScanWorkflow(fake).CollectAsync(Settings(true), Direct, _ => throw new InvalidOperationException("nie pyta przy podajniku"));
        Assert.Equal(3, pages.Count);
        Assert.Single(fake.Calls);
        Assert.Equal((int)ApartmentState.STA, fake.ApartmentSeen);
        Assert.Equal(300, fake.Calls[0].Dpi);
        Assert.Equal(ScanColorMode.Gray, fake.Calls[0].Mode);
    }

    [Fact]
    public async Task Flatbed_asks_before_each_next_page()
    {
        var fake = new FakeScanner();
        var asked = new List<int>();
        var pages = await new ScanWorkflow(fake).CollectAsync(Settings(false), Direct, n => { asked.Add(n); return n < 3; });
        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { 1, 2, 3 }, asked);
    }

    [Fact]
    public async Task Cancelling_keeps_pages_already_scanned()
    {
        var fake = new FakeScanner();
        int runs = 0;
        var pages = await new ScanWorkflow(fake).CollectAsync(Settings(false),
            async step => { if (++runs == 2) return false; await step(CancellationToken.None); return true; }, _ => true);
        Assert.Single(pages);
    }

    [Fact]
    public async Task Backend_error_reaches_the_caller_with_a_message_key()
    {
        var fake = new FakeScanner { Fail = WiaScannerBackend.Translate(new COMException("paper", unchecked((int)0x80210003))) };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ScanWorkflow(fake).CollectAsync(Settings(true), Direct, _ => false));
        Assert.Equal("Edit.Scan.Error.NoPaper", ex.Data["ZetPDF.MessageKey"] ?? MessageKeyOf(ex));
    }

    private static object? MessageKeyOf(Exception ex) => ex.Data.Keys.Cast<object>().Select(k => ex.Data[k]).OfType<string>().FirstOrDefault(v => v.StartsWith("Edit.", StringComparison.Ordinal));

    [Theory]
    [InlineData(unchecked((int)0x80210002), "Edit.Scan.Error.Jam")]
    [InlineData(unchecked((int)0x80210005), "Edit.Scan.Error.Offline")]
    [InlineData(unchecked((int)0x80210006), "Edit.Scan.Error.Busy")]
    [InlineData(unchecked((int)0x80004005), "Edit.Scan.Error.General")]
    public void Wia_errors_are_translated(int hresult, string key) =>
        Assert.Equal(key, MessageKeyOf(WiaScannerBackend.Translate(new COMException("x", hresult))));

    [Fact]
    public async Task Pages_become_pdf_through_image_conversion_and_temp_files_are_removed()
    {
        var conv = new ConversionService(TestEnv.Renderer);
        var temp = TestPaths.NewDirectory();
        var pages = new[] { TestImages.Png(96, 96, alpha: false), TestImages.Png(96, 48, alpha: false) };
        var pdf = await ScanWorkflow.BuildPdfAsync(pages, conv, temp, null, null, null, default);
        Assert.Equal(2, PdfSharpEdit.Read(pdf, d => d.PageCount));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [Fact]
    public async Task Ocr_option_adds_a_text_layer()
    {
        var conv = new ConversionService(TestEnv.Renderer);
        var ocr = TestEnv.Ocr();
        // Strona ze skanu jako obraz: render próbki Scanned z tekstem.
        using var doc = await TestEnv.Renderer.OpenAsync(SamplePdfFactory.Scanned());
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(300 / 72.0));
        var png = ZetPDF.Editing.Imaging.PngWriter.FromBgra(bmp);
        var pdf = await ScanWorkflow.BuildPdfAsync(new[] { png }, conv, TestPaths.NewDirectory(), ocr, new OcrOptions { SkipPagesWithText = false }, null, default);
        Assert.False(string.IsNullOrWhiteSpace(TestEnv.PdfPigText(pdf)));
    }

    [Fact]
    public void Real_wia_lists_devices_without_error_or_reports_unavailable()
    {
        // Sprawdza tylko warstwę COM na tym komputerze (zwykle pusta lista); skanowania nie testuje.
        var backend = new WiaScannerBackend();
        if (!backend.IsAvailable) return;
        IReadOnlyList<ScannerInfo>? devices = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { devices = backend.ListDevices(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(error);
        Assert.NotNull(devices);
    }
}

using System.Security.Cryptography.X509Certificates;
using System.Windows.Input;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Narzędzia (Wypełnij i podpisz ✓/✗, stempel z obrazu), polecenia widoku, optymalizator, TSA bez sieci.</summary>
public class ToolsAndViewTests
{
    private static async Task<(TestHost Host, IPdfDocumentSession Session, IDocumentView View)> OpenAsync(byte[] pdf)
    {
        var host = new TestHost(initializeModules: true);
        var session = await host.Documents.CreateNewAsync(pdf, "narzędzia.pdf");
        host.Documents.ActiveDocument = session;
        var view = host.Get<IDocumentViewFactory>().CreateView(session);
        Sta.Layout(view.Element);
        await Sta.WaitFor(() => view.GetPageTransform(0) is not null, 20000, "strona");
        return (host, session, view);
    }

    [Fact]
    public void Fill_and_sign_check_and_cross_marks_are_placed_in_file_and_undoable() => Sta.Run(async () =>
    {
        var (host, session, view) = await OpenAsync(SamplePdfFactory.WithForm());
        using (host)
        {
            var r = host.Get<ICommandRegistry>();
            Assert.True(r.TryExecute(CommandIds.FillSign.Open));
            foreach (var (id, at) in new[] { (CommandIds.FillSign.Check, new PdfPoint(300, 300)), (CommandIds.FillSign.Cross, new PdfPoint(400, 300)) })
            {
                await Sta.WaitFor(() => r.GetHandler(id)!.CanExecute(null), 10000, id);
                Assert.True(r.TryExecute(id), id);
                var tool = host.Get<IToolService>().ActiveTool;
                Assert.Equal(id, tool.Id);
                var t = view.GetPageTransform(0)!;
                var before = session.Current;
                var e = new PagePointerEventArgs(0, at, t.ToView(at), MouseButton.Left, ModifierKeys.None, 1);
                tool.OnPointerPressed(view, e);
                tool.OnPointerReleased(view, e);
                await Sta.WaitFor(() => !ReferenceEquals(before, session.Current), 20000, id + " w pliku");
            }
            var pdf = session.Current.GetBytes();
            var annots = (await host.Get<IAnnotationService>().GetAnnotationsAsync(pdf)).Where(a => a.Kind != AnnotationKind.Widget).ToList();
            Assert.Equal(2, annots.Count);
            Assert.All(annots, a => Assert.True(a.Rect.Width > 4 && a.Rect.Height > 4));
            var (_, _, dark) = await Inspect.RenderStats(pdf, 0, 72);
            var (_, _, darkBase) = await Inspect.RenderStats(SamplePdfFactory.WithForm(), 0, 72);
            Assert.True(dark > darkBase, "znaczniki niewidoczne w renderze");
            await session.History.UndoAsync();
            await session.History.UndoAsync();
            Assert.DoesNotContain(await host.Get<IAnnotationService>().GetAnnotationsAsync(session.Current.GetBytes()), a => a.Kind != AnnotationKind.Widget);
            view.Dispose();
        }
    });

    [Fact]
    public void View_commands_change_only_the_view_not_the_file() => Sta.Run(async () =>
    {
        var (host, session, view) = await OpenAsync(SamplePdfFactory.Text(4));
        using (host)
        {
            var r = host.Get<ICommandRegistry>();
            var bytes = session.Current.GetBytes();
            double zoom = view.Zoom;
            Assert.True(r.TryExecute(CommandIds.View.ZoomIn));
            Assert.True(view.Zoom > zoom);
            Assert.True(r.TryExecute(CommandIds.View.FitWidth));
            Assert.Equal(ZoomMode.FitWidth, view.ZoomMode);
            Assert.True(r.TryExecute(CommandIds.View.RotateRight));
            Assert.Equal(90, view.ViewRotation);
            Assert.True(r.TryExecute(CommandIds.View.TwoPages));
            Assert.Equal(PageLayoutMode.TwoPages, view.Layout);
            Assert.True(r.TryExecute(CommandIds.View.Continuous));
            Assert.True(r.TryExecute(CommandIds.Nav.LastPage));
            Sta.Layout(view.Element);
            Assert.Equal(3, view.CurrentPageIndex);
            Assert.True(r.TryExecute(CommandIds.Nav.FirstPage));
            Sta.Layout(view.Element);
            Assert.Equal(0, view.CurrentPageIndex);
            Assert.True(r.TryExecute(CommandIds.View.ReadingMode));
            Assert.True(host.Shell.IsReadingMode);
            Assert.True(r.TryExecute(CommandIds.View.FullScreen));
            Assert.True(host.Shell.IsFullScreen);
            Assert.Equal(bytes, session.Current.GetBytes()); // (dokument nowy – IsModified=true od początku)
            Assert.False(session.History.CanUndo);
            view.Dispose();
        }
    });

    [Fact]
    public async Task Custom_image_stamp_is_stored_with_image_appearance()
    {
        using var host = new TestHost();
        var (pdf, id) = await host.Get<IAnnotationService>().AddAsync(SamplePdfFactory.Text(1), new AnnotationSpec
        {
            Kind = AnnotationKind.Stamp, PageIndex = 0, Rect = new PdfRect(100, 100, 340, 260), ImageData = ExtraSamples.Png(240, 160),
        });
        Assert.Contains((0, "/Stamp"), Inspect.Annotations(pdf));
        Assert.Contains("/Image", Inspect.Deep(pdf));
        using var doc = await Inspect.Renderer.OpenAsync(pdf);
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(1.0, Clip: new PixelRect(100, (int)(doc.Pages[0].Size.Height - 260), 240, 160)));
        Assert.True(Inspect.Stats(bmp).ColorFraction > 0.5, "stempel z obrazu niewidoczny");
        var flat = await host.Get<IPageOperations>().FlattenAsync(pdf, new FlattenOptions());
        Assert.DoesNotContain((0, "/Stamp"), Inspect.Annotations(flat));
        Assert.True((await Inspect.RenderStats(flat, 0, 72)).ColorFraction > 0.05, "po spłaszczeniu stempel zniknął z treści");
    }

    [Fact]
    public async Task Optimizer_reduces_scan_size_and_keeps_appearance()
    {
        using var host = new TestHost();
        var scan = SamplePdfFactory.Scanned();
        var res = await host.Get<IOptimizerService>().OptimizeAsync(scan, new OptimizeOptions { MaxImageDpi = 100, JpegQuality = 60 });
        Assert.Equal(scan.Length, res.SizeBefore);
        Assert.Equal(res.Pdf.Length, res.SizeAfter);
        Assert.True(res.SizeAfter < res.SizeBefore * 0.8, $"{res.SizeBefore} → {res.SizeAfter}");
        Assert.True(res.ImagesRecompressed >= 1);
        var a = await Inspect.RenderStats(scan, 0, 50);
        var b = await Inspect.RenderStats(res.Pdf, 0, 50);
        Assert.True(Math.Abs(a.MeanLuma - b.MeanLuma) < 5, $"{a.MeanLuma} vs {b.MeanLuma}");
        using var pig = UglyToad.PdfPig.PdfDocument.Open(res.Pdf);
        var img = pig.GetPage(1).GetImages().Single();
        Assert.True(img.WidthInSamples <= 8.27 * 100 + 2, $"obraz {img.WidthInSamples} px przy 100 DPI");
    }

    [Fact]
    public async Task Unreachable_tsa_fails_fast_with_error_and_without_tsa_no_timestamp()
    {
        using var host = new TestHost();
        var sig = host.Get<ISignatureService>();
        var pfx = await sig.CreateSelfSignedIdAsync(new DigitalIdRequest("Test TSA", "Ą", "t@example.com", 1, "p"));
        using var cert = new X509Certificate2(pfx, "p", X509KeyStorageFlags.EphemeralKeySet);
        var request = new SignRequest { Certificate = new SigningCertificate(CertificateSource.PfxFile, cert), Rect = default };
        var plain = await sig.SignAsync(SamplePdfFactory.Text(1), request, null);
        Assert.False(Assert.Single(await sig.ValidateAsync(plain, null)).HasTimestamp);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = sig.SignAsync(SamplePdfFactory.Text(1), request with { TsaUrl = "http://127.0.0.1:9/tsa" }, null);
        var done = await Task.WhenAny(task, Task.Delay(60000));
        Assert.Same(task, done);
        var ex = await Record.ExceptionAsync(() => task);
        Assert.NotNull(ex); // brak cichego podpisu bez znacznika czasu
        CorruptedInputTests.AssertNotProgrammingError(ex, "TSA");
        Assert.True(sw.ElapsedMilliseconds < 60000);
    }
}

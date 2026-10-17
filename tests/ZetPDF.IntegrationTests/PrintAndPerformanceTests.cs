using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Testy mierzące czas i liczniki procesu (uchwyty, pamięć) – uruchamiane bez równoległych klas testowych.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SequentialCollection
{
    public const string Name = "Sekwencyjne (pomiary procesu)";
}

/// <summary>Druk do XPS (zakres, N-up, skala szarości), 500 stron (czas, brak blokady wątku UI), uchwyty i pamięć.</summary>
[Collection(SequentialCollection.Name)]
public class PrintAndPerformanceTests
{
    // ================================================================== druk

    /// <summary>Ta sama ścieżka co druk do drukarki (PrintService.PrintToXpsFileAsync – wewnętrzna, wołana przez odbicie).</summary>
    private static Task PrintToXps(IRenderDocument doc, PrintOptions options, string path)
    {
        var m = typeof(ZetPDF.Viewer.Printing.PrintService).GetMethod("PrintToXpsFileAsync", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("PrintService.PrintToXpsFileAsync");
        return (Task)m.Invoke(null, new object?[] { doc, options, path, null, null, CancellationToken.None })!;
    }

    /// <summary>Arkusze XPS jako bitmapy (STA).</summary>
    private static List<(double Red, double Blue, double Color)> Sheets(string xpsPath)
    {
        var result = new List<(double, double, double)>();
        using var xps = new XpsDocument(xpsPath, FileAccess.Read);
        var paginator = xps.GetFixedDocumentSequence().DocumentPaginator;
        for (int i = 0; i < paginator.PageCount; i++)
        {
            var page = paginator.GetPage(i);
            var v = page.Visual;
            if (v is UIElement ui)
            {
                ui.Measure(page.Size);
                ui.Arrange(new Rect(page.Size));
                ui.UpdateLayout();
            }
            int w = (int)(page.Size.Width / 4), h = (int)(page.Size.Height / 4);
            var rtb = new RenderTargetBitmap(w, h, 24, 24, PixelFormats.Pbgra32);
            rtb.Render(v);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            long red = 0, blue = 0, color = 0, n = (long)w * h;
            for (int p = 0; p < px.Length; p += 4)
            {
                int b = px[p], g = px[p + 1], r = px[p + 2];
                if (r > 180 && g < 80 && b < 80) red++;
                if (b > 180 && r < 80 && g < 80) blue++;
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 40) color++;
            }
            result.Add(((double)red / n, (double)blue / n, (double)color / n));
        }
        return result;
    }

    [Fact]
    public void Print_range_1_3_5_prints_exactly_those_pages_in_order() => Sta.Run(async () =>
    {
        using var doc = await Inspect.Renderer.OpenAsync(ExtraSamples.ColoredPages(6, redPage: 4)); // strona 5 czerwona
        var path = Path.Combine(TestPaths.NewDirectory(), "wydruk zakres ąę.xps");
        await PrintToXps(doc, new PrintOptions(null, "1-3,5"), path);
        var sheets = Sheets(path);
        Assert.Equal(4, sheets.Count);
        for (int i = 0; i < 3; i++) Assert.True(sheets[i].Blue > 0.3 && sheets[i].Red < 0.01, $"arkusz {i + 1}: {sheets[i]}");
        Assert.True(sheets[3].Red > 0.3, $"arkusz 4 (strona 5) nie jest czerwony: {sheets[3]}");
    });

    [Fact]
    public void Print_n_up_places_several_pages_per_sheet() => Sta.Run(async () =>
    {
        using var doc = await Inspect.Renderer.OpenAsync(ExtraSamples.ColoredPages(6, redPage: 4));
        var dir = TestPaths.NewDirectory();
        var four = Path.Combine(dir, "4 na arkusz.xps");
        await PrintToXps(doc, new PrintOptions(null, string.Empty, PagesPerSheet: 4), four);
        var s4 = Sheets(four);
        Assert.Equal(2, s4.Count);
        Assert.True(s4[0].Red < 0.01 && s4[0].Blue > 0.2, $"arkusz 1 (strony 1-4): {s4[0]}");
        Assert.True(s4[1].Red > 0.03 && s4[1].Blue > 0.03, $"arkusz 2 (strony 5-6): {s4[1]}");

        var two = Path.Combine(dir, "2 na arkusz zakres.xps");
        await PrintToXps(doc, new PrintOptions(null, "1-3,5", PagesPerSheet: 2), two);
        var s2 = Sheets(two);
        Assert.Equal(2, s2.Count);
        Assert.True(s2[0].Red < 0.01, "arkusz 1: strony 1-2 niebieskie");
        Assert.True(s2[1].Red > 0.05 && s2[1].Blue > 0.05, "arkusz 2: strona 3 (niebieska) i 5 (czerwona)");
    });

    [Fact]
    public void Print_grayscale_has_no_color() => Sta.Run(async () =>
    {
        using var doc = await Inspect.Renderer.OpenAsync(ExtraSamples.ColoredPages(2, redPage: 0));
        var path = Path.Combine(TestPaths.NewDirectory(), "szarość.xps");
        await PrintToXps(doc, new PrintOptions(null, string.Empty, Grayscale: true), path);
        Assert.All(Sheets(path), s => Assert.True(s.Color < 0.01, $"kolor w druku w skali szarości: {s}"));
    });

    [Fact]
    public async Task Print_service_refuses_document_without_print_permission()
    {
        using var host = new TestHost();
        var enc = await host.Get<ISecurityService>().EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings("u-ą", "o-ę", PdfEncryptionMethod.Aes256, DocumentPermissions.CopyContent));
        var session = (await host.Documents.OpenAsync(TestPaths.WriteSample(enc, "bez druku.pdf"), "u-ą"))!;
        Assert.False(session.Can(DocumentPermissions.Print));
        await Assert.ThrowsAsync<DocumentPermissionException>(() => host.Get<IPrintService>().PrintAsync(session, new PrintOptions("nieistniejąca drukarka", string.Empty)));
    }

    // ================================================================== 500 stron

    private static readonly Lazy<byte[]> Large500 = new(() => SamplePdfFactory.Large(500));

    /// <summary>Mierzy największą przerwę w obsłudze wątku UI (Dispatcher) w trakcie <paramref name="work"/>.</summary>
    private static async Task<(T Result, long MaxGapMs, long TotalMs)> MeasureUi<T>(Func<Task<T>> work)
    {
        long maxGap = 0;
        bool done = false;
        var sw = Stopwatch.StartNew();
        async Task Tick()
        {
            long last = sw.ElapsedMilliseconds;
            while (!done)
            {
                await Task.Delay(10); // wraca na wątek UI (DispatcherSynchronizationContext)
                long now = sw.ElapsedMilliseconds;
                maxGap = Math.Max(maxGap, now - last);
                last = now;
            }
        }
        var t = Tick();
        var result = await work();
        done = true;
        await t;
        return (result, maxGap, sw.ElapsedMilliseconds);
    }

    [Fact]
    public void Large_500_page_document_opens_renders_and_edits_without_blocking_ui_thread() => Sta.Run(async () =>
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(Large500.Value, "500 stron ąę.pdf");

        var (session, gapOpen, openMs) = await MeasureUi(() => host.Documents.OpenAsync(path));
        Assert.NotNull(session);
        Assert.True(openMs < 15000, $"otwarcie 500 stron: {openMs} ms");
        Assert.True(gapOpen < 1000, $"wątek UI zablokowany na {gapOpen} ms podczas otwierania");

        var (bmp, gapRender, renderMs) = await MeasureUi(async () =>
        {
            using var doc = await host.Get<IPdfRenderer>().OpenAsync(session!.Current.GetBytes());
            Assert.Equal(500, doc.PageCount);
            return await doc.RenderPageAsync(0, RenderRequest.ForDpi(96));
        });
        Assert.True(bmp.Width > 0);
        Assert.True(renderMs < 10000, $"render pierwszej strony: {renderMs} ms");
        Assert.True(gapRender < 1000, $"wątek UI zablokowany na {gapRender} ms podczas renderu");

        var op = Operations.Get("Pages.Rotate");
        var (changed, gapEdit, editMs) = await MeasureUi(() => session!.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.PageContent, op.Permission)));
        Assert.True(changed);
        Assert.True(editMs < 30000, $"obrót strony w 500-stronicowym: {editMs} ms");
        Assert.True(gapEdit < 1000, $"wątek UI zablokowany na {gapEdit} ms podczas edycji");

        var (_, gapSave, saveMs) = await MeasureUi(async () => { await host.Documents.SaveAsync(session!, Path.Combine(Path.GetDirectoryName(path)!, "zapis 500 ść.pdf")); return 0; });
        Assert.True(saveMs < 30000, $"zapis: {saveMs} ms");
        Assert.True(gapSave < 1000, $"wątek UI zablokowany na {gapSave} ms podczas zapisu");

        var (_, gapUndo, _) = await MeasureUi(async () => { await session!.History.UndoAsync(); return 0; });
        Assert.True(gapUndo < 1000, $"wątek UI zablokowany na {gapUndo} ms podczas Cofnij");
    });

    [Fact]
    public void Large_500_page_document_view_opens_and_search_does_not_block_ui() => Sta.Run(async () =>
    {
        using var host = new TestHost(initializeModules: true);
        var session = await host.Documents.CreateNewAsync(Large500.Value, "500.pdf");
        host.Documents.ActiveDocument = session;
        var sw = Stopwatch.StartNew();
        var view = host.Get<IDocumentViewFactory>().CreateView(session);
        Sta.Layout(view.Element);
        await Sta.WaitFor(() => view.GetPageTransform(0) is not null, 20000, "pierwsza strona w widoku");
        Assert.True(sw.ElapsedMilliseconds < 15000, $"widok 500 stron: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(500, view.PageCount);
        view.GoToPage(499);
        Sta.Layout(view.Element);
        Assert.Equal(499, view.CurrentPageIndex);

        var (hits, gap, ms) = await MeasureUi(async () =>
        {
            using var doc = await host.Get<IPdfRenderer>().OpenAsync(session.Current.GetBytes());
            int n = 0;
            await foreach (var _ in doc.SearchAsync("gęślą", TextSearchOptions.None)) n++;
            return n;
        });
        Assert.Equal(1000, hits); // "gęślą" i "GĘŚLĄ" na każdej stronie (bez rozróżniania wielkości liter)
        Assert.True(gap < 1000, $"wątek UI zablokowany na {gap} ms podczas wyszukiwania");
        view.Dispose();
    });

    // ================================================================== uchwyty i pamięć

    [Fact]
    public async Task Many_open_edit_close_cycles_do_not_leak_handles_or_memory()
    {
        using var host = new TestHost();
        var path = TestPaths.WriteSample(SamplePdfFactory.WithForm(), "cykle ąę.pdf");
        var encPath = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "cykle szyfrowany.pdf");
        var op = Operations.Get("Annot.Note");

        async Task Cycle(int i)
        {
            var s = (await host.Documents.OpenAsync(i % 2 == 0 ? path : encPath, i % 2 == 0 ? null : SamplePdfFactory.OwnerPassword))!;
            await s.ExecuteAsync(new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.Annotations, op.Permission));
            using (var d = await host.Get<IPdfRenderer>().OpenAsync(s.Current.GetBytes()))
            {
                await d.RenderPageAsync(0, new RenderRequest(0.5));
                await d.GetPageTextAsync(0);
            }
            await s.History.UndoAsync();
            host.Documents.Close(s);
        }

        for (int i = 0; i < 5; i++) await Cycle(i); // rozgrzewka
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        int handlesBefore = proc.HandleCount;
        long memBefore = GC.GetTotalMemory(true);

        for (int i = 0; i < 60; i++) await Cycle(i);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        proc.Refresh();
        int handlesAfter = proc.HandleCount;
        long memAfter = GC.GetTotalMemory(true);
        Assert.Empty(host.Documents.Documents);
        Assert.True(handlesAfter - handlesBefore < 60, $"uchwyty procesu: {handlesBefore} → {handlesAfter}");
        Assert.True(memAfter - memBefore < 64L * 1024 * 1024, $"pamięć zarządzana: {memBefore / 1024 / 1024} → {memAfter / 1024 / 1024} MB");
        // pliki z dysku nie są blokowane po zamknięciu
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public void Many_view_create_dispose_cycles_do_not_leak_handles() => Sta.Run(async () =>
    {
        using var host = new TestHost(initializeModules: true);
        var pdf = SamplePdfFactory.Text(5);
        async Task Cycle()
        {
            var s = await host.Documents.CreateNewAsync(pdf, "widok.pdf");
            host.Documents.ActiveDocument = s;
            var v = host.Get<IDocumentViewFactory>().CreateView(s);
            Sta.Layout(v.Element);
            await Sta.WaitFor(() => v.GetPageTransform(0) is not null, 20000, "strona");
            v.Dispose();
            host.Documents.Close(s);
        }
        for (int i = 0; i < 3; i++) await Cycle();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        int before = proc.HandleCount;
        for (int i = 0; i < 25; i++) await Cycle();
        await Task.Delay(500);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        proc.Refresh();
        Assert.True(proc.HandleCount - before < 60, $"uchwyty procesu: {before} → {proc.HandleCount}");
    });
}

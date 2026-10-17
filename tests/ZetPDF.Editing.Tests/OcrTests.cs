using ZetPDF.Core.Contracts;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

public class OcrTests
{
    [Fact]
    public async Task Scan_gets_hidden_text_layer_searchable_by_pdfium_and_pdfpig()
    {
        var scan = SamplePdfFactory.Scanned();
        Assert.Equal(0, await TestEnv.PdfiumSearchCount(scan, "gęślą"));

        var ocr = TestEnv.Ocr();
        Assert.True(await ocr.IsAvailableAsync());
        var progress = new List<OperationProgress>();
        var result = await ocr.RecognizeAsync(scan, new OcrOptions(), new SyncProgress(progress));
        var page = Assert.Single(result.Pages);
        Assert.False(page.Skipped);
        var words = page.Words.Select(w => w.Text).ToList();
        Assert.Contains("Zażółć", words);
        Assert.Contains("gęślą", words);
        Assert.Contains("12345", words);
        Assert.NotEmpty(progress);

        var output = await ocr.ApplyTextLayerAsync(scan, result);
        Assert.Equal(1, await TestEnv.PdfiumSearchCount(output, "gęślą"));
        Assert.Equal(1, await TestEnv.PdfiumSearchCount(output, "Faktura numer 12345"));
        Assert.Contains("Zażółć", TestEnv.PdfPigText(output));

        // Tryb renderowania 3: render przed i po jest identyczny (warstwa niewidoczna).
        using var a = await TestEnv.Renderer.OpenAsync(scan);
        using var b = await TestEnv.Renderer.OpenAsync(output);
        var ra = await a.RenderPageAsync(0, RenderRequest.ForDpi(50));
        var rb = await b.RenderPageAsync(0, RenderRequest.ForDpi(50));
        Assert.Equal(ra.Bgra, rb.Bgra);

        // Słowo leży mniej więcej tam, gdzie na obrazie (prostokąt trafienia PDFium vs prostokąt OCR).
        var ocrBox = page.Words.First(w => w.Text == "gęślą").Box;
        await foreach (var hit in b.SearchAsync("gęślą", TextSearchOptions.None))
        {
            var r = hit.Rects[0];
            Assert.True(Math.Abs(r.Left - ocrBox.Left) < 4, $"left {r.Left} vs {ocrBox.Left}");
            Assert.True(Math.Abs(r.Right - ocrBox.Right) < 6, $"right {r.Right} vs {ocrBox.Right}");
            Assert.True(r.IntersectsWith(ocrBox));
        }
    }

    [Fact]
    public async Task Repeated_ocr_replaces_previous_layer_and_skips_pages_with_text()
    {
        var ocr = TestEnv.Ocr();
        var scan = SamplePdfFactory.Scanned();
        var once = await ocr.OcrDocumentAsync(scan, new OcrOptions());
        // Domyślnie strona z tekstem jest pomijana.
        var skipped = await ocr.RecognizeAsync(once, new OcrOptions());
        Assert.True(skipped.Pages[0].Skipped);
        // Wymuszone ponowne OCR zastępuje warstwę (fraza występuje raz, nie dwa razy).
        var twice = await ocr.OcrDocumentAsync(once, new OcrOptions { SkipPagesWithText = false });
        Assert.Equal(1, await TestEnv.PdfiumSearchCount(twice, "gęślą"));
    }

    [Fact]
    public async Task Region_ocr_recognizes_only_the_area()
    {
        var ocr = TestEnv.Ocr();
        var scan = SamplePdfFactory.Scanned();
        // Druga linia ("Faktura numer…") jest ok. 48–60 pt od góry; strona A4 842 pt.
        var full = await ocr.RecognizeAsync(scan, new OcrOptions());
        var faktura = full.Pages[0].Words.First(w => w.Text == "Faktura").Box;
        var region = new PdfRect(0, faktura.Bottom - 5, 595, faktura.Top + 5);
        var result = await ocr.RecognizeAsync(scan, new OcrOptions { Region = region });
        var words = result.Pages[0].Words.Select(w => w.Text).ToList();
        Assert.Contains("Faktura", words);
        Assert.DoesNotContain("Zażółć", words);
        var output = await ocr.ApplyTextLayerAsync(scan, result);
        Assert.Equal(1, await TestEnv.PdfiumSearchCount(output, "Faktura"));
        Assert.Equal(0, await TestEnv.PdfiumSearchCount(output, "gęślą"));
    }

    [Fact]
    public async Task Low_confidence_words_are_suspect_with_threshold()
    {
        var ocr = TestEnv.Ocr();
        var scan = SamplePdfFactory.Scanned();
        var all = await ocr.RecognizeAsync(scan, new OcrOptions { SuspectThreshold = 101 });
        Assert.All(all.Pages[0].Words, w => Assert.True(w.IsSuspect));
        var none = await ocr.RecognizeAsync(scan, new OcrOptions { SuspectThreshold = 0 });
        Assert.Empty(none.SuspectWords);
    }

    [Fact]
    public async Task Recognition_can_be_cancelled()
    {
        var ocr = TestEnv.Ocr();
        var scan = SamplePdfFactory.Scanned();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ocr.RecognizeAsync(scan, new OcrOptions(), null, cts.Token));
    }

    [Fact]
    public async Task Rotated_scan_layer_matches_display_orientation()
    {
        var ocr = TestEnv.Ocr();
        var scan = Samples.RotatedUpright(SamplePdfFactory.Scanned());
        var output = await ocr.OcrDocumentAsync(scan, new OcrOptions());
        Assert.Equal(1, await TestEnv.PdfiumSearchCount(output, "gęślą"));
        // Trafienie leży w górnej części wyświetlanej strony (portret po obrocie).
        using var doc = await TestEnv.Renderer.OpenAsync(output);
        Assert.Equal(90, doc.Pages[0].Rotation);
        await foreach (var hit in doc.SearchAsync("gęślą", TextSearchOptions.None))
        {
            var r = hit.Rects[0];
            // Treść obrócona: górna część wyświetlanej strony = małe X w przestrzeni użytkownika.
            Assert.True(r.Left < 100, r.ToString());
            Assert.True(r.Height > r.Width, r.ToString());
        }
    }

    private sealed class SyncProgress : IProgress<OperationProgress>
    {
        private readonly List<OperationProgress> _list;
        public SyncProgress(List<OperationProgress> list) => _list = list;
        public void Report(OperationProgress value) { lock (_list) _list.Add(value); }
    }
}

using System.Diagnostics;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.TestData;

namespace ZetPDF.Viewer.Tests;

/// <summary>Testy renderera PDFium (ZetPDF.Rendering). Kolekcja szeregowa – liczniki uchwytów są globalne.</summary>
[Collection(PdfiumCollection.Name)]
public class RendererTests
{
    private readonly PdfiumRenderer _renderer = new();

    [Fact]
    public async Task Renders_page_to_bgra_with_dark_text_pixels()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(3));
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(PdfSize.A4.Width, doc.Pages[0].Size.Width, 0);
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(96 / 72.0));
        Assert.Equal((int)Math.Round(doc.Pages[0].Size.Width * 96 / 72.0), bmp.Width);
        Assert.True(bmp.Stride >= bmp.Width * 4);
        Assert.Equal(bmp.Stride * bmp.Height, bmp.Bgra.Length);
        int dark = 0;
        for (int i = 0; i < bmp.Bgra.Length; i += 4) if (bmp.Bgra[i] < 100 && bmp.Bgra[i + 3] == 255) dark++;
        Assert.True(dark > 500, $"dark pixels: {dark}");
    }

    [Fact]
    public async Task Renders_clip_region_identical_to_full_render()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(1));
        var full = await doc.RenderPageAsync(0, new RenderRequest(1.5));
        var clip = new PixelRect(40, 60, 200, 120);
        var part = await doc.RenderPageAsync(0, new RenderRequest(1.5, Clip: clip));
        Assert.Equal(200, part.Width);
        Assert.Equal(120, part.Height);
        int diff = 0;
        for (int y = 0; y < part.Height; y++)
            for (int x = 0; x < part.Width; x++)
            {
                int a = (y + clip.Y) * full.Stride + (x + clip.X) * 4, b = y * part.Stride + x * 4;
                if (Math.Abs(full.Bgra[a] - part.Bgra[b]) > 8) diff++;
            }
        Assert.True(diff < part.Width * part.Height / 100, $"diff {diff}");
    }

    [Fact]
    public async Task View_rotation_swaps_bitmap_dimensions()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(1));
        var r0 = await doc.RenderPageAsync(0, new RenderRequest(0.5));
        var r90 = await doc.RenderPageAsync(0, new RenderRequest(0.5, ViewRotation: 90));
        Assert.Equal(r0.Width, r90.Height);
        Assert.Equal(r0.Height, r90.Width);
    }

    [Fact]
    public async Task Page_text_contains_polish_characters_with_boxes()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(2));
        var text = await doc.GetPageTextAsync(1);
        Assert.Contains("Strona 2", text.Text);
        Assert.Contains("Zażółć gęślą jaźń", text.Text);
        Assert.Equal(text.Text.Length, text.Chars.Count);
        int i = text.Text.IndexOf('ż');
        var box = text.Chars[i].Box;
        Assert.False(box.IsEmpty);
        Assert.InRange(box.Top, 600, 842); // górna część strony A4 (Y w górę)
        Assert.True(text.Chars[i].FontSize > 10);
    }

    [Theory]
    [InlineData("gęślą", TextSearchOptions.MatchCase, 3)]
    [InlineData("GĘŚLĄ", TextSearchOptions.None, 6)]          // bez rozróżniania wielkości: "gęślą" ×3 + "GĘŚLĄ" ×3
    [InlineData("gęślą", TextSearchOptions.None, 6)]
    [InlineData("GĘŚLĄ", TextSearchOptions.MatchCase, 3)]
    [InlineData("jaźń", TextSearchOptions.WholeWord | TextSearchOptions.MatchCase, 3)]
    [InlineData("jaź", TextSearchOptions.WholeWord, 0)]
    [InlineData("zażółć", TextSearchOptions.MatchCase, 0)]
    public async Task Search_handles_polish_characters_case_and_whole_words(string query, TextSearchOptions options, int expected)
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(3));
        var hits = new List<TextSearchHit>();
        var progress = new List<OperationProgress>();
        await foreach (var h in doc.SearchAsync(query, options, new SyncProgress<OperationProgress>(progress.Add)))
            hits.Add(h);
        Assert.Equal(expected, hits.Count);
        Assert.Equal(3, progress.Count);
        foreach (var h in hits)
        {
            Assert.NotEmpty(h.Rects);
            Assert.Equal(query.Length, h.CharCount);
            Assert.Contains(query, h.Context, StringComparison.CurrentCultureIgnoreCase);
        }
        if (expected > 0) Assert.Equal(new[] { 0, 1, 2 }, hits.Select(h => h.PageIndex).Distinct().ToArray());
    }

    [Fact]
    public async Task Search_can_be_cancelled()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(40));
        using var cts = new CancellationTokenSource();
        int count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in doc.SearchAsync("Strona", TextSearchOptions.None, null, cts.Token))
            {
                if (++count == 2) cts.Cancel();
            }
        });
        Assert.True(count < 40);
    }

    [Fact]
    public async Task Encrypted_file_requires_password_and_rejects_wrong_one()
    {
        var enc = SamplePdfFactory.EncryptedAes256();
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _renderer.OpenAsync(enc));
        await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => _renderer.OpenAsync(enc, "złe hasło"));

        var probeNone = await _renderer.ProbeAsync(enc);
        Assert.Equal(PdfProbeStatus.PasswordRequired, probeNone.Status);
        var probeWrong = await _renderer.ProbeAsync(enc, "złe");
        Assert.Equal(PdfProbeStatus.InvalidPassword, probeWrong.Status);

        var probeUser = await _renderer.ProbeAsync(enc, SamplePdfFactory.UserPassword);
        Assert.Equal(PdfProbeStatus.Ok, probeUser.Status);
        Assert.True(probeUser.IsEncrypted);
        Assert.Equal(6, probeUser.SecurityRevision);
        Assert.True(probeUser.Permissions.HasFlag(DocumentPermissions.Print));
        Assert.False(probeUser.Permissions.HasFlag(DocumentPermissions.ModifyContent));
        Assert.False(probeUser.Permissions.HasFlag(DocumentPermissions.CopyContent));

        var probeOwner = await _renderer.ProbeAsync(enc, SamplePdfFactory.OwnerPassword);
        Assert.Equal(DocumentPermissions.All, probeOwner.Permissions);

        using var doc = await _renderer.OpenAsync(enc, SamplePdfFactory.UserPassword);
        var text = await doc.GetPageTextAsync(0);
        Assert.Contains("gęślą", text.Text);
    }

    [Fact]
    public async Task RemoveSecurity_with_user_password_gives_plain_pdf_editable_by_pdfsharp()
    {
        var enc = SamplePdfFactory.EncryptedAes256(pages: 3);
        var plain = await _renderer.RemoveSecurityAsync(enc, SamplePdfFactory.UserPassword);

        var probe = await _renderer.ProbeAsync(plain);
        Assert.Equal(PdfProbeStatus.Ok, probe.Status);
        Assert.False(probe.IsEncrypted);
        Assert.Equal(3, probe.PageCount);

        // PDFsharp (hasło właściciela nieznane) może teraz modyfikować – podstawa modelu sesji.
        var edited = PdfSharpEdit.Modify(plain, d => d.Pages[0].Rotate = 90);
        using var doc = await _renderer.OpenAsync(edited);
        Assert.Equal(90, doc.Pages[0].Rotation);
        Assert.Contains("Zażółć", (await doc.GetPageTextAsync(2)).Text);

        await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => _renderer.RemoveSecurityAsync(enc, "złe"));
    }

    [Fact]
    public async Task Corrupted_and_non_pdf_raise_domain_exceptions()
    {
        var ex = await Assert.ThrowsAsync<PdfCorruptException>(() => _renderer.OpenAsync(SamplePdfFactory.NotAPdf()));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        await Assert.ThrowsAsync<PdfCorruptException>(() => _renderer.OpenAsync(Array.Empty<byte>()));
        Assert.Equal(PdfProbeStatus.Corrupt, (await _renderer.ProbeAsync(SamplePdfFactory.NotAPdf())).Status);

        // Obcięty plik: PDFium próbuje naprawy. Wynik musi być albo czytelnym wyjątkiem, albo poprawnie otwartym
        // dokumentem – nigdy awarią procesu.
        try
        {
            using var doc = await _renderer.OpenAsync(SamplePdfFactory.Corrupted());
            for (int i = 0; i < doc.PageCount; i++) await doc.RenderPageAsync(i, new RenderRequest(0.3));
        }
        catch (PdfCorruptException) { }
        var probe = await _renderer.ProbeAsync(SamplePdfFactory.Corrupted());
        Assert.True(probe.Status is PdfProbeStatus.Corrupt or PdfProbeStatus.Ok);
    }

    [Fact]
    public async Task Handles_are_released_after_many_open_close_cycles()
    {
        var pdf = SamplePdfFactory.WithForm();
        var enc = SamplePdfFactory.EncryptedAes256();
        await PdfiumRenderer.FlushAsync();
        var before = PdfiumRenderer.OpenHandleCounts;
        for (int i = 0; i < 60; i++)
        {
            var doc = await _renderer.OpenAsync(i % 3 == 0 ? enc : pdf, i % 3 == 0 ? SamplePdfFactory.UserPassword : null);
            await doc.RenderPageAsync(0, new RenderRequest(0.4));
            await doc.GetPageTextAsync(0);
            await doc.GetLinksAsync(0);
            await foreach (var _ in doc.SearchAsync("Strona", TextSearchOptions.None)) { }
            doc.Dispose();
            doc.Dispose(); // podwójne Dispose jest bezpieczne
            await _renderer.ProbeAsync(enc, "złe");
            await _renderer.RemoveSecurityAsync(enc, SamplePdfFactory.UserPassword);
        }
        await PdfiumRenderer.FlushAsync();
        var after = PdfiumRenderer.OpenHandleCounts;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Disposed_document_rejects_calls_but_pending_render_completes()
    {
        var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(2));
        var pending = doc.RenderPageAsync(1, new RenderRequest(1));
        doc.Dispose();
        var bmp = await pending; // zlecone przed Dispose – kończy się poprawnie
        Assert.True(bmp.Width > 0);
        Assert.IsType<ObjectDisposedException>(Record.Exception(() => { doc.RenderPageAsync(0, new RenderRequest(1)); }));
        await PdfiumRenderer.FlushAsync();
        Assert.True(((PdfiumDocument)doc).IsClosed);
    }

    [Fact]
    public async Task Cancelled_render_request_does_not_run()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.Text(1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.RenderPageAsync(0, new RenderRequest(1), cts.Token));
    }

    [Fact]
    public async Task Large_document_with_500_pages_opens_quickly()
    {
        var pdf = SamplePdfFactory.Large(500);
        var sw = Stopwatch.StartNew();
        using var doc = await _renderer.OpenAsync(pdf);
        sw.Stop();
        Assert.Equal(500, doc.PageCount);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"open took {sw.ElapsedMilliseconds} ms");
        var bmp = await doc.RenderPageAsync(499, new RenderRequest(0.2));
        Assert.True(bmp.Width > 0);
    }

    [Fact]
    public async Task Bookmarks_are_read_with_nesting_and_targets()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.WithBookmarks(3));
        var tree = await doc.GetBookmarksAsync();
        Assert.Equal(3, tree.Count);
        Assert.Equal("Rozdział 1", tree[0].Title);
        Assert.Equal(2, tree[1].Children.Count);
        Assert.Equal("Podrozdział 2.2", tree[1].Children[1].Title);
        Assert.Equal(3, tree[1].Children[1].PageIndex);
        Assert.Equal(4, tree[2].PageIndex);
    }

    [Fact]
    public async Task Page_info_and_PageTransform_match_FPDF_PageToDevice()
    {
        var pdf = PdfSharpEdit.Modify(SamplePdfFactory.Text(3), d =>
        {
            d.Pages[1].Rotate = 90;
            d.Pages[2].CropBox = new PdfRectangle(new XRect(50, 60, 400, 600));
            d.Pages[2].Rotate = 270;
        });
        using var doc = await _renderer.OpenAsync(pdf);
        var pd = (PdfiumDocument)doc;
        Assert.Equal(90, doc.Pages[1].Rotation);
        Assert.Equal(270, doc.Pages[2].Rotation);
        Assert.Equal(400, doc.Pages[2].Size.Width, 1);
        Assert.Equal(50, doc.Pages[2].CropOrigin.X, 1);

        var points = new[] { new PdfPoint(100, 700), new PdfPoint(300, 200), new PdfPoint(420, 640) };
        foreach (var info in doc.Pages)
            foreach (int view in new[] { 0, 90, 180, 270 })
            {
                double scale = 2.0;
                var t = new PageTransform(info.Size, info.CropOrigin, info.Rotation + view, scale, new System.Windows.Point(0, 0));
                int sx = (int)Math.Round(t.ViewSize.Width), sy = (int)Math.Round(t.ViewSize.Height);
                foreach (var p0 in points)
                {
                    var p = new PdfPoint(info.CropOrigin.X + (p0.X % info.Size.Width), info.CropOrigin.Y + (p0.Y % info.Size.Height));
                    var expected = t.ToView(p);
                    var (dx, dy) = await pd.PageToDeviceAsync(info.Index, sx, sy, view / 90, p.X, p.Y);
                    Assert.InRange(Math.Abs(dx - expected.X), 0, 1.5);
                    Assert.InRange(Math.Abs(dy - expected.Y), 0, 1.5);
                }
            }
    }

    [Fact]
    public async Task Links_are_reported()
    {
        var pdf = PdfSharpEdit.Modify(SamplePdfFactory.Text(2), d =>
        {
            d.Pages[0].AddDocumentLink(new PdfRectangle(new XRect(50, 50, 100, 20)), 2);
            d.Pages[0].AddWebLink(new PdfRectangle(new XRect(50, 100, 100, 20)), "https://example.com/zetpdf");
        });
        using var doc = await _renderer.OpenAsync(pdf);
        var links = await doc.GetLinksAsync(0);
        Assert.True(links.Count == 2, string.Join(" | ", links));
        Assert.Contains(links, l => l.Kind == PdfLinkKind.GoToPage && l.TargetPageIndex == 1);
        Assert.Contains(links, l => l.Kind == PdfLinkKind.Uri && l.Uri == "https://example.com/zetpdf");
    }

    [Fact]
    public async Task Form_fields_render_without_errors()
    {
        using var doc = await _renderer.OpenAsync(SamplePdfFactory.WithForm());
        var withForms = await doc.RenderPageAsync(0, new RenderRequest(1, Flags: RenderFlags.Default));
        var printing = await doc.RenderPageAsync(0, new RenderRequest(1, Flags: RenderFlags.Annotations | RenderFlags.Printing | RenderFlags.Grayscale));
        Assert.Equal(withForms.Width, printing.Width);
    }

    /// <summary>IProgress wołany synchronicznie (Progress&lt;T&gt; używa kontekstu synchronizacji – w testach niedeterministyczny).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PdfiumCollection
{
    public const string Name = "PDFium";
}

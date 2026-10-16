using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.TestData;
using ZetPDF.Viewer.Panels;
using ZetPDF.Viewer.Printing;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tests;

/// <summary>Widok, panele, zakładki, druk (WPF na wątku STA, prawdziwy PDFium).</summary>
[Collection(PdfiumCollection.Name)]
public class ViewerTests
{
    [Fact]
    public void Large_document_opens_without_rendering_all_pages_and_navigates() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var sw = Stopwatch.StartNew();
        var (_, view) = await host.OpenAsync(SamplePdfFactory.Large(500));
        sw.Stop();
        Assert.Equal(500, view.PageCount);
        Assert.True(sw.ElapsedMilliseconds < 8000, $"open {sw.ElapsedMilliseconds} ms");

        await Sta.WaitFor(() => view.CacheStats.HighCount > 0 && view.CacheStats.Pending == 0, what: "first renders");
        var stats = view.CacheStats;
        Assert.InRange(stats.HighCount, 1, 8); // tylko widoczne strony (+ bufor), nie 500
        Assert.True(stats.HighBytes <= 256L * 1024 * 1024);

        view.GoToPage(250);
        Sta.Layout(view.Element);
        Assert.Equal(250, view.CurrentPageIndex);
        var t = view.GetPageTransform(250);
        Assert.NotNull(t);
        Assert.True(t!.ViewBounds.IntersectsWith(new Rect(0, 0, 900, 700)));
        await Sta.WaitFor(() => view.CacheStats.Pending == 0, what: "renders after jump");

        // szybkie przewijanie: zlecenia dla stron, które zniknęły z okna, są anulowane
        for (int p = 0; p < 500; p += 25) view.GoToPage(p);
        await Sta.WaitFor(() => view.CacheStats.Pending == 0, what: "renders after fast scroll");
        Assert.Equal(475, view.CurrentPageIndex);
        view.Dispose();
    });

    [Fact]
    public void Page_transform_round_trips_with_hit_testing_for_zoom_and_rotation() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (_, view) = await host.OpenAsync(PdfSharpEdit.Modify(SamplePdfFactory.Text(3), d => d.Pages[1].Rotate = 90));
        foreach (int rot in new[] { 0, 90, 180, 270 })
        {
            view.ViewRotation = rot;
            view.Zoom = 1.5;
            view.GoToPage(1);
            Sta.Layout(view.Element);
            var t = view.GetPageTransform(1)!;
            Assert.Equal((90 + rot) % 360, t.Rotation);
            Assert.Equal(1.5 * 96 / 72, t.Scale, 6);
            var p = new PdfPoint(200, 500);
            var v = t.ToView(p);
            Assert.True(view.TryHitPage(v, out int page, out var back));
            Assert.Equal(1, page);
            Assert.Equal(p.X, back.X, 3);
            Assert.Equal(p.Y, back.Y, 3);
        }
        view.ViewRotation = 0;
        view.ZoomMode = ZoomMode.FitWidth;
        Sta.Layout(view.Element);
        var tw = view.GetPageTransform(view.CurrentPageIndex)!;
        Assert.InRange(tw.ViewSize.Width, 900 - 80, 900);
        view.ZoomMode = ZoomMode.ActualSize;
        Assert.Equal(1.0, view.Zoom);
        view.Dispose();
    });

    [Fact]
    public void Layout_modes_and_navigation_commands_work() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (_, view) = await host.OpenAsync(SamplePdfFactory.Text(7));
        var registry = host.Services.GetRequiredService<ICommandRegistry>();
        Assert.True(registry.TryExecute(CommandIds.View.TwoPages));
        Assert.Equal(PageLayoutMode.TwoPages, view.Layout);
        Assert.True(((IToggleCommand)registry.GetHandler(CommandIds.View.TwoPages)!).IsChecked);
        Assert.True(registry.TryExecute(CommandIds.Nav.NextPage));
        Assert.Equal(2, view.CurrentPageIndex);
        Assert.NotNull(view.GetPageTransform(3));
        Assert.Null(view.GetPageTransform(0)); // poza bieżącą rozkładówką
        Assert.True(registry.TryExecute(CommandIds.Nav.LastPage));
        Assert.Equal(6, view.CurrentPageIndex);
        Assert.True(registry.TryExecute(CommandIds.View.Continuous));
        Assert.True(registry.TryExecute(CommandIds.Nav.FirstPage));
        Assert.Equal(0, view.CurrentPageIndex);
        Assert.True(registry.TryExecute(CommandIds.View.RotateRight));
        Assert.Equal(90, view.ViewRotation);
        Assert.True(registry.TryExecute(CommandIds.View.RotateLeft));
        Assert.Equal(0, view.ViewRotation);
        double z = view.Zoom;
        Assert.True(registry.TryExecute(CommandIds.View.ZoomIn));
        Assert.True(view.Zoom > z);
        Assert.Equal(ZoomMode.Custom, view.ZoomMode);
        Assert.True(registry.TryExecute(CommandIds.Tool.SelectText));
        Assert.True(host.Services.GetRequiredService<IToolService>().IsActive(CommandIds.Tool.SelectText));
        Assert.True(registry.TryExecute(CommandIds.Tool.Hand));
        view.Dispose();
    });

    [Fact]
    public void All_viewer_commands_from_catalog_have_handlers_and_panels_are_registered()
    {
        using var host = new ViewerHost();
        var registry = host.Services.GetRequiredService<ICommandRegistry>();
        var mine = RibbonCatalog.Commands.Where(c => c.Module == FeatureModuleId.Viewer).ToList();
        Assert.Equal(30, mine.Count);
        Assert.All(mine, c => Assert.NotNull(registry.GetHandler(c.Id)));
        var panels = host.Services.GetRequiredService<IPanelRegistry>().Panels;
        Assert.Contains(panels, p => p.Id == PanelIds.Thumbnails && p.Order == 10);
        Assert.Contains(panels, p => p.Id == PanelIds.Bookmarks && p.Order == 20);
        var loc = host.Services.GetRequiredService<ILocalizationService>();
        Assert.True(loc.HasKey("Viewer.Panel.Thumbnails"));
    }

    [Fact]
    public void Select_all_and_copy_respect_text_and_permissions() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (_, view) = await host.OpenAsync(SamplePdfFactory.Text(3));
        await view.SelectAllAsync();
        var sel = view.TextSelection!;
        Assert.Equal(3, sel.Spans.Count);
        Assert.Contains("Zażółć gęślą jaźń", sel.Text);
        Assert.Contains("Strona 3", sel.Text);
        Assert.All(sel.Spans, s => Assert.NotEmpty(s.Quads));
        var copy = host.Services.GetRequiredService<ICommandRegistry>().GetHandler(CommandIds.Edit.Copy)!;
        Assert.True(copy.CanExecute(null));
        view.ClearTextSelection();
        Assert.False(copy.CanExecute(null));
        view.Dispose();
    });

    [Fact]
    public void Find_highlights_polish_hits_and_moves_between_them() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (_, view) = await host.OpenAsync(SamplePdfFactory.Text(3));
        view.Find.Open("GĘŚLĄ");
        await Sta.WaitFor(() => view.Find.Hits.Count == 6 && !view.Find.IsSearching, what: "6 hits");
        Assert.Equal(0, view.Find.CurrentIndex);
        Assert.Equal(2, view.Find.HitsOnPage(1).Count);
        view.Find.Move(+1);
        Assert.Equal(1, view.Find.CurrentIndex);
        view.Find.Move(-1);
        view.Find.Move(-1);
        Assert.Equal(5, view.Find.CurrentIndex); // zawija
        Assert.Equal(2, view.CurrentPageIndex);
        view.Find.Close();
        Assert.Empty(view.Find.Hits);
        view.Dispose();
    });

    private sealed class RecordingOverlay : IPageOverlay
    {
        public List<(int Page, PageTransform T)> Calls { get; } = new();
        public int ZIndex => 100;
        public bool IsVisible => true;
        public void Render(DrawingContext dc, int pageIndex, PageTransform transform) => Calls.Add((pageIndex, transform));
    }

    [Fact]
    public void Overlays_receive_same_transform_as_GetPageTransform_and_view_reloads_after_change() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (session, view) = await host.OpenAsync(SamplePdfFactory.Text(2));
        var overlay = new RecordingOverlay();
        view.AddOverlay(overlay);
        Sta.Layout(view.Element);
        var rtb = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(view.Element);
        Assert.NotEmpty(overlay.Calls);
        var (page, t) = overlay.Calls[0];
        var expected = view.GetPageTransform(page)!;
        Assert.Equal(expected.ViewOrigin, t.ViewOrigin);
        Assert.Equal(expected.Scale, t.Scale);
        view.RemoveOverlay(overlay);

        int reloaded = 0;
        view.DocumentReloaded += (_, e) => reloaded++;
        await session.ExecuteAsync(new DelegateDocumentCommand("Test.Rotate", (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.Pages[0].Rotate = 90)),
            DocumentChangeKind.PageContent, DocumentPermissions.AssembleDocument, new[] { 0 }));
        await Sta.WaitFor(() => reloaded > 0, what: "reload");
        Assert.Equal(90, view.RenderDocument!.Pages[0].Rotation);
        await session.History.UndoAsync();
        await Sta.WaitFor(() => reloaded > 1, what: "reload after undo");
        Assert.Equal(0, view.RenderDocument!.Pages[0].Rotation);
        view.Dispose();
    });

    [Fact]
    public void Closing_document_disposes_view_and_releases_pdfium_handles() => Sta.Run(async () =>
    {
        await PdfiumRenderer.FlushAsync();
        int before = PdfiumRenderer.OpenHandleCounts.Documents;
        using var host = new ViewerHost();
        for (int i = 0; i < 5; i++)
        {
            var (session, view) = await host.OpenAsync(SamplePdfFactory.Text(4), $"doc{i}.pdf");
            await Sta.WaitFor(() => view.CacheStats.Pending == 0, what: "renders");
            host.Documents.Close(session); // ViewerService zwalnia widok
            Assert.False(view.IsDocumentLoaded);
        }
        await PdfiumRenderer.FlushAsync();
        Assert.Equal(before, PdfiumRenderer.OpenHandleCounts.Documents);
        Assert.Equal(0, PdfiumRenderer.OpenHandleCounts.Pages);
        Assert.Equal(0, PdfiumRenderer.OpenHandleCounts.Bitmaps);
    });

    [Fact]
    public void Encrypted_file_with_user_password_opens_through_document_manager_and_is_editable() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var path = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(pages: 3), "szyfrowany zażółć.pdf");
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => host.Documents.OpenAsync(path));
        var session = await host.Documents.OpenAsync(path, SamplePdfFactory.UserPassword);
        Assert.NotNull(session);
        Assert.True(session!.SecurityInfo.IsEncrypted);
        Assert.False(session.SecurityInfo.OpenedWithOwnerPassword);
        Assert.False(PdfSecurityHelper.IsEncrypted(session.Current.GetBytes()));
        // PDFsharp może zmodyfikować odszyfrowane bajty (bez hasła właściciela)
        var edited = PdfSharpEdit.Modify(session.Current.GetBytes(), d => d.Pages[0].Rotate = 180);
        Assert.Equal(3, PdfSharpEdit.Read(edited, d => d.PageCount));
        var view = (DocumentView)host.Services.GetRequiredService<IDocumentViewFactory>().CreateView(session);
        Sta.Layout(view.Element);
        await Sta.WaitFor(() => view.IsDocumentLoaded, what: "view");
        Assert.Equal(3, view.PageCount);
        Assert.Contains("gęślą", (await view.GetPageTextAsync(0)).Text);
        host.Documents.Close(session);
    });

    [Fact]
    public async Task Bookmark_service_round_trips_nested_tree_with_targets_and_styles()
    {
        var service = new BookmarkService();
        var pdf = SamplePdfFactory.WithBookmarks(2);
        var tree = await service.GetBookmarksAsync(pdf);
        Assert.Equal(2, tree.Count);
        Assert.Equal("Podrozdział 2.2", tree[1].Children[1].Title);
        Assert.Equal(3, tree[1].Children[1].PageIndex);

        var edited = new[]
        {
            new BookmarkNode("Wstęp – zażółć", 0, BookmarkNode.NoChildren, new PdfPoint(10, 700), Bold: true),
            new BookmarkNode("Rozdział", 1, new[]
            {
                new BookmarkNode("Podrozdział ąę", 2, BookmarkNode.NoChildren, Italic: true),
                new BookmarkNode("Bez celu", -1, BookmarkNode.NoChildren),
            }, IsOpen: true),
        };
        var saved = await service.SetBookmarksAsync(pdf, edited);
        var back = await service.GetBookmarksAsync(saved);
        Assert.Equal(2, back.Count);
        Assert.Equal("Wstęp – zażółć", back[0].Title);
        Assert.True(back[0].Bold);
        Assert.Equal(700, back[0].Target!.Value.Y, 1);
        Assert.True(back[1].IsOpen);
        Assert.Equal("Podrozdział ąę", back[1].Children[0].Title);
        Assert.True(back[1].Children[0].Italic);
        Assert.Equal(2, back[1].Children[0].PageIndex);
        Assert.Equal(-1, back[1].Children[1].PageIndex);

        // PDFium widzi to samo
        using var doc = await new PdfiumRenderer().OpenAsync(saved);
        var viaPdfium = await doc.GetBookmarksAsync();
        Assert.Equal("Podrozdział ąę", viaPdfium[1].Children[0].Title);
        Assert.Equal(2, viaPdfium[1].Children[0].PageIndex);

        var cleared = await service.SetBookmarksAsync(saved, Array.Empty<BookmarkNode>());
        Assert.Empty(await service.GetBookmarksAsync(cleared));
        Assert.Equal(4, PdfSharpEdit.Read(cleared, d => d.PageCount));
    }

    [Fact]
    public void Bookmarks_panel_adds_renames_and_moves_with_undo() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (session, view) = await host.OpenAsync(SamplePdfFactory.WithBookmarks(2));
        host.Services.GetRequiredService<IViewerService>(); // aktywny widok
        var panel = new BookmarksPanel(host.Services);
        await Sta.WaitFor(() => panel.Items.Count == 2, what: "bookmarks loaded");

        view.GoToPage(3);
        await panel.AddBookmarkAsync();
        await Sta.WaitFor(() => panel.Items.Count == 3, what: "added");
        Assert.Equal("Strona 4", panel.Items[2].Title, ignoreCase: true, ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);
        Assert.Equal(3, panel.Items[2].Node.PageIndex);
        Assert.Equal("Viewer.Undo.AddBookmark", session.History.UndoDescriptionKey);

        var tree = await host.Services.GetRequiredService<IBookmarkService>().GetBookmarksAsync(session.Current.GetBytes());
        var moved = ZetPDF.Viewer.Bookmarks.BookmarkTree.Move(tree, new[] { 2 }, new[] { 0 }, 0)!.Value;
        await ZetPDF.Viewer.Bookmarks.BookmarkTree.CommitAsync(session, host.Services.GetRequiredService<IBookmarkService>(), moved.Tree, "Viewer.Undo.MoveBookmark");
        await Sta.WaitFor(() => panel.Items.Count == 2 && panel.Items[0].Children.Count == 3, what: "moved");

        await session.History.UndoAsync();
        await session.History.UndoAsync();
        await Sta.WaitFor(() => panel.Items.Count == 2 && panel.Items[0].Children.Count == 2, what: "undone");
        view.Dispose();
    });

    /// <summary>Atrapa operacji stron (pages-engine jeszcze nie scalony): tylko MoveAsync, przez PDFsharp.</summary>
    private sealed class MoveOnlyPageOperations : IPageOperations
    {
        public Task<byte[]> MoveAsync(byte[] pdf, IReadOnlyList<int> pages, int targetIndex, CancellationToken ct = default) =>
            PdfSharpEdit.ModifyAsync(pdf, d =>
            {
                var order = Enumerable.Range(0, d.PageCount).ToList();
                var moving = pages.OrderBy(i => i).ToList();
                int insert = targetIndex - moving.Count(p => p < targetIndex);
                order.RemoveAll(moving.Contains);
                order.InsertRange(insert, moving);
                // przestawienie przez MovePage zgodnie z docelową kolejnością
                var pagesObj = Enumerable.Range(0, d.PageCount).Select(i => d.Pages[i]).ToList();
                for (int pos = 0; pos < order.Count; pos++)
                {
                    int current = d.Pages.Cast<PdfSharp.Pdf.PdfPage>().ToList().IndexOf(pagesObj[order[pos]]);
                    if (current != pos) d.Pages.MovePage(current, pos);
                }
            }, ct);

        public Task<byte[]> InsertFromFileAsync(byte[] pdf, string sourcePath, string? sourcePassword, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> InsertFromBytesAsync(byte[] pdf, byte[] sourcePdf, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> InsertBlankAsync(byte[] pdf, int insertAt, int count, PdfSize size, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> DeleteAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> ExtractAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ExtractToFilesAsync(byte[] pdf, IReadOnlyList<int> pages, string outputDirectory, string fileNamePattern, string baseName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> ReplaceAsync(byte[] pdf, IReadOnlyList<int> targetPages, string sourcePath, string? sourcePassword, IReadOnlyList<int> sourcePages, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> DuplicateAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> SwapAsync(byte[] pdf, int pageA, int pageB, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> ReverseAsync(byte[] pdf, IReadOnlyList<int>? pages, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> RotateAsync(byte[] pdf, IReadOnlyList<int> pages, int degrees, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> CropAsync(byte[] pdf, IReadOnlyList<int> pages, CropOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> ResizeAsync(byte[] pdf, IReadOnlyList<int> pages, ResizeOptions options, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> InterleaveAsync(byte[] pdf, string secondPath, string? secondPassword, bool reverseSecond, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> FlattenAsync(byte[] pdf, FlattenOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> SetPageLabelsAsync(byte[] pdf, IReadOnlyList<PageLabelRange> ranges, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PageLabelRange>> GetPageLabelsAsync(byte[] pdf, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> SplitAsync(byte[] pdf, SplitOptions options, string outputDirectory, string baseName, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> MergeAsync(IReadOnlyList<MergeSource> sources, MergeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<byte[]> CreateBlankDocumentAsync(PdfSize size, int count = 1, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public void Thumbnails_panel_lists_pages_renders_visible_and_moves_pages_with_undo() => Sta.Run(async () =>
    {
        using var host = new ViewerHost(s => s.AddSingleton<IPageOperations, MoveOnlyPageOperations>());
        var (session, view) = await host.OpenAsync(SamplePdfFactory.Text(5));
        var panel = new ThumbnailsPanel(host.Services);
        // Loaded (realizacja miniatur) wymaga okna – pokazujemy je poza ekranem
        var window = new Window { Content = panel, Width = 200, Height = 700, Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        window.Show();
        Assert.Equal(5, panel.Items.Count);
        await Sta.WaitFor(() => panel.Items.Take(2).All(i => i.Image is not null), what: "thumbnails");

        Assert.True(await panel.MovePagesAsync(view, new[] { 3, 4 }, 0));
        await Sta.WaitFor(() => view.RenderDocument is { } d && view.PageCount == 5 && panel.Items.Count == 5 && view.SelectedPages.SequenceEqual(new[] { 0, 1 }), what: "moved");
        Assert.Contains("Strona 4", (await view.GetPageTextAsync(0)).Text);
        Assert.Equal("Viewer.Undo.MovePages", session.History.UndoDescriptionKey);
        Assert.False(await panel.MovePagesAsync(view, new[] { 1 }, 1)); // upuszczenie w miejscu – bez zmiany
        await session.History.UndoAsync();
        await Sta.WaitFor(() => view.GetCachedText(0) is null || view.GetCachedText(0)!.Text.Contains("Strona 1"), what: "undo");
        Assert.Contains("Strona 1", (await view.GetPageTextAsync(0)).Text);
        window.Close();
        view.Dispose();
    });

    [Fact]
    public void Print_composes_n_up_sheets_to_xps() => Sta.Run(async () =>
    {
        using var doc = await new PdfiumRenderer().OpenAsync(SamplePdfFactory.Text(10));
        var dir = TestPaths.NewDirectory();
        foreach (var (perSheet, range, expected) in new[] { (4, "", 3), (1, "2-4", 3), (9, "", 2), (2, "1,3,5", 2) })
        {
            var path = Path.Combine(dir, $"wydruk {perSheet}.xps");
            var progress = new List<OperationProgress>();
            await PrintService.PrintToXpsFileAsync(doc, new PrintOptions(null, range, PagesPerSheet: perSheet), path, null, new SyncProgress(progress.Add));
            Assert.Equal(expected, progress.Count);
            using var xps = new XpsDocument(path, FileAccess.Read);
            var seq = xps.GetFixedDocumentSequence();
            Assert.Equal(expected, seq.DocumentPaginator.PageCount);
        }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrintService.PrintToXpsFileAsync(doc, new PrintOptions(null, ""), Path.Combine(dir, "anulowany.xps"), null, null, cts.Token));
    });

    private sealed class SyncProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}

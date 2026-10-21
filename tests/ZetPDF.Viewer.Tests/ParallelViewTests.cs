using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.TestData;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tests;

[Collection(PdfiumCollection.Name)]
public class ParallelViewTests
{
    [Fact]
    public void Parallel_view_shows_second_document_and_synchronizes_scroll_and_zoom() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (s1, v1) = await host.OpenAsync(SamplePdfFactory.Text(10), "a.pdf");
        var (s2, v2) = await host.OpenAsync(SamplePdfFactory.Text(12), "b.pdf");
        host.Documents.ActiveDocument = s1;
        var registry = host.Services.GetRequiredService<ICommandRegistry>();
        var parallel = host.Services.GetRequiredService<ParallelViewController>();
        var viewToggle = (IToggleCommand)registry.GetHandler(CommandIds.View.ParallelView)!;
        var syncToggle = (IToggleCommand)registry.GetHandler(CommandIds.View.SyncScroll)!;
        Assert.False(viewToggle.IsChecked);
        Assert.False(syncToggle.CanExecute(null));

        var pair = parallel.Open(v1, s2, Orientation.Horizontal);
        Assert.True(viewToggle.IsChecked);
        Assert.NotNull(v1.SideContent);
        Assert.Same(s2, pair.Companion.Session);
        Assert.True(pair.Companion.IsCompanion);
        Sta.Layout(v1.Element, 1400, 700);
        await Sta.WaitFor(() => pair.Companion.IsDocumentLoaded, what: "companion loaded");
        Sta.Layout(v1.Element, 1400, 700);
        Assert.Equal(12, pair.Companion.PageCount);
        // widok karty zostaje w kolumnie 0: współrzędne Element bez przesunięcia
        Assert.True(v1.GetPageTransform(0)!.ViewOrigin.X < 700);

        // bez synchronizacji drugi widok stoi
        var before = pair.Companion.ScrollOffset;
        v1.ScrollTo(new Point(v1.ScrollOffset.X, v1.ScrollOffset.Y + 300));
        Assert.Equal(before, pair.Companion.ScrollOffset);

        Assert.True(syncToggle.CanExecute(null));
        Assert.True(registry.TryExecute(CommandIds.View.SyncScroll));
        Assert.True(syncToggle.IsChecked);
        Assert.Equal(v1.Zoom, pair.Companion.Zoom, 6);

        var c0 = pair.Companion.ScrollOffset.Y;
        v1.ScrollTo(new Point(v1.ScrollOffset.X, v1.ScrollOffset.Y + 400));
        Assert.Equal(c0 + 400, pair.Companion.ScrollOffset.Y, 1);
        // i w drugą stronę
        var p0 = v1.ScrollOffset.Y;
        pair.Companion.ScrollTo(new Point(pair.Companion.ScrollOffset.X, pair.Companion.ScrollOffset.Y - 150));
        Assert.Equal(p0 - 150, v1.ScrollOffset.Y, 1);
        // powiększenie
        v1.Zoom = 1.5;
        Assert.Equal(1.5, pair.Companion.Zoom, 6);
        pair.Companion.Zoom = 0.75;
        Assert.Equal(0.75, v1.Zoom, 6);

        // wyłączenie synchronizacji
        Assert.True(registry.TryExecute(CommandIds.View.SyncScroll));
        var c1 = pair.Companion.ScrollOffset;
        v1.ScrollTo(new Point(0, 0));
        Assert.Equal(c1, pair.Companion.ScrollOffset);

        // zamknięcie drugiego dokumentu zamyka widok równoległy
        host.Documents.Close(s2);
        Assert.Null(v1.SideContent);
        Assert.Null(parallel.GetPair(v1));
        Assert.False(viewToggle.IsChecked);
        v1.Dispose();
        v2.Dispose();
    });

    [Fact]
    public void Parallel_view_toggle_closes_pair_and_vertical_layout_keeps_primary_on_top() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (s1, v1) = await host.OpenAsync(SamplePdfFactory.Text(3), "a.pdf");
        var (s2, v2) = await host.OpenAsync(SamplePdfFactory.Text(3), "b.pdf");
        host.Documents.ActiveDocument = s1;
        var parallel = host.Services.GetRequiredService<ParallelViewController>();
        var pair = parallel.Open(v1, s2, Orientation.Vertical);
        Sta.Layout(v1.Element, 900, 900);
        await Sta.WaitFor(() => pair.Companion.IsDocumentLoaded, what: "companion loaded");
        Sta.Layout(v1.Element, 900, 900);
        Assert.Single(((Grid)v1.Element).RowDefinitions, r => r.Height.IsAbsolute);
        Assert.True(host.Services.GetRequiredService<ICommandRegistry>().TryExecute(CommandIds.View.ParallelView));
        Assert.Null(v1.SideContent);
        Assert.Empty(((Grid)v1.Element).RowDefinitions);
        v1.Dispose();
        v2.Dispose();
    });
    [Fact]
    public void Parallel_dialog_and_read_aloud_panel_build() => Sta.Run(async () =>
    {
        var factory = new FakeSpeechFactory();
        using var host = new ViewerHost(s => s.AddSingleton<ReadAloud.ISpeechEngineFactory>(factory));
        var (s1, v1) = await host.OpenAsync(SamplePdfFactory.Text(2), "a.pdf");
        var loc = host.Services.GetRequiredService<ILocalizationService>();
        var dlg = new Dialogs.ParallelViewDialog(loc, new[] { s1 });
        Assert.Same(s1, dlg.Selected);
        Assert.Equal(Orientation.Horizontal, dlg.Orientation);
        dlg.Close();
        var panel = new ReadAloud.ReadAloudPanel(host.Services.GetRequiredService<ReadAloud.ReadAloudService>(), loc, host.Services.GetRequiredService<ICommandRegistry>());
        Assert.NotNull(panel.Content);
        panel.Close();
        v1.Dispose();
    });
}

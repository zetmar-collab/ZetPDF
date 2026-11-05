using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Rendering;
using ZetPDF.TestData;
using ZetPDF.Viewer.Layers;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tests;

/// <summary>Warstwy: odczyt /Order, ukrycie zmienia piksele bez zmiany pliku sesji, zapis jako polecenie, plik spreparowany.</summary>
[Collection(PdfiumCollection.Name)]
public class LayersTests
{
    /// <summary>Warstwa A (czerwony prostokąt) i B (niebieski), B domyślnie włączona.</summary>
    private static byte[] TwoLayers(Action<PdfDocument, PdfDictionary, PdfDictionary, PdfDictionary>? extra = null) =>
        PdfSharpEdit.Modify(SamplePdfFactory.Text(1), doc =>
        {
            PdfDictionary Ocg(string name)
            {
                var d = new PdfDictionary(doc);
                d.Elements["/Type"] = new PdfName("/OCG");
                d.Elements["/Name"] = new PdfString(name);
                doc.Internals.AddObject(d);
                return d;
            }
            var a = Ocg("Warstwa A");
            var b = Ocg("Warstwa B");
            var ocp = new PdfDictionary(doc);
            ocp.Elements["/OCGs"] = new PdfArray(doc, a.Reference!, b.Reference!);
            var config = new PdfDictionary(doc);
            config.Elements["/Order"] = new PdfArray(doc, a.Reference!, b.Reference!);
            ocp.Elements["/D"] = config;
            doc.Internals.AddObject(ocp);
            doc.Internals.Catalog.Elements["/OCProperties"] = ocp.Reference!;
            var page = doc.Pages[0];
            var res = (PdfDictionary)(page.Elements["/Resources"] is PdfReference r ? r.Value : page.Elements["/Resources"]!);
            var props = new PdfDictionary(doc);
            props.Elements["/LA"] = a.Reference!;
            props.Elements["/LB"] = b.Reference!;
            res.Elements["/Properties"] = props;
            var content = new PdfDictionary(doc);
            content.CreateStream(Encoding.ASCII.GetBytes("/OC /LA BDC 1 0 0 rg 300 300 100 100 re f EMC /OC /LB BDC 0 0 1 rg 100 300 100 100 re f EMC\n"));
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = new PdfArray(doc, OldContents(page).Append(content.Reference!).ToArray());
            extra?.Invoke(doc, config, a, b);
        });

    private static IEnumerable<PdfItem> OldContents(PdfPage page)
    {
        var item = page.Elements["/Contents"];
        var resolved = item is PdfReference r ? r.Value : item;
        return resolved is PdfArray a ? a.Elements.ToArray() : new[] { item! };
    }

    private static bool ColorAt(BitmapSource bmp, double fx, double fy, Func<byte, byte, byte, bool> test)
    {
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int x = (int)(converted.PixelWidth * fx), y = (int)(converted.PixelHeight * fy);
        var px = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, converted.PixelWidth * 4, 0);
        return test(px[2], px[1], px[0]);
    }

    private static bool RedShown(BitmapSource bmp) => ColorAt(bmp, 350.0 / 595, 1 - 350.0 / 842, (r, g, b) => r > 200 && g < 60 && b < 60);
    private static bool BlueShown(BitmapSource bmp) => ColorAt(bmp, 150.0 / 595, 1 - 350.0 / 842, (r, g, b) => b > 200 && r < 60 && g < 60);

    [Fact]
    public async Task Tree_reads_names_order_and_groups()
    {
        var pdf = TwoLayers((doc, config, a, b) =>
        {
            var group = new PdfArray(doc, new PdfString("Grupa"), a.Reference!);
            config.Elements["/Order"] = new PdfArray(doc, group, b.Reference!, new PdfArray(doc, a.Reference!));
            config.Elements["/OFF"] = new PdfArray(doc, b.Reference!);
            config.Elements["/Locked"] = new PdfArray(doc, a.Reference!);
        });
        var tree = await LayerDocument.ReadAsync(pdf);
        Assert.Equal(2, tree.LayerCount);
        Assert.Equal("Grupa", tree.Roots[0].Title);
        Assert.Equal(-1, tree.Roots[0].OcgIndex);
        Assert.True(tree.Roots[0].Children[0].Locked);
        Assert.Equal("Warstwa B", tree.Roots[1].Title);
        Assert.False(tree.Roots[1].Visible);
        Assert.Equal("Warstwa A", Assert.Single(tree.Roots[1].Children).Title);
    }

    [Fact]
    public async Task No_layers_gives_empty_tree() => Assert.Equal(0, (await LayerDocument.ReadAsync(SamplePdfFactory.Text(1))).LayerCount);

    [Fact]
    public void Hiding_changes_pixels_but_not_session_file_then_save_is_undoable() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var pdf = TwoLayers();
        var (session, view) = await host.OpenAsync(pdf);
        var original = session.Current.GetBytes();
        bool originalModified = session.IsModified;
        long version = session.Current.Version;
        var before = (await view.RenderThumbnailAsync(0, 600, 842, default))!;
        Assert.True(RedShown(before));
        Assert.True(BlueShown(before));

        await view.SetLayerOverridesAsync(new Dictionary<int, bool> { [0] = false });
        var hidden = (await view.RenderThumbnailAsync(0, 600, 842, default))!;
        Assert.False(RedShown(hidden));
        Assert.True(BlueShown(hidden));
        Assert.Equal(original, session.Current.GetBytes());
        Assert.Equal(version, session.Current.Version);
        Assert.Equal(originalModified, session.IsModified);
        Assert.False(session.History.CanUndo);

        await LayersPanel.SaveVisibilityAsync(session, view);
        Assert.Empty(view.LayerOverrides);
        Assert.True(session.History.CanUndo);
        var tree = await LayerDocument.ReadAsync(session.Current.GetBytes());
        Assert.False(tree.Roots[0].Visible);
        Assert.True(tree.Roots[1].Visible);
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (RedShown((await view.RenderThumbnailAsync(0, 600, 842, default))!))
        {
            Assert.True(DateTime.UtcNow < limit, "render po zapisie");
            await Task.Delay(30);
        }

        await session.History.UndoAsync();
        Assert.Equal(original, session.Current.GetBytes());
    });

    [Fact]
    public async Task Hidden_state_from_file_is_respected_by_pdfium_and_automatic_state_is_replaced_by_choice()
    {
        var pdf = TwoLayers((doc, config, a, _) =>
        {
            var usage = new PdfDictionary(doc);
            var view = new PdfDictionary(doc);
            view.Elements["/ViewState"] = new PdfName("/OFF");
            usage.Elements["/View"] = view;
            a.Elements["/Usage"] = usage;
            var entry = new PdfDictionary(doc);
            entry.Elements["/Event"] = new PdfName("/View");
            entry.Elements["/Category"] = new PdfArray(doc, new PdfName("/View"));
            entry.Elements["/OCGs"] = new PdfArray(doc, a.Reference!);
            config.Elements["/AS"] = new PdfArray(doc, entry);
        });
        Assert.False((await LayerDocument.ReadAsync(pdf)).Roots[0].Visible);
        var red = await Pixel(pdf);
        Assert.False(red);
        Assert.True(await Pixel(await LayerDocument.ApplyToCopyAsync(pdf, new Dictionary<int, bool> { [0] = true })));
    }

    private static async Task<bool> Pixel(byte[] pdf)
    {
        using var doc = await new PdfiumRenderer().OpenAsync(pdf);
        var bmp = await doc.RenderPageAsync(0, new RenderRequest(1.0));
        int o = (int)(1.0 * (842 - 350)) * bmp.Stride + 350 * 4;
        return bmp.Bgra[o + 2] > 200 && bmp.Bgra[o] < 60;
    }

    [Fact]
    public async Task Cycles_and_depth_in_order_do_not_crash()
    {
        var pdf = TwoLayers((doc, config, a, b) =>
        {
            var self = new PdfArray(doc);
            doc.Internals.AddObject(self);
            self.Elements.Add(a.Reference!);
            self.Elements.Add(self.Reference!);
            var chainTop = new PdfArray(doc);
            doc.Internals.AddObject(chainTop);
            var cur = chainTop;
            for (int i = 0; i < 200; i++)
            {
                var next = new PdfArray(doc);
                doc.Internals.AddObject(next);
                cur.Elements.Add(next.Reference!);
                cur = next;
            }
            cur.Elements.Add(b.Reference!);
            var shared = new PdfArray(doc, a.Reference!);
            doc.Internals.AddObject(shared);
            var many = new PdfArray(doc);
            for (int i = 0; i < 5000; i++) many.Elements.Add(shared.Reference!);
            config.Elements["/Order"] = new PdfArray(doc, self.Reference!, chainTop.Reference!, many);
        });
        var tree = await LayerDocument.ReadAsync(pdf);
        Assert.Equal(2, tree.LayerCount);
        Assert.Contains(tree.Roots.SelectMany(r => new[] { r }), r => r.Title == "Warstwa A");
    }

    [Fact]
    public async Task Too_many_nodes_are_truncated_with_flag()
    {
        var pdf = TwoLayers((doc, config, a, _) =>
        {
            var many = new PdfArray(doc);
            for (int i = 0; i < LayerDocument.MaxNodes + 50; i++) many.Elements.Add(a.Reference!);
            config.Elements["/Order"] = many;
        });
        var tree = await LayerDocument.ReadAsync(pdf);
        Assert.True(tree.Truncated);
        Assert.Equal(LayerDocument.MaxNodes, tree.Roots.Count);
    }

    [Fact]
    public async Task Cancellation_stops_reading()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LayerDocument.ReadAsync(TwoLayers(), cts.Token));
    }

    [Fact]
    public void Panel_builds_and_lists_layers() => Sta.Run(async () =>
    {
        using var host = new ViewerHost();
        var (session, view) = await host.OpenAsync(TwoLayers());
        host.Documents.ActiveDocument = null;
        host.Documents.ActiveDocument = session;
        var panel = new LayersPanel(host.Services);
        await panel.RefreshAsync();
        var tree = (System.Windows.Controls.TreeView)((System.Windows.Controls.DockPanel)panel.Content).Children.OfType<System.Windows.Controls.TreeView>().Single();
        Assert.Equal(2, tree.Items.Count);
    });
}

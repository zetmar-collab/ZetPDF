using System.IO.Packaging;
using System.Printing;
using System.Windows;
using System.Windows.Documents.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Utilities;

namespace ZetPDF.Viewer.Printing;

/// <summary>
/// Drukowanie: własne okno (drukarka, zakres, kopie, skalowanie, N stron na arkuszu, orientacja auto, komentarze),
/// render stron przez PDFium do bitmap w rozdzielczości drukarki (maks. 300 DPI) i wysyłka arkusz po arkuszu
/// przez <see cref="XpsDocumentWriter"/> (VisualsToXpsDocument) na osobnym wątku STA – interfejs nie jest blokowany,
/// postęp raportowany po każdym arkuszu, anulowanie między arkuszami.
/// </summary>
public sealed class PrintService : IPrintService
{
    private readonly IServiceProvider _services;

    public PrintService(IServiceProvider services) => _services = services;

    private IPdfRenderer Renderer => _services.GetRequiredService<IPdfRenderer>();

    public async Task ShowPrintDialogAsync(IPdfDocumentSession session, int currentPageIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        var loc = _services.GetRequiredService<ILocalizationService>();
        var dialogs = _services.GetService<IDialogService>();
        if (!session.Can(DocumentPermissions.Print)) throw new DocumentPermissionException(DocumentPermissions.Print);

        using var doc = await Renderer.OpenAsync(session.Current.GetBytes());
        var window = new PrintDialogWindow(doc, loc, currentPageIndex);
        bool? ok = dialogs is not null ? dialogs.ShowDialog(window) : window.ShowDialog();
        if (ok != true || window.Result is not { } options) return;

        var title = loc.Format("Viewer.Print.Progress", session.DisplayName);
        if (dialogs is not null)
            await dialogs.RunWithProgressAsync(title, (progress, ct) => PrintAsync(session, options, progress, ct));
        else
            await PrintAsync(session, options);
    }

    public async Task PrintAsync(IPdfDocumentSession session, PrintOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        if (!session.Can(DocumentPermissions.Print)) throw new DocumentPermissionException(DocumentPermissions.Print);
        using var doc = await Renderer.OpenAsync(session.Current.GetBytes(), null, ct);
        await RunSta(() => PrintToQueue(doc, options, progress, ct));
    }

    /// <summary>Druk do pliku XPS tą samą ścieżką co do drukarki (testy, podgląd). Rozmiar papieru: A4 (lub podany).</summary>
    internal static Task PrintToXpsFileAsync(IRenderDocument doc, PrintOptions options, string path, Size? paper = null, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        RunSta(() =>
        {
            var size = paper ?? new Size(PdfSize.A4.Width * PrintLayout.DipPerPoint, PdfSize.A4.Height * PrintLayout.DipPerPoint);
            using var xps = new XpsDocument(path, FileAccess.ReadWrite, CompressionOption.Normal);
            var writer = XpsDocument.CreateXpsDocumentWriter(xps);
            var printable = new Rect(new Point(24, 24), new Size(size.Width - 48, size.Height - 48));
            WriteSheets(writer, null, doc, options, size, printable, 300, progress, ct);
        });

    private static void PrintToQueue(IRenderDocument doc, PrintOptions options, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var server = new LocalPrintServer();
        using var queue = string.IsNullOrWhiteSpace(options.PrinterName) ? LocalPrintServer.GetDefaultPrintQueue() : server.GetPrintQueue(options.PrinterName);
        var ticket = queue.UserPrintTicket?.Clone() ?? new PrintTicket();
        ticket.CopyCount = Math.Max(1, options.Copies);
        ticket.Collation = options.Collate ? Collation.Collated : Collation.Uncollated;
        if (options.Grayscale) ticket.OutputColor = OutputColor.Grayscale;
        var caps = queue.GetPrintCapabilities(ticket);
        double paperW = ticket.PageMediaSize?.Width ?? PdfSize.A4.Width * PrintLayout.DipPerPoint;
        double paperH = ticket.PageMediaSize?.Height ?? PdfSize.A4.Height * PrintLayout.DipPerPoint;
        var area = caps.PageImageableArea;
        Rect printable = area is null
            ? new Rect(24, 24, paperW - 48, paperH - 48)
            : new Rect(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight);
        if (ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape)
        {
            (paperW, paperH) = (paperH, paperW);
            printable = new Rect(printable.Y, printable.X, printable.Height, printable.Width);
        }
        double dpi = ticket.PageResolution?.X ?? 300;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        WriteSheets(writer, ticket, doc, options, new Size(paperW, paperH), printable, dpi, progress, ct);
    }

    private static void WriteSheets(XpsDocumentWriter writer, PrintTicket? ticket, IRenderDocument doc, PrintOptions options, Size paper, Rect printable, double dpi,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var pages = PageRange.Parse(options.PageRange, doc.PageCount);
        if (pages.Count == 0) throw new InvalidOperationException("No pages to print.");
        var sheets = PrintLayout.Sheets(pages, options.PagesPerSheet);
        var collator = ticket is null ? writer.CreateVisualsCollator() : writer.CreateVisualsCollator(ticket, ticket);
        collator.BeginBatchWrite();
        bool cancelled = false;
        try
        {
            for (int s = 0; s < sheets.Count; s++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var placements = PrintLayout.LayoutSheet(sheets[s], i => ToSize(doc.Pages[i].DisplaySize), printable, options);
                var bitmaps = RenderBitmapsAsync(doc, placements, dpi, options, ct).GetAwaiter().GetResult();
                var visual = ComposeSheet(paper, bitmaps);
                if (ticket is null) collator.Write(visual); else collator.Write(visual, ticket);
                progress?.Report(OperationProgress.Of(s + 1, sheets.Count));
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        if (cancelled)
        {
            collator.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }
        collator.EndBatchWrite();
    }

    private static Size ToSize(PdfSize s) => new(s.Width, s.Height);

    internal static RenderFlags FlagsFor(PrintOptions options) =>
        RenderFlags.Printing | (options.PrintAnnotations ? RenderFlags.Annotations | RenderFlags.FormFields : RenderFlags.None) | (options.Grayscale ? RenderFlags.Grayscale : RenderFlags.None);

    /// <summary>Render stron arkusza (PDFium) – bitmapy zamrożone, więc można ich użyć na dowolnym wątku.</summary>
    internal static async Task<IReadOnlyList<(CellPlacement Placement, BitmapSource Bitmap)>> RenderBitmapsAsync(IRenderDocument doc, IReadOnlyList<CellPlacement> placements, double dpi, PrintOptions options, CancellationToken ct)
    {
        var list = new List<(CellPlacement, BitmapSource)>(placements.Count);
        foreach (var p in placements)
        {
            ct.ThrowIfCancellationRequested();
            var size = ToSize(doc.Pages[p.PageIndex].DisplaySize);
            double scale = PrintLayout.PixelScale(p, size, dpi);
            var bmp = await doc.RenderPageAsync(p.PageIndex, new RenderRequest(scale, p.Rotate90 ? 90 : 0, null, FlagsFor(options)), ct).ConfigureAwait(false);
            list.Add((p, Ui.ToBitmapSource(bmp)));
        }
        return list;
    }

    /// <summary>Arkusz jako DrawingVisual: strony w komórkach (przycięte do komórki przy rzeczywistym rozmiarze/własnym %).</summary>
    internal static DrawingVisual ComposeSheet(Size paper, IReadOnlyList<(CellPlacement Placement, BitmapSource Bitmap)> pages)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // przezroczysty prostokąt wymusza rozmiar arkusza (bez koloru interfejsu)
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(paper));
            foreach (var (p, bmp) in pages)
            {
                dc.PushClip(new RectangleGeometry(p.Cell));
                dc.DrawImage(bmp, p.Destination);
                dc.Pop();
            }
        }
        return visual;
    }

    private static Task RunSta(Action work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); tcs.TrySetResult(); }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        {
            IsBackground = true,
            Name = "ZetPDF Print",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Nazwy drukarek (lokalne i połączone) oraz domyślna.</summary>
    internal static (IReadOnlyList<string> Names, string? Default) GetPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var names = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
                .Select(q => { using (q) return q.FullName; }).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            string? def = null;
            try { using var d = LocalPrintServer.GetDefaultPrintQueue(); def = d.FullName; } catch (PrintQueueException) { }
            return (names, def);
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintServerException or InvalidOperationException)
        {
            return (Array.Empty<string>(), null);
        }
    }
}

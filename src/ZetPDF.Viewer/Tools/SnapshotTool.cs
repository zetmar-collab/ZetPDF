using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tools;

/// <summary>
/// Migawka: zaznaczony prostokątem obszar strony trafia do schowka jako obraz w wysokiej rozdzielczości
/// (co najmniej 300 DPI lub rozdzielczość ekranu przy bieżącym powiększeniu, maks. 8000 px na bok).
/// Obszar jest ograniczony do strony, na której zaczęto przeciąganie.
/// </summary>
public sealed class SnapshotTool : IViewerTool, IPageOverlay
{
    public const double SnapshotDpi = 300;
    public const int MaxSide = 8000;

    private readonly IServiceProvider _services;
    private int _page = -1;
    private PdfPoint _a, _b;
    private bool _pressed;

    public SnapshotTool(IServiceProvider services) => _services = services;

    public string Id => CommandIds.Tool.Snapshot;
    public Cursor Cursor => Cursors.Cross;
    public IPageOverlay? Overlay => this;

    // IPageOverlay – prostokąt w trakcie przeciągania
    public int ZIndex => 900;
    public bool IsVisible => _pressed && _page >= 0;

    public void Render(DrawingContext dc, int pageIndex, PageTransform transform)
    {
        if (pageIndex != _page) return;
        var r = transform.ToView(PdfRect.FromPoints(_a, _b));
        var brush = Application.Current?.TryFindResource(ThemeKeys.ViewerOverlayOutline) as Brush;
        var fill = Application.Current?.TryFindResource(ThemeKeys.ViewerTextSelection) as Brush;
        var pen = brush is null ? null : new Pen(brush, 1) { DashStyle = DashStyles.Dash };
        dc.DrawRectangle(fill, pen, r);
    }

    public void OnActivated(IDocumentView view) => (view as DocumentView)?.UpdateCursor();
    public void OnDeactivated(IDocumentView view) { _pressed = false; view.InvalidateOverlays(); }

    public bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (e.Button != MouseButton.Left || e.PageIndex < 0) return false;
        _page = e.PageIndex;
        _a = _b = e.PagePoint;
        _pressed = true;
        return true;
    }

    public bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed || view is not DocumentView dv) return false;
        var t = dv.GetPageTransform(_page);
        if (t is null) return true;
        var p = t.ToPage(e.ViewPoint);
        var info = dv.RenderDocument?.Pages[_page];
        if (info is not null)
            p = new PdfPoint(Math.Clamp(p.X, info.CropOrigin.X, info.CropOrigin.X + info.Size.Width), Math.Clamp(p.Y, info.CropOrigin.Y, info.CropOrigin.Y + info.Size.Height));
        _b = p;
        view.InvalidateOverlays();
        return true;
    }

    public bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _pressed = false;
        view.InvalidateOverlays();
        var rect = PdfRect.FromPoints(_a, _b);
        if (view is DocumentView dv && rect.Width > 2 && rect.Height > 2) _ = CopyAsync(dv, _page, rect);
        return true;
    }

    public bool OnKeyDown(IDocumentView view, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_pressed) return false;
        _pressed = false;
        view.InvalidateOverlays();
        return true;
    }

    private async Task CopyAsync(DocumentView view, int page, PdfRect rect)
    {
        var loc = (ILocalizationService)_services.GetService(typeof(ILocalizationService))!;
        var shell = _services.GetService(typeof(IShellService)) as IShellService;
        var dialogs = _services.GetService(typeof(IDialogService)) as IDialogService;
        try
        {
            var image = await view.RenderRegionAsync(page, rect, SnapshotDpi, MaxSide);
            if (image is null) return;
            Clipboard.SetImage(image);
            shell?.SetStatus(loc.Format("Viewer.Snapshot.Copied", image.PixelWidth, image.PixelHeight));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            dialogs?.ShowError(ex, loc["Viewer.Snapshot.Error"]);
        }
    }
}

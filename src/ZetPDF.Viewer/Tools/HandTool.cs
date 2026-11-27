using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Viewer.View;

namespace ZetPDF.Viewer.Tools;

/// <summary>
/// Narzędzie Ręka (domyślne, H; spacja włącza je chwilowo w innych narzędziach): przeciąganie przewija widok,
/// kliknięcie w łącze przechodzi do strony albo – po potwierdzeniu – otwiera adres w przeglądarce systemowej
/// (tylko http/https/mailto; ZetPDF sam nie łączy się z siecią).
/// </summary>
public sealed class HandTool : IViewerTool
{
    private readonly IServiceProvider _services;
    private Point _start;
    private Point _startOffset;
    private bool _pressed;
    private bool _dragged;

    public HandTool(IServiceProvider services) => _services = services;

    public string Id => CommandIds.Tool.Hand;
    public Cursor Cursor => _pressed ? Cursors.ScrollAll : Cursors.Hand;
    public IPageOverlay? Overlay => null;

    public void OnActivated(IDocumentView view) => (view as DocumentView)?.UpdateCursor();
    public void OnDeactivated(IDocumentView view) => _pressed = false;

    public bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e)
    {
        if (view is not DocumentView dv || e.Button is not (MouseButton.Left or MouseButton.Middle)) return false;
        _pressed = true;
        _dragged = false;
        _start = e.ViewPoint;
        _startOffset = dv.ScrollOffset;
        dv.UpdateCursor();
        return true;
    }

    public bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e)
    {
        if (view is not DocumentView dv) return false;
        if (!_pressed)
        {
            dv.SetHoverCursor(dv.HitLink(e.PageIndex, e.PagePoint) is not null ? Cursors.Arrow : null);
            return false;
        }
        var d = e.ViewPoint - _start;
        if (!_dragged && Math.Abs(d.X) + Math.Abs(d.Y) > 3) _dragged = true;
        if (_dragged) dv.ScrollTo(new Point(_startOffset.X - d.X, _startOffset.Y - d.Y));
        return true;
    }

    public bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e)
    {
        if (!_pressed) return false;
        _pressed = false;
        if (view is not DocumentView dv) return true;
        dv.UpdateCursor();
        if (!_dragged && e.Button == MouseButton.Left && dv.HitLink(e.PageIndex, e.PagePoint) is { } link) Follow(dv, link);
        return true;
    }

    public bool OnKeyDown(IDocumentView view, KeyEventArgs e) => false;

    private void Follow(DocumentView view, PdfLinkInfo link)
    {
        if (link.Kind == PdfLinkKind.GoToPage && link.TargetPageIndex is int page)
        {
            view.GoToPage(page, link.TargetPoint);
            return;
        }
        if (link.Kind != PdfLinkKind.Uri || string.IsNullOrWhiteSpace(link.Uri)) return;
        if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return;
        var dialogs = _services.GetService(typeof(IDialogService)) as IDialogService;
        var loc = (ILocalizationService)_services.GetService(typeof(ILocalizationService))!;
        if (dialogs is null || !dialogs.Confirm(loc.Format("Viewer.Link.OpenConfirm", ZetPDF.Core.Services.LinkSafety.Describe(uri)), loc["Viewer.Link.OpenTitle"], MessageSeverity.Warning)) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { dialogs.ShowError(ex); }
    }
}

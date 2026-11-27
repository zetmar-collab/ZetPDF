using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ZetPDF.Viewer.View;

/// <summary>
/// Wirtualizowana powierzchnia stron (zawartość ScrollViewera, <see cref="IScrollInfo"/>).
/// Nie tworzy kontrolki na stronę – w OnRender rysuje wyłącznie strony przecinające okno (prostokąty z
/// <see cref="PageLayout"/>), bitmapy bierze z pamięci podręcznej widoku. Nad stronami leży
/// <see cref="OverlayLayer"/> (wyniki wyszukiwania, zaznaczenie, nakładki modułów). Układ współrzędnych
/// prezentera = układ okna widoku (przesunięcie przewijania jest już odjęte).
/// </summary>
internal sealed class PagesPresenter : FrameworkElement, IScrollInfo
{
    public static readonly DependencyProperty BackgroundBrushProperty = Register(nameof(BackgroundBrush));
    public static readonly DependencyProperty PageBorderBrushProperty = Register(nameof(PageBorderBrush));
    public static readonly DependencyProperty PageShadowBrushProperty = Register(nameof(PageShadowBrush));

    private static DependencyProperty Register(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(PagesPresenter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DocumentView _owner;
    private Size _viewport;
    private Point _offset;

    public PagesPresenter(DocumentView owner)
    {
        _owner = owner;
        Overlay = new OverlayLayer(owner);
        AddVisualChild(Overlay);
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public Brush? BackgroundBrush { get => (Brush?)GetValue(BackgroundBrushProperty); set => SetValue(BackgroundBrushProperty, value); }
    public Brush? PageBorderBrush { get => (Brush?)GetValue(PageBorderBrushProperty); set => SetValue(PageBorderBrushProperty, value); }
    public Brush? PageShadowBrush { get => (Brush?)GetValue(PageShadowBrushProperty); set => SetValue(PageShadowBrushProperty, value); }

    public OverlayLayer Overlay { get; }

    public Size Viewport => _viewport;
    public Point Offset => _offset;

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? Overlay : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
        Overlay.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Overlay.Arrange(new Rect(finalSize));
        if (finalSize != _viewport)
        {
            _viewport = finalSize;
            _owner.OnViewportSizeChanged();
        }
        return finalSize;
    }

    /// <summary>Po zmianie układu (zoom, tryb, obrót, liczba stron): przelicz zakres przewijania.</summary>
    public void OnLayoutChanged()
    {
        ClampOffset();
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateAll();
    }

    public void InvalidateAll()
    {
        InvalidateVisual();
        Overlay.InvalidateVisual();
    }

    /// <summary>Prostokąt strony w układzie prezentera (lub Empty). Zaokrąglony do pikseli urządzenia.</summary>
    public Rect PageRectInView(int pageIndex)
    {
        var r = _owner.PageGeometry.GetPageRect(pageIndex);
        if (r.IsEmpty) return r;
        double dpi = PixelsPerDip;
        double x = Math.Round((r.X - _offset.X) * dpi) / dpi;
        double y = Math.Round((r.Y - _offset.Y) * dpi) / dpi;
        return new Rect(x, y, r.Width, r.Height);
    }

    public double PixelsPerDip
    {
        get
        {
            try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch (InvalidOperationException) { return 1.0; }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(_viewport);
        dc.DrawRectangle(BackgroundBrush ?? Brushes.Transparent, null, bounds);
        var layout = _owner.PageGeometry;
        var borderPen = PageBorderBrush is { } bb ? new Pen(bb, 1) : null;
        foreach (int page in layout.PlacedPages)
        {
            var r = PageRectInView(page);
            if (!r.IntersectsWith(bounds)) continue;
            if (PageShadowBrush is { } shadow) dc.DrawRectangle(shadow, null, new Rect(r.X + 2, r.Y + 3, r.Width, r.Height));
            // Tło = kolor papieru strony PDF (treść dokumentu, nie kolor interfejsu); w trybie nocnym i sepii – przeliczony.
            dc.DrawRectangle(_owner.PaperBrush, null, r);
            _owner.DrawPageBitmaps(dc, page, r);
            if (borderPen is not null) dc.DrawRectangle(null, borderPen, new Rect(r.X - 0.5, r.Y - 0.5, r.Width + 1, r.Height + 1));
        }
    }

    // ------------------------------------------------------------------ mysz i klawiatura → widok

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_owner.HandlePointerPressed(e)) { CaptureMouse(); e.Handled = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _owner.HandlePointerMoved(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _owner.HandlePointerReleased(e);
        if (IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released)
            ReleaseMouseCapture();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_owner.HandleWheel(e)) e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && _owner.HandleNavigationKey(e)) e.Handled = true;
    }

    // ------------------------------------------------------------------ IScrollInfo

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; } = true;
    public double ExtentWidth => Math.Max(_owner.PageGeometry.Extent.Width, _viewport.Width);
    public double ExtentHeight => Math.Max(_owner.PageGeometry.Extent.Height, _viewport.Height);
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private static double Line => 48;
    private static double Wheel => Math.Max(1, SystemParameters.WheelScrollLines) * 40;

    public void LineUp() => SetVerticalOffset(_offset.Y - Line);
    public void LineDown() => SetVerticalOffset(_offset.Y + Line);
    public void LineLeft() => SetHorizontalOffset(_offset.X - Line);
    public void LineRight() => SetHorizontalOffset(_offset.X + Line);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height * 0.9);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height * 0.9);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width * 0.9);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width * 0.9);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - Wheel);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + Wheel);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - Wheel);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + Wheel);

    public void SetHorizontalOffset(double offset) => SetOffset(new Point(offset, _offset.Y));
    public void SetVerticalOffset(double offset) => SetOffset(new Point(_offset.X, offset));

    public void SetOffset(Point p)
    {
        var clamped = Clamp(p);
        if (clamped == _offset) return;
        _offset = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateAll();
        _owner.OnScrolled();
    }

    public bool IsAtBottom => _offset.Y >= ExtentHeight - _viewport.Height - 0.5;
    public bool IsAtTop => _offset.Y <= 0.5;

    private Point Clamp(Point p) => new(
        Math.Clamp(double.IsNaN(p.X) ? 0 : p.X, 0, Math.Max(0, ExtentWidth - _viewport.Width)),
        Math.Clamp(double.IsNaN(p.Y) ? 0 : p.Y, 0, Math.Max(0, ExtentHeight - _viewport.Height)));

    private void ClampOffset() => _offset = Clamp(_offset);

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}

/// <summary>Warstwa nakładek nad stronami (nie przechwytuje myszy).</summary>
internal sealed class OverlayLayer : FrameworkElement
{
    public static readonly DependencyProperty SearchHitBrushProperty = Register(nameof(SearchHitBrush));
    public static readonly DependencyProperty SearchHitCurrentBrushProperty = Register(nameof(SearchHitCurrentBrush));
    public static readonly DependencyProperty SelectionBrushProperty = Register(nameof(SelectionBrush));
    public static readonly DependencyProperty OutlineBrushProperty = Register(nameof(OutlineBrush));
    public static readonly DependencyProperty HandleBrushProperty = Register(nameof(HandleBrush));

    private static DependencyProperty Register(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(OverlayLayer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DocumentView _owner;

    public OverlayLayer(DocumentView owner)
    {
        _owner = owner;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public Brush? SearchHitBrush { get => (Brush?)GetValue(SearchHitBrushProperty); set => SetValue(SearchHitBrushProperty, value); }
    public Brush? SearchHitCurrentBrush { get => (Brush?)GetValue(SearchHitCurrentBrushProperty); set => SetValue(SearchHitCurrentBrushProperty, value); }
    public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush? OutlineBrush { get => (Brush?)GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }
    public Brush? HandleBrush { get => (Brush?)GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty, value); }

    protected override void OnRender(DrawingContext dc) => _owner.RenderOverlays(dc, new Rect(RenderSize));
}

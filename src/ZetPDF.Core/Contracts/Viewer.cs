using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Widok dokumentu, narzędzia, nakładki, zakładki, druk – implementacja: ZetPDF.Viewer (renderer-viewer)
//  Moduły komentarzy/formularzy/edycji/redakcji rysują i reagują na mysz WYŁĄCZNIE przez
//  IViewerTool + IPageOverlay i przeliczają współrzędne przez IDocumentView / PageTransform.
// =====================================================================================

/// <summary>Tworzy widok dla sesji (powłoka wkłada <see cref="IDocumentView.Element"/> do karty).</summary>
public interface IDocumentViewFactory
{
    IDocumentView CreateView(IPdfDocumentSession session);
}

/// <summary>Dostęp do aktywnego widoku (aktywnej karty).</summary>
public interface IViewerService
{
    IDocumentView? ActiveView { get; }
    event EventHandler<IDocumentView?>? ActiveViewChanged;

    /// <summary>Widok danej sesji (jeśli istnieje).</summary>
    IDocumentView? GetView(IPdfDocumentSession session);
}

public enum ZoomMode { Custom, FitPage, FitWidth, FitVisible, ActualSize }

public enum PageLayoutMode { SinglePage, Continuous, TwoPages, TwoPagesContinuous }

/// <summary>Widok jednego dokumentu.</summary>
public interface IDocumentView : IDisposable
{
    IPdfDocumentSession Session { get; }

    /// <summary>Kontrolka WPF widoku (umieszczana przez powłokę w karcie).</summary>
    FrameworkElement Element { get; }

    int PageCount { get; }
    int CurrentPageIndex { get; }
    event EventHandler? CurrentPageChanged;
    void GoToPage(int pageIndex, PdfPoint? target = null);

    /// <summary>Powiększenie: 1.0 = 100%.</summary>
    double Zoom { get; set; }
    ZoomMode ZoomMode { get; set; }
    PageLayoutMode Layout { get; set; }
    /// <summary>Obrót widoku 0/90/180/270 (nie zmienia pliku).</summary>
    int ViewRotation { get; set; }
    /// <summary>
    /// Czy rysować komentarze (adnotacje inne niż widżety pól). false = render bez <see cref="RenderFlags.Annotations"/>
    /// (FPDF_ANNOT), z <see cref="RenderFlags.FormFields"/> (FPDF_FFLDraw rysuje tylko widżety). Nie zmienia pliku.
    /// </summary>
    bool ShowAnnotations { get; set; }
    event EventHandler? ViewStateChanged;

    /// <summary>Strony zaznaczone w panelu miniatur (0-based, posortowane); gdy pusto – bieżąca strona.</summary>
    IReadOnlyList<int> SelectedPages { get; set; }
    event EventHandler? SelectedPagesChanged;

    /// <summary>Bieżące zaznaczenie tekstu (narzędzie Zaznacz tekst) lub null.</summary>
    PdfTextSelection? TextSelection { get; }
    event EventHandler? TextSelectionChanged;
    void ClearTextSelection();

    /// <summary>Przekształcenie strona↔widok dla strony (null, gdy strona nie jest zrealizowana w układzie).</summary>
    PageTransform? GetPageTransform(int pageIndex);

    /// <summary>Trafienie punktu widoku (współrzędne <see cref="Element"/>) w stronę.</summary>
    bool TryHitPage(Point viewPoint, out int pageIndex, out PdfPoint pagePoint);

    /// <summary>Przewija tak, by prostokąt strony był widoczny.</summary>
    void ScrollIntoView(int pageIndex, PdfRect rect);

    /// <summary>Nakładki rysowane nad stronami (komentarze w trakcie tworzenia, uchwyty, redakcje, wyniki porównania…).</summary>
    void AddOverlay(IPageOverlay overlay);
    void RemoveOverlay(IPageOverlay overlay);
    /// <summary>Przerysuj nakładki (np. po zmianie stanu narzędzia).</summary>
    void InvalidateOverlays();

    /// <summary>Tekst strony z cache widoku (PDFium) – dla narzędzi potrzebujących znaków (adiustacja, redakcja).</summary>
    Task<PageText> GetPageTextAsync(int pageIndex, CancellationToken ct = default);
}

/// <summary>Zaznaczony tekst: fragmenty per strona (czworokąty wierszy) i sam tekst.</summary>
public sealed record PdfTextSelection(IReadOnlyList<TextSelectionSpan> Spans, string Text);

public sealed record TextSelectionSpan(int PageIndex, int StartCharIndex, int CharCount, IReadOnlyList<PdfQuad> Quads);

/// <summary>
/// Nakładka rysowana nad stroną. Widok woła Render dla każdej widocznej strony.
/// Rysuj w układzie widoku używając transform.ToView(...). Bez kolorów na sztywno w UI – pędzle z motywu
/// (wyjątek: kolor treści wybrany przez użytkownika, np. kolor wyróżnienia).
/// </summary>
public interface IPageOverlay
{
    /// <summary>Kolejność (większe nad mniejszymi). Konwencja: 100 komentarze, 200 formularze, 300 edycja, 400 redakcja, 900 narzędzie aktywne.</summary>
    int ZIndex { get; }
    bool IsVisible { get; }
    void Render(DrawingContext dc, int pageIndex, PageTransform transform);
}

/// <summary>Narzędzie myszy (Ręka, Zaznacz tekst, Wyróżnij, Prostokąt, Pole tekstowe formularza, Redakcja obszaru…).</summary>
public interface IViewerTool
{
    /// <summary>Identyfikator – zwykle id polecenia wstążki, które je aktywuje (np. CommandIds.Comment.Highlight).</summary>
    string Id { get; }
    Cursor Cursor { get; }

    void OnActivated(IDocumentView view);
    void OnDeactivated(IDocumentView view);

    /// <summary>Zwróć true, jeśli zdarzenie obsłużone (widok nie przewija/nie zaznacza).</summary>
    bool OnPointerPressed(IDocumentView view, PagePointerEventArgs e);
    bool OnPointerMoved(IDocumentView view, PagePointerEventArgs e);
    bool OnPointerReleased(IDocumentView view, PagePointerEventArgs e);
    bool OnKeyDown(IDocumentView view, KeyEventArgs e);

    /// <summary>Nakładka narzędzia (np. rysowany prostokąt); może być null.</summary>
    IPageOverlay? Overlay { get; }
}

/// <summary>Zdarzenie myszy/pióra przeliczone na stronę.</summary>
public sealed class PagePointerEventArgs : EventArgs
{
    public PagePointerEventArgs(int pageIndex, PdfPoint pagePoint, Point viewPoint, MouseButton? button, ModifierKeys modifiers, int clickCount, double pressure = 0.5)
    {
        PageIndex = pageIndex; PagePoint = pagePoint; ViewPoint = viewPoint; Button = button; Modifiers = modifiers; ClickCount = clickCount; Pressure = pressure;
    }

    /// <summary>-1, gdy kursor poza stronami.</summary>
    public int PageIndex { get; }
    public PdfPoint PagePoint { get; }
    public Point ViewPoint { get; }
    public MouseButton? Button { get; }
    public ModifierKeys Modifiers { get; }
    public int ClickCount { get; }
    /// <summary>Nacisk pióra 0..1 (mysz: 0.5).</summary>
    public double Pressure { get; }
}

/// <summary>Aktywne narzędzie (jedno na aplikację; stosowane do aktywnego widoku).</summary>
public interface IToolService
{
    IViewerTool ActiveTool { get; }
    event EventHandler? ActiveToolChanged;

    /// <summary>Aktywuje narzędzie (poprzednie dostaje OnDeactivated).</summary>
    void Activate(IViewerTool tool);

    /// <summary>Wraca do narzędzia domyślnego (Ręka).</summary>
    void ActivateDefault();

    /// <summary>Czy aktywne jest narzędzie o danym id (do IsChecked przełączników wstążki).</summary>
    bool IsActive(string toolId);
}

// ------------------------------------------------------------------ zakładki

/// <summary>Węzeł drzewa zakładek. PageIndex = -1, gdy cel nieznany/zewnętrzny.</summary>
public sealed record BookmarkNode(string Title, int PageIndex, IReadOnlyList<BookmarkNode> Children, PdfPoint? Target = null, bool IsOpen = false, bool Bold = false, bool Italic = false, PdfColor? Color = null)
{
    public static IReadOnlyList<BookmarkNode> NoChildren { get; } = Array.Empty<BookmarkNode>();
}

/// <summary>Odczyt/zapis drzewa zakładek (PDFsharp). Implementacja: ZetPDF.Viewer (renderer-viewer).</summary>
public interface IBookmarkService
{
    Task<IReadOnlyList<BookmarkNode>> GetBookmarksAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Zastępuje całe drzewo zakładek. Zwraca nowe bajty.</summary>
    Task<byte[]> SetBookmarksAsync(byte[] pdf, IReadOnlyList<BookmarkNode> bookmarks, CancellationToken ct = default);
}

// ------------------------------------------------------------------ druk

public enum PrintScaling { FitToPaper, ActualSize, Custom }

public sealed record PrintOptions(
    string? PrinterName,
    string PageRange,
    int Copies = 1,
    bool Collate = true,
    PrintScaling Scaling = PrintScaling.FitToPaper,
    double CustomScalePercent = 100,
    int PagesPerSheet = 1,
    bool AutoOrientation = true,
    bool PrintAnnotations = true,
    bool Grayscale = false,
    /// <summary>Broszura: 2 strony na stronie arkusza w kolejności składki, druk dwustronny; pomija <see cref="PagesPerSheet"/>.</summary>
    bool Booklet = false);

/// <summary>Drukowanie. Implementacja: ZetPDF.Viewer (renderer-viewer).</summary>
public interface IPrintService
{
    /// <summary>Pokazuje własne okno druku dla sesji i drukuje w tle (z postępem i anulowaniem).</summary>
    Task ShowPrintDialogAsync(IPdfDocumentSession session, int currentPageIndex);

    /// <summary>Drukuje bez okna (np. z linii poleceń).</summary>
    Task PrintAsync(IPdfDocumentSession session, PrintOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

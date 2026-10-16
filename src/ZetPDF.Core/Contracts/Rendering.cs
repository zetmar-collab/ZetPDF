namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Renderowanie (PDFium) – implementacja: ZetPDF.Rendering (renderer-viewer)
//  PDFium NIE jest wątkowo bezpieczny: implementacja wykonuje WSZYSTKIE wywołania PDFium
//  na jednym dedykowanym wątku (kolejka). Metody są asynchroniczne i bezpieczne z dowolnego wątku.
// =====================================================================================

/// <summary>Usługa renderowania (singleton). Zwraca niezależne uchwyty dokumentów.</summary>
public interface IPdfRenderer
{
    /// <summary>
    /// Otwiera dokument z bajtów (kopiuje je lub przypina na czas życia uchwytu).
    /// Wyjątki: <see cref="PdfPasswordRequiredException"/>, <see cref="PdfInvalidPasswordException"/>, <see cref="PdfCorruptException"/>.
    /// </summary>
    Task<IRenderDocument> OpenAsync(byte[] pdf, string? password = null, CancellationToken ct = default);

    /// <summary>Szybkie sprawdzenie pliku bez trzymania uchwytu: czy wymaga hasła, liczba stron, uprawnienia.</summary>
    Task<PdfProbeResult> ProbeAsync(byte[] pdf, string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Zwraca kopię dokumentu BEZ szyfrowania (FPDF_SaveAsCopy z FPDF_REMOVE_SECURITY) – działa także z hasłem
    /// użytkownika (PDFsharp wymaga hasła właściciela do modyfikacji, więc sesja trzyma odszyfrowane bajty).
    /// Zweryfikowane na PDFium 157 + AES-256 (patrz docs/ARCHITEKTURA.md).
    /// </summary>
    Task<byte[]> RemoveSecurityAsync(byte[] pdf, string password, CancellationToken ct = default);
}

public enum PdfProbeStatus { Ok, PasswordRequired, InvalidPassword, Corrupt }

public sealed record PdfProbeResult(
    PdfProbeStatus Status,
    int PageCount,
    bool IsEncrypted,
    /// <summary>Rewizja handlera zabezpieczeń (FPDF_GetSecurityHandlerRevision; -1 brak, 6 = AES-256).</summary>
    int SecurityRevision,
    /// <summary>Uprawnienia z /P przełożone na flagi (przy braku szyfrowania: All).</summary>
    DocumentPermissions Permissions,
    string? ErrorMessage = null);

/// <summary>Otwarty dokument PDFium. Zwolnij (Dispose) – zwalnia uchwyty na wątku PDFium.</summary>
public interface IRenderDocument : IDisposable
{
    int PageCount { get; }

    /// <summary>Informacje o stronach (rozmiar, /Rotate) – wczytane przy otwarciu.</summary>
    IReadOnlyList<PageInfo> Pages { get; }

    /// <summary>Render strony lub jej fragmentu do BGRA (premultiplied), z anulowaniem.</summary>
    Task<RenderedBitmap> RenderPageAsync(int pageIndex, RenderRequest request, CancellationToken ct = default);

    /// <summary>Tekst strony z prostokątami znaków (FPDFText).</summary>
    Task<PageText> GetPageTextAsync(int pageIndex, CancellationToken ct = default);

    /// <summary>Wyszukiwanie w całym dokumencie; trafienia raportowane strumieniowo (progress = strona).</summary>
    IAsyncEnumerable<TextSearchHit> SearchAsync(string query, TextSearchOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Łącza na stronie (do klikania w trybie Ręka).</summary>
    Task<IReadOnlyList<PdfLinkInfo>> GetLinksAsync(int pageIndex, CancellationToken ct = default);

    /// <summary>Drzewo zakładek do odczytu (zapis: <see cref="IBookmarkService"/>).</summary>
    Task<IReadOnlyList<BookmarkNode>> GetBookmarksAsync(CancellationToken ct = default);
}

/// <summary>Strona: rozmiar CropBox w punktach BEZ uwzględnienia /Rotate, obrót strony i początek CropBox.</summary>
public sealed record PageInfo(int Index, PdfSize Size, int Rotation, PdfPoint CropOrigin)
{
    /// <summary>Rozmiar wyświetlany (po /Rotate; 90/270 zamienia szerokość z wysokością).</summary>
    public PdfSize DisplaySize => Rotation % 180 == 0 ? Size : new PdfSize(Size.Height, Size.Width);
}

[Flags]
public enum RenderFlags
{
    None = 0,
    /// <summary>Rysuj adnotacje (FPDF_ANNOT) – domyślnie tak.</summary>
    Annotations = 1,
    /// <summary>Rysuj pola formularza (FPDF_FFLDraw) z bieżącymi wartościami.</summary>
    FormFields = 2,
    Grayscale = 4,
    /// <summary>Tryb druku (FPDF_PRINTING) – adnotacje z flagą Print.</summary>
    Printing = 8,
    /// <summary>Wyłącz wygładzanie tekstu (np. pod OCR nie ustawiaj).</summary>
    NoSmoothText = 16,
    Default = Annotations | FormFields,
}

/// <summary>
/// Parametry renderu. Scale = piksele na punkt (zoom 100% przy 96 DPI = 96/72).
/// ViewRotation = dodatkowy obrót widoku (0/90/180/270, zgodnie z ruchem wskazówek) ponad /Rotate strony.
/// Clip = fragment strony w pikselach WYNIKOWEJ bitmapy całej strony (null = cała strona).
/// </summary>
public sealed record RenderRequest(
    double Scale,
    int ViewRotation = 0,
    PixelRect? Clip = null,
    RenderFlags Flags = RenderFlags.Default,
    uint BackgroundBgra = 0xFFFFFFFF)
{
    public static RenderRequest ForDpi(double dpi, RenderFlags flags = RenderFlags.Default) => new(dpi / 72.0, Flags: flags);
}

/// <summary>Prostokąt w pikselach (początek w lewym górnym rogu).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Bitmapa BGRA32 (premultiplied), wiersze od góry. Niezależna od WPF (Viewer konwertuje do BitmapSource).</summary>
public sealed record RenderedBitmap(int Width, int Height, int Stride, byte[] Bgra, double Scale);

/// <summary>Tekst strony. Chars[i] odpowiada Text[i] (znaki generowane przez PDFium, np. \r\n, mają pusty Box).</summary>
public sealed record PageText(int PageIndex, string Text, IReadOnlyList<TextChar> Chars)
{
    public static PageText Empty(int pageIndex) => new(pageIndex, string.Empty, Array.Empty<TextChar>());
}

/// <summary>Znak z prostokątem w punktach strony (Y w górę), rozmiarem czcionki i kątem.</summary>
public readonly record struct TextChar(char Char, PdfRect Box, double FontSize, bool IsGenerated);

[Flags]
public enum TextSearchOptions
{
    None = 0,
    MatchCase = 1,
    WholeWord = 2,
}

/// <summary>Trafienie: strona, indeks pierwszego znaku i liczba znaków w <see cref="PageText"/>, prostokąty (wiersze).</summary>
public sealed record TextSearchHit(int PageIndex, int CharIndex, int CharCount, IReadOnlyList<PdfRect> Rects, string Context);

public enum PdfLinkKind { Uri, GoToPage, Other }

public sealed record PdfLinkInfo(int PageIndex, PdfRect Rect, PdfLinkKind Kind, string? Uri, int? TargetPageIndex, PdfPoint? TargetPoint);

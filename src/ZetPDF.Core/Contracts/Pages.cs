namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Strony, oznaczenia stron, optymalizacja, oczyszczanie – implementacja: ZetPDF.Pages (pages-engine)
//
//  KONWENCJA (wszystkie moduły): metody zmieniające dokument mają postać
//      Task<byte[]> XxxAsync(byte[] pdf, Options, IProgress<OperationProgress>? progress, CancellationToken ct)
//  Wejście i wyjście to bajty PDF BEZ szyfrowania. Nie modyfikuj tablicy wejściowej.
//  Polecenie z Cofnij/Ponów buduje się przez Commands.DelegateDocumentCommand (lub DocumentCommands.FromService).
//  Dzięki temu te same metody wywołuje Kreator działań (wsadowo, bez sesji) i testy.
//  Opcje to rekordy z właściwościami init i wartościami domyślnymi – serializowalne System.Text.Json
//  (Kreator działań zapisuje je w JSON).
//  Indeksy stron zawsze 0-based. Zakres stron tekstowy ("1-3,5") parsuje Utilities.PageRange.
// =====================================================================================

/// <summary>Operacje na stronach (karta Organizuj, menu miniatur).</summary>
public interface IPageOperations
{
    /// <summary>Wstawia strony z pliku PDF (sourcePages null = wszystkie) przed indeksem insertAt (== PageCount → na końcu).</summary>
    Task<byte[]> InsertFromFileAsync(byte[] pdf, string sourcePath, string? sourcePassword, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default);

    /// <summary>Wstawia strony z bajtów innego PDF (np. przeciągnięte z innej karty).</summary>
    Task<byte[]> InsertFromBytesAsync(byte[] pdf, byte[] sourcePdf, IReadOnlyList<int>? sourcePages, int insertAt, CancellationToken ct = default);

    Task<byte[]> InsertBlankAsync(byte[] pdf, int insertAt, int count, PdfSize size, CancellationToken ct = default);
    Task<byte[]> DeleteAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default);

    /// <summary>Wyodrębnia strony do nowego PDF (bajty). Usunięcie z oryginału = osobne DeleteAsync.</summary>
    Task<byte[]> ExtractAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default);

    /// <summary>Wyodrębnia każdą stronę do osobnego pliku wg wzorca nazw (np. "{name}_{page}.pdf"). Zwraca ścieżki.</summary>
    Task<IReadOnlyList<string>> ExtractToFilesAsync(byte[] pdf, IReadOnlyList<int> pages, string outputDirectory, string fileNamePattern, string baseName, CancellationToken ct = default);

    /// <summary>Zastępuje strony targetPages stronami sourcePages z pliku (liczności muszą być równe).</summary>
    Task<byte[]> ReplaceAsync(byte[] pdf, IReadOnlyList<int> targetPages, string sourcePath, string? sourcePassword, IReadOnlyList<int> sourcePages, CancellationToken ct = default);

    /// <summary>Duplikuje strony (kopie wstawiane zaraz za oryginałami).</summary>
    Task<byte[]> DuplicateAsync(byte[] pdf, IReadOnlyList<int> pages, CancellationToken ct = default);

    /// <summary>
    /// Przenosi strony tak, by pierwsza z nich znalazła się na pozycji targetIndex liczonej w dokumencie
    /// PRZED przeniesieniem (semantyka przeciągania miniatur: upuszczenie "przed stroną targetIndex";
    /// targetIndex == PageCount → na koniec). Kolejność przenoszonych stron zachowana.
    /// </summary>
    Task<byte[]> MoveAsync(byte[] pdf, IReadOnlyList<int> pages, int targetIndex, CancellationToken ct = default);

    Task<byte[]> SwapAsync(byte[] pdf, int pageA, int pageB, CancellationToken ct = default);

    /// <summary>Odwraca kolejność stron (pages null = cały dokument, inaczej tylko w obrębie podanych pozycji).</summary>
    Task<byte[]> ReverseAsync(byte[] pdf, IReadOnlyList<int>? pages, CancellationToken ct = default);

    /// <summary>Obraca strony o wielokrotność 90° (dodatnie = w prawo), zmienia /Rotate.</summary>
    Task<byte[]> RotateAsync(byte[] pdf, IReadOnlyList<int> pages, int degrees, CancellationToken ct = default);

    Task<byte[]> CropAsync(byte[] pdf, IReadOnlyList<int> pages, CropOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<byte[]> ResizeAsync(byte[] pdf, IReadOnlyList<int> pages, ResizeOptions options, CancellationToken ct = default);

    /// <summary>Przeplata strony dokumentu i drugiego pliku (A1,B1,A2,B2…; reverseSecond dla skanów odwrotnej kolejności).</summary>
    Task<byte[]> InterleaveAsync(byte[] pdf, string secondPath, string? secondPassword, bool reverseSecond, CancellationToken ct = default);

    /// <summary>Spłaszcza adnotacje i/lub pola formularza w treść stron.</summary>
    Task<byte[]> FlattenAsync(byte[] pdf, FlattenOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Etykiety stron (/PageLabels). Pusta lista usuwa etykiety.</summary>
    Task<byte[]> SetPageLabelsAsync(byte[] pdf, IReadOnlyList<PageLabelRange> ranges, CancellationToken ct = default);
    Task<IReadOnlyList<PageLabelRange>> GetPageLabelsAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Dzieli dokument na pliki w outputDirectory. Zwraca ścieżki utworzonych plików.</summary>
    Task<IReadOnlyList<string>> SplitAsync(byte[] pdf, SplitOptions options, string outputDirectory, string baseName, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Scala wiele PDF w jeden (bajty).</summary>
    Task<byte[]> MergeAsync(IReadOnlyList<MergeSource> sources, MergeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Nowy pusty dokument z jedną (lub count) stroną.</summary>
    Task<byte[]> CreateBlankDocumentAsync(PdfSize size, int count = 1, CancellationToken ct = default);
}

public sealed record CropOptions
{
    /// <summary>Marginesy w punktach do odcięcia z CropBox.</summary>
    public double Left { get; init; }
    public double Bottom { get; init; }
    public double Right { get; init; }
    public double Top { get; init; }
    /// <summary>Wykryj i usuń białe marginesy (analiza renderu PDFium).</summary>
    public bool RemoveWhiteMargins { get; init; }
    /// <summary>Ustaw także MediaBox (domyślnie tylko CropBox).</summary>
    public bool ApplyToMediaBox { get; init; }
}

public sealed record ResizeOptions
{
    public PdfSize TargetSize { get; init; } = PdfSize.A4;
    /// <summary>Skaluj treść proporcjonalnie, by zmieściła się (inaczej tylko zmiana ramki, treść wyśrodkowana).</summary>
    public bool ScaleContent { get; init; } = true;
}

public sealed record FlattenOptions
{
    public bool Annotations { get; init; } = true;
    public bool FormFields { get; init; } = true;
    /// <summary>Pomiń adnotacje typu Link i Popup.</summary>
    public bool KeepLinks { get; init; } = true;
}

public enum PageLabelStyle { Decimal, UpperRoman, LowerRoman, UpperLetters, LowerLetters, None }

public sealed record PageLabelRange(int StartPageIndex, PageLabelStyle Style, string Prefix = "", int StartNumber = 1);

public enum SplitMode { EveryNPages, TopLevelBookmarks, MaxFileSize }

public sealed record SplitOptions
{
    public SplitMode Mode { get; init; } = SplitMode.EveryNPages;
    public int PagesPerFile { get; init; } = 1;
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    /// <summary>Wzorzec nazw: {name} nazwa bazowa, {n} numer części, {page} pierwsza strona (1-based), {bookmark} tytuł zakładki.</summary>
    public string FileNamePattern { get; init; } = "{name}_{n}";
}

public sealed record MergeSource(string Path, string? Password = null, IReadOnlyList<int>? Pages = null, byte[]? Data = null);

public sealed record MergeOptions
{
    /// <summary>Dodaj zakładkę najwyższego poziomu z nazwą każdego pliku.</summary>
    public bool BookmarkPerFile { get; init; } = true;
    public bool KeepBookmarks { get; init; } = true;
}

// ------------------------------------------------------------------ oznaczenia stron

/// <summary>Znak wodny, tło, nagłówek/stopka, numeracja stron, Bates. Oznaczenia są rozpoznawalne (do Aktualizuj/Usuń).</summary>
public interface IStampingService
{
    Task<byte[]> AddWatermarkAsync(byte[] pdf, WatermarkOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<byte[]> RemoveWatermarksAsync(byte[] pdf, CancellationToken ct = default);
    Task<byte[]> AddBackgroundAsync(byte[] pdf, BackgroundOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<byte[]> RemoveBackgroundsAsync(byte[] pdf, CancellationToken ct = default);
    Task<byte[]> AddHeaderFooterAsync(byte[] pdf, HeaderFooterOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<byte[]> RemoveHeaderFooterAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Numeracja stron to nagłówek/stopka z jednym polem (osobny znacznik, osobno usuwalna).</summary>
    Task<byte[]> AddPageNumbersAsync(byte[] pdf, PageNumberOptions options, CancellationToken ct = default);

    /// <summary>Bates dla jednego dokumentu; startNumber zwracany w wyniku dla kolejnych plików.</summary>
    Task<BatesResult> AddBatesAsync(byte[] pdf, BatesOptions options, long startNumber, CancellationToken ct = default);
    Task<byte[]> RemoveBatesAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Czy dokument zawiera oznaczenia danego rodzaju (do włączania Aktualizuj/Usuń).</summary>
    Task<PageMarkKind> DetectMarksAsync(byte[] pdf, CancellationToken ct = default);
}

[Flags]
public enum PageMarkKind { None = 0, Watermark = 1, Background = 2, HeaderFooter = 4, PageNumbers = 8, Bates = 16 }

public enum HorizontalPosition { Left, Center, Right }
public enum VerticalPosition { Top, Middle, Bottom }

/// <summary>Kolor ARGB treści PDF (wybór użytkownika – nie kolor interfejsu).</summary>
public readonly record struct PdfColor(byte A, byte R, byte G, byte B)
{
    public static PdfColor Black => new(255, 0, 0, 0);
    public static PdfColor White => new(255, 255, 255, 255);
    public static PdfColor Red => new(255, 220, 30, 30);
    public static PdfColor Yellow => new(255, 255, 230, 0);
    public static PdfColor Gray => new(255, 128, 128, 128);
}

public sealed record TextStyle
{
    /// <summary>Rodzina czcionki systemowej (osadzana jako TrueType – polskie znaki).</summary>
    public string FontFamily { get; init; } = "Arial";
    public double FontSize { get; init; } = 12;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public PdfColor Color { get; init; } = PdfColor.Black;
}

public sealed record WatermarkOptions
{
    /// <summary>Tekst albo ścieżka obrazu (jedno z dwóch).</summary>
    public string? Text { get; init; }
    public string? ImagePath { get; init; }
    public TextStyle Style { get; init; } = new() { FontSize = 72, Color = PdfColor.Gray };
    /// <summary>Skala obrazu względem strony (0..1) lub 0 = rozmiar oryginalny.</summary>
    public double ImageScale { get; init; } = 0.5;
    public double Opacity { get; init; } = 0.3;
    public double RotationDegrees { get; init; } = 45;
    public HorizontalPosition Horizontal { get; init; } = HorizontalPosition.Center;
    public VerticalPosition Vertical { get; init; } = VerticalPosition.Middle;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    /// <summary>Zakres stron ("1-3,5"); pusty = wszystkie.</summary>
    public string PageRange { get; init; } = string.Empty;
    public bool BehindContent { get; init; }
}

public sealed record BackgroundOptions
{
    public PdfColor? Color { get; init; } = PdfColor.White;
    public string? ImagePath { get; init; }
    public double Opacity { get; init; } = 1.0;
    public string PageRange { get; init; } = string.Empty;
}

/// <summary>Makra w tekstach: &lt;&lt;strona&gt;&gt;/&lt;&lt;page&gt;&gt;, &lt;&lt;stron&gt;&gt;/&lt;&lt;pages&gt;&gt;, &lt;&lt;data&gt;&gt;/&lt;&lt;date&gt;&gt;, &lt;&lt;plik&gt;&gt;/&lt;&lt;file&gt;&gt;.</summary>
public sealed record HeaderFooterOptions
{
    public string HeaderLeft { get; init; } = string.Empty;
    public string HeaderCenter { get; init; } = string.Empty;
    public string HeaderRight { get; init; } = string.Empty;
    public string FooterLeft { get; init; } = string.Empty;
    public string FooterCenter { get; init; } = string.Empty;
    public string FooterRight { get; init; } = string.Empty;
    public TextStyle Style { get; init; } = new() { FontSize = 10 };
    public double MarginLeft { get; init; } = 36;
    public double MarginRight { get; init; } = 36;
    public double MarginTop { get; init; } = 24;
    public double MarginBottom { get; init; } = 24;
    public string DateFormat { get; init; } = "d";
    public string PageRange { get; init; } = string.Empty;
    /// <summary>Numer pierwszej strony dla makra &lt;&lt;strona&gt;&gt;.</summary>
    public int StartPageNumber { get; init; } = 1;
}

public sealed record PageNumberOptions
{
    /// <summary>Format z makrami, np. "Strona &lt;&lt;strona&gt;&gt; z &lt;&lt;stron&gt;&gt;".</summary>
    public string Format { get; init; } = "<<page>> / <<pages>>";
    public HorizontalPosition Horizontal { get; init; } = HorizontalPosition.Center;
    public VerticalPosition Vertical { get; init; } = VerticalPosition.Bottom;
    public TextStyle Style { get; init; } = new() { FontSize = 10 };
    public double Margin { get; init; } = 24;
    public int StartNumber { get; init; } = 1;
    public string PageRange { get; init; } = string.Empty;
}

public sealed record BatesOptions
{
    public string Prefix { get; init; } = string.Empty;
    public string Suffix { get; init; } = string.Empty;
    public int Digits { get; init; } = 6;
    public HorizontalPosition Horizontal { get; init; } = HorizontalPosition.Right;
    public VerticalPosition Vertical { get; init; } = VerticalPosition.Bottom;
    public TextStyle Style { get; init; } = new() { FontSize = 10 };
    public double Margin { get; init; } = 24;
}

public sealed record BatesResult(byte[] Pdf, long FirstNumber, long LastNumber);

// ------------------------------------------------------------------ optymalizacja i oczyszczanie

public interface IOptimizerService
{
    Task<OptimizeResult> OptimizeAsync(byte[] pdf, OptimizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Skala szarości (obrazy + kolory w strumieniach treści; rasterize=true – przez rasteryzację z utratą tekstu wektorowego, z ostrzeżeniem w UI).</summary>
    Task<byte[]> ConvertToGrayscaleAsync(byte[] pdf, bool rasterize, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

public sealed record OptimizeOptions
{
    public bool RecompressImages { get; init; } = true;
    /// <summary>Jakość JPEG 1–100.</summary>
    public int JpegQuality { get; init; } = 75;
    /// <summary>Maksymalna rozdzielczość obrazów (DPI względem rozmiaru na stronie); 0 = bez zmniejszania.</summary>
    public int MaxImageDpi { get; init; } = 150;
    public bool RemoveUnusedObjects { get; init; } = true;
    public bool CompressStreams { get; init; } = true;
    public bool RemoveEmbeddedThumbnails { get; init; } = true;
    public bool RemoveMetadata { get; init; }
}

public sealed record OptimizeResult(byte[] Pdf, long SizeBefore, long SizeAfter, int ImagesRecompressed);

public interface ISanitizeService
{
    Task<SanitizeResult> SanitizeAsync(byte[] pdf, SanitizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

public sealed record SanitizeOptions
{
    public bool Metadata { get; init; } = true;
    public bool Comments { get; init; } = true;
    public bool Attachments { get; init; } = true;
    public bool JavaScript { get; init; } = true;
    public bool HiddenLayers { get; init; } = true;
    public bool FormData { get; init; }
    public bool ExternalLinks { get; init; } = true;
    public bool Thumbnails { get; init; } = true;
}

/// <summary>Liczby usuniętych elementów (do raportu w oknie i testów).</summary>
public sealed record SanitizeResult(byte[] Pdf, int MetadataEntriesRemoved, int CommentsRemoved, int AttachmentsRemoved, int ScriptsRemoved, int LayersRemoved, int LinksRemoved);

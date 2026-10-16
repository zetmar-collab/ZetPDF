namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Edycja treści, łącza, konwersje, redakcja – implementacja: ZetPDF.Editing (edit-convert-ocr)
//  OCR – Contracts/Ocr.cs, implementacja ZetPDF.Ocr. Konwencja "bajty → bajty" jak w Pages.cs.
// =====================================================================================

/// <summary>Blok tekstu na stronie (linia lub akapit) – do narzędzia Edytuj tekst.</summary>
public sealed record PdfTextBlock(string Id, int PageIndex, PdfRect Bounds, string Text, string FontName, double FontSize, PdfColor Color, bool Bold, bool Italic);

public interface ITextEditService
{
    Task<IReadOnlyList<PdfTextBlock>> GetTextBlocksAsync(byte[] pdf, int pageIndex, CancellationToken ct = default);

    /// <summary>Zastępuje blok nowym tekstem (stary usuwany ze strumienia, jeśli wykonalne; inaczej zakrywany – patrz OGRANICZENIA).</summary>
    Task<byte[]> ReplaceTextBlockAsync(byte[] pdf, int pageIndex, string blockId, string newText, TextStyle style, CancellationToken ct = default);

    /// <summary>Dodaje nowy tekst (osadzona czcionka TrueType z polskimi znakami). Punkt = lewa linia bazowa.</summary>
    Task<byte[]> AddTextAsync(byte[] pdf, int pageIndex, PdfPoint position, string text, TextStyle style, double? maxWidth = null, CancellationToken ct = default);

    Task<FindReplaceResult> FindReplaceAsync(byte[] pdf, string find, string replace, TextSearchOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

public sealed record FindReplaceResult(byte[] Pdf, int Replaced, int Skipped);

public enum PageObjectKind { Image, Shape, Text, Form }

/// <summary>Obiekt strony (obraz z operatora Do, ścieżka) z obrysem i macierzą CTM.</summary>
public sealed record PageObjectInfo(string Id, int PageIndex, PageObjectKind Kind, PdfRect Bounds, PdfMatrix Matrix);

public enum ShapeKind { Rectangle, Ellipse, Line }

public sealed record ShapeSpec
{
    public ShapeKind Kind { get; init; } = ShapeKind.Rectangle;
    /// <summary>Rectangle/Ellipse – prostokąt; Line – od (Left,Bottom) do (Right,Top).</summary>
    public PdfRect Rect { get; init; }
    public PdfColor? Fill { get; init; }
    public PdfColor? Stroke { get; init; } = PdfColor.Black;
    public double LineWidth { get; init; } = 1;
}

public interface IObjectEditService
{
    Task<IReadOnlyList<PageObjectInfo>> GetObjectsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default);
    /// <summary>Doskłada macierz do obiektu (przesunięcie/skala/obrót) – modyfikacja cm w strumieniu.</summary>
    Task<byte[]> TransformObjectAsync(byte[] pdf, int pageIndex, string objectId, PdfMatrix transform, CancellationToken ct = default);
    Task<byte[]> DeleteObjectAsync(byte[] pdf, int pageIndex, string objectId, CancellationToken ct = default);
    Task<byte[]> ReplaceImageAsync(byte[] pdf, int pageIndex, string objectId, byte[] imageData, CancellationToken ct = default);
    Task<byte[]> AddImageAsync(byte[] pdf, int pageIndex, byte[] imageData, PdfRect rect, CancellationToken ct = default);
    Task<byte[]> AddShapeAsync(byte[] pdf, int pageIndex, ShapeSpec shape, CancellationToken ct = default);
}

public sealed record LinkTarget(string? Uri = null, int? PageIndex = null, PdfPoint? Point = null);

public interface ILinkService
{
    Task<IReadOnlyList<PdfLinkInfo>> GetLinksAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default);
    Task<byte[]> AddLinkAsync(byte[] pdf, int pageIndex, PdfRect rect, LinkTarget target, CancellationToken ct = default);
    /// <summary>linkIndex = indeks w wyniku GetLinksAsync dla tej strony.</summary>
    Task<byte[]> UpdateLinkAsync(byte[] pdf, int pageIndex, int linkIndex, PdfRect rect, LinkTarget target, CancellationToken ct = default);
    Task<byte[]> DeleteLinkAsync(byte[] pdf, int pageIndex, int linkIndex, CancellationToken ct = default);
    /// <summary>Wykrywa http(s)://, www., e-mail w tekście i tworzy łącza. Zwraca liczbę utworzonych.</summary>
    Task<(byte[] Pdf, int Created)> CreateLinksFromUrlsAsync(byte[] pdf, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<byte[]> RemoveAllLinksAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Drzewo zakładek z nagłówków (rozmiar/pogrubienie czcionki, PdfPig). Zapis przez IBookmarkService.</summary>
    Task<IReadOnlyList<BookmarkNode>> GenerateBookmarksFromHeadingsAsync(byte[] pdf, HeadingDetectionOptions options, CancellationToken ct = default);
}

public sealed record HeadingDetectionOptions
{
    public int MaxLevels { get; init; } = 3;
    /// <summary>Minimalny stosunek rozmiaru czcionki nagłówka do tekstu podstawowego.</summary>
    public double MinSizeRatio { get; init; } = 1.15;
    public bool TreatBoldAsHeading { get; init; } = true;
}

// ------------------------------------------------------------------ konwersje

public enum ImagePageSize { FitImage, A4, Letter }
public enum ImageFormat { Png, Jpeg, Tiff, Bmp }

public sealed record ImageToPdfOptions
{
    public ImagePageSize PageSize { get; init; } = ImagePageSize.FitImage;
    public double Margin { get; init; }
    public int JpegQuality { get; init; } = 90;
}

public sealed record ImageExportOptions
{
    public ImageFormat Format { get; init; } = ImageFormat.Png;
    public int Dpi { get; init; } = 150;
    public int JpegQuality { get; init; } = 90;
    /// <summary>TIFF: wszystkie strony w jednym pliku wielostronicowym.</summary>
    public bool MultiPageTiff { get; init; }
    public string PageRange { get; init; } = string.Empty;
    public string FileNamePattern { get; init; } = "{name}_{page}";
}

public sealed record TextExportOptions
{
    /// <summary>Zachowaj układ (spacje odwzorowujące położenie) vs. czysty tekst.</summary>
    public bool PreserveLayout { get; init; }
    public string PageRange { get; init; } = string.Empty;
    public bool PageSeparator { get; init; } = true;
}

public sealed record DocxExportOptions
{
    public bool IncludeImages { get; init; } = true;
    /// <summary>Dla stron bez tekstu (skany) wstaw obraz strony.</summary>
    public bool RasterizePagesWithoutText { get; init; } = true;
    public string PageRange { get; init; } = string.Empty;
}

public interface IConversionService
{
    /// <summary>Obrazy (JPG/PNG/TIFF wielostronicowy/BMP) → PDF.</summary>
    Task<byte[]> ImagesToPdfAsync(IReadOnlyList<string> imagePaths, ImageToPdfOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> PdfToImagesAsync(byte[] pdf, ImageExportOptions options, string outputDirectory, string baseName, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<string> PdfToTextAsync(byte[] pdf, TextExportOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task PdfToDocxAsync(byte[] pdf, string outputPath, DocxExportOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Scala pliki różnych typów (PDF, obrazy, TXT) w jeden PDF w podanej kolejności.</summary>
    Task<byte[]> CombineFilesAsync(IReadOnlyList<string> paths, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    /// <summary>PDF z tekstu (czcionka z polskimi znakami, zawijanie, A4).</summary>
    Task<byte[]> TextToPdfAsync(string text, TextStyle? style = null, CancellationToken ct = default);
    /// <summary>PDF z obrazu w pamięci (np. ze schowka – PNG/BMP bajty).</summary>
    Task<byte[]> ImageDataToPdfAsync(byte[] imageData, ImageToPdfOptions options, CancellationToken ct = default);
}

// ------------------------------------------------------------------ redakcja

[Flags]
public enum SensitiveDataPattern
{
    None = 0,
    Pesel = 1,
    Nip = 2,
    Email = 4,
    PhonePl = 8,
    CardNumber = 16,
    IbanPl = 32,
    All = Pesel | Nip | Email | PhonePl | CardNumber | IbanPl,
}

public sealed record RedactionSearch
{
    public IReadOnlyList<string> Phrases { get; init; } = Array.Empty<string>();
    public SensitiveDataPattern Patterns { get; init; }
    public TextSearchOptions Options { get; init; }
}

public sealed record RedactionCandidate(int PageIndex, string Text, SensitiveDataPattern Pattern, IReadOnlyList<PdfQuad> Quads);

public sealed record RedactionMark(int PageIndex, IReadOnlyList<PdfQuad> Quads, string? OverlayText = null);

public sealed record RedactionApplyOptions
{
    public PdfColor FillColor { get; init; } = PdfColor.Black;
    /// <summary>Usuń adnotacje i pola przecinające obszary.</summary>
    public bool RemoveIntersectingAnnotations { get; init; } = true;
    /// <summary>Usuń metadane zawierające zredagowane frazy.</summary>
    public bool ScrubMetadata { get; init; } = true;
}

public interface IRedactionService
{
    Task<IReadOnlyList<RedactionCandidate>> FindAsync(byte[] pdf, RedactionSearch search, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Oznacza obszary jako adnotacje Redact (podgląd; odwracalne).</summary>
    Task<byte[]> MarkAsync(byte[] pdf, IReadOnlyList<RedactionMark> marks, CancellationToken ct = default);
    /// <summary>Stosuje wszystkie adnotacje Redact: treść TRWALE usunięta ze strumieni (nieodwracalne poza Cofnij).</summary>
    Task<byte[]> ApplyAsync(byte[] pdf, RedactionApplyOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Walidacja wzorca (PESEL/NIP suma kontrolna, karta – Luhn, IBAN – mod 97) – używane też przez testy.</summary>
    bool Validate(SensitiveDataPattern pattern, string value);
}

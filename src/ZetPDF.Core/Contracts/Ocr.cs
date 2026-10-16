namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  OCR – implementacja: ZetPDF.Ocr (edit-convert-ocr). Tesseract 5.2.0, dane pol + eng (tessdata_fast).
//  Render strony przez IPdfRenderer (300 DPI) → Tesseract → słowa z prostokątami → ukryta warstwa
//  tekstu (tryb renderowania 3) dodana do strony.
// =====================================================================================

public sealed record OcrOptions
{
    /// <summary>Języki Tesseract, np. "pol+eng".</summary>
    public string Languages { get; init; } = "pol+eng";
    public int Dpi { get; init; } = 300;
    /// <summary>Strony 0-based; null = wszystkie.</summary>
    public IReadOnlyList<int>? Pages { get; init; }
    /// <summary>Tylko obszar (OCR zaznaczonego obszaru) – wspólny dla wszystkich wybranych stron.</summary>
    public PdfRect? Region { get; init; }
    /// <summary>Pomiń strony, które mają już tekst.</summary>
    public bool SkipPagesWithText { get; init; } = true;
    /// <summary>Próg pewności (0–100) – słowa poniżej to "podejrzane".</summary>
    public int SuspectThreshold { get; init; } = 70;
}

/// <summary>Rozpoznane słowo: tekst, prostokąt w punktach strony, pewność 0–100.</summary>
public sealed record OcrWord(string Text, PdfRect Box, float Confidence, int LineIndex, bool IsSuspect);

public sealed record OcrPageResult(int PageIndex, IReadOnlyList<OcrWord> Words, float MeanConfidence, bool Skipped);

public sealed record OcrResult(IReadOnlyList<OcrPageResult> Pages)
{
    public IEnumerable<OcrWord> SuspectWords => Pages.SelectMany(p => p.Words.Where(w => w.IsSuspect));
}

public interface IOcrService
{
    /// <summary>Czy silnik i dane językowe są dostępne (rozpakowanie natywnych DLL przy pierwszym użyciu).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Rozpoznaje tekst (bez zmiany dokumentu) – wynik do przeglądu podejrzanych słów.</summary>
    Task<OcrResult> RecognizeAsync(byte[] pdf, OcrOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Dodaje ukrytą warstwę tekstu z (ewentualnie poprawionego) wyniku.</summary>
    Task<byte[]> ApplyTextLayerAsync(byte[] pdf, OcrResult result, CancellationToken ct = default);

    /// <summary>Recognize + Apply w jednym kroku (Szybkie rozpoznawanie, Kreator działań).</summary>
    Task<byte[]> OcrDocumentAsync(byte[] pdf, OcrOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>OCR gotowej bitmapy (np. migawka). Box w pikselach bitmapy przeliczony na punkty wg bitmap.Scale.</summary>
    Task<IReadOnlyList<OcrWord>> RecognizeBitmapAsync(RenderedBitmap bitmap, string languages, CancellationToken ct = default);
}

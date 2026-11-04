namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  PDF/A-2b – implementacja: ZetPDF.Editing. Używa: Editing (polecenia), Security (Kreator, „Archiwizuj”).
//  To własne sprawdzenie wybranych reguł, a nie certyfikowana walidacja.
// =====================================================================================

public enum PdfARuleStatus
{
    Passed,
    Failed,
    /// <summary>Reguły nie da się sprawdzić tym programem.</summary>
    NotChecked,
    /// <summary>Informacja bez oceny (np. transparencja, dozwolona w 2b).</summary>
    Info,
}

/// <param name="Rule">Identyfikator reguły; teksty: Edit.PdfA.Rule.&lt;Rule&gt; i Edit.PdfA.Rule.&lt;Rule&gt;.Desc.</param>
/// <param name="Examples">Przykłady z dokumentu (np. nazwy czcionek ze stronami). Tekst niezaufany: przy HTML wymaga escapowania.</param>
public sealed record PdfARuleResult(string Rule, PdfARuleStatus Status, int Count = 0, IReadOnlyList<string>? Examples = null);

public sealed record PdfACheckReport(IReadOnlyList<PdfARuleResult> Results)
{
    public int Passed => Results.Count(r => r.Status == PdfARuleStatus.Passed);
    public int Failed => Results.Count(r => r.Status == PdfARuleStatus.Failed);
    public int NotChecked => Results.Count(r => r.Status == PdfARuleStatus.NotChecked);
    /// <summary>Brak błędów w regułach sprawdzanych. Nie oznacza zgodności z normą.</summary>
    public bool NoFailures => Failed == 0;
}

/// <summary>Co zmieni konwersja tego dokumentu. Liczby do okna zgody i do decyzji Kreatora.</summary>
public sealed record PdfAConversionPlan
{
    /// <summary>Strony, które trzeba zamienić na obraz z ukrytą warstwą tekstu (0-based).</summary>
    public IReadOnlyList<int> PagesToRasterize { get; init; } = Array.Empty<int>();
    /// <summary>Czcionki osadzane z systemu: „Helvetica → Arial”.</summary>
    public IReadOnlyList<string> FontsToEmbed { get; init; } = Array.Empty<string>();
    public int Attachments { get; init; }
    /// <summary>Adnotacje do usunięcia: multimedia, bez wyglądu, ukryte, załączniki na stronie.</summary>
    public int AnnotationsToRemove { get; init; }
    /// <summary>Skrypty i akcje zabronione (JavaScript, uruchamianie plików, multimedia).</summary>
    public int ActionsToRemove { get; init; }
    public bool HasXfa { get; init; }
    public bool IsSigned { get; init; }

    /// <summary>Konwersja usuwa treść, którą użytkownik mógłby chcieć zachować (nie liczą się skrypty).</summary>
    public bool RemovesContent => Attachments > 0 || AnnotationsToRemove > 0 || HasXfa || IsSigned;
}

public sealed record PdfAConvertOptions
{
    /// <summary>Zgoda na zamianę stron na obraz, gdy inaczej się nie da (czcionki nieosadzalne, CMYK).</summary>
    public bool AllowRasterize { get; init; }
    /// <summary>Zgoda na utratę załączników, adnotacji, XFA i ważności podpisów.</summary>
    public bool AllowContentRemoval { get; init; }
    public int RasterDpi { get; init; } = 200;
}

/// <summary>Powód braku konwersji.</summary>
public enum PdfANotConverted { None, NeedsRasterize, RemovesContent, RulesStillFailing }

/// <param name="Pdf">Wynik; null, gdy nie przekonwertowano. Plik z pdfaid powstaje tylko wtedy, gdy własne sprawdzenie nie zgłasza błędów.</param>
/// <param name="After">Własne sprawdzenie wyniku (także gdy wynik odrzucono).</param>
public sealed record PdfAConvertResult(byte[]? Pdf, PdfANotConverted Reason, PdfAConversionPlan Plan, PdfACheckReport? After);

public interface IPdfAService
{
    /// <param name="savedEncrypted">Dokument będzie zapisany z szyfrowaniem (sesja trzyma bajty odszyfrowane).</param>
    Task<PdfACheckReport> CheckAsync(byte[] pdf, bool savedEncrypted = false, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<PdfAConversionPlan> PlanAsync(byte[] pdf, CancellationToken ct = default);
    Task<PdfAConvertResult> ConvertAsync(byte[] pdf, PdfAConvertOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

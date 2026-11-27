using System.Text.Json.Serialization;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Security.Actions;

/// <summary>Krok „Szyfruj”. Hasła NIE są zapisywane w pliku działania – okno pyta o nie przed uruchomieniem.</summary>
public sealed record EncryptStepOptions
{
    public PdfEncryptionMethod Method { get; init; } = PdfEncryptionMethod.Aes256;
    public DocumentPermissions Permissions { get; init; } = DocumentPermissions.All;
    public string? UserPassword { get; init; }
    public string? OwnerPassword { get; init; }
}

/// <summary>Krok „Metadane”. Makro {file}/{plik} = nazwa pliku bez rozszerzenia.</summary>
public sealed record MetadataStepOptions
{
    public string Title { get; init; } = "{file}";
    public string Author { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    /// <summary>Uzupełnij tylko puste pola (nie nadpisuj istniejących).</summary>
    public bool OnlyIfEmpty { get; init; } = true;
}

/// <summary>
/// Krok „PDF/A-2b”. Wsadowo nie ma kogo zapytać, więc domyślnie bez rasteryzacji i bez utraty treści: gdy to niemożliwe, plik
/// przechodzi dalej bez zmian tego kroku i dostaje ostrzeżenie w raporcie.
/// </summary>
public sealed record PdfAStepOptions
{
    public bool AllowRasterize { get; init; }
    public bool AllowContentRemoval { get; init; }
}

/// <summary>
/// Krok „Redakcja według profilu”: wyszukuje frazy i wzorce z profilu zapisanego w oknie „Szukaj i redaguj” i od razu stosuje
/// redakcję (treść trwale usunięta). Gdy nic nie znaleziono, plik przechodzi dalej bez zmian.
/// </summary>
public sealed record RedactStepOptions
{
    public string Profile { get; init; } = string.Empty;
}

/// <summary>Identyfikatory typów kroków (zapisywane w JSON).</summary>
public static class ActionStepTypes
{
    public const string Sanitize = "Sanitize";
    public const string Watermark = "Watermark";
    public const string HeaderFooter = "HeaderFooter";
    public const string PageNumbers = "PageNumbers";
    public const string Optimize = "Optimize";
    public const string Flatten = "Flatten";
    public const string Ocr = "Ocr";
    public const string Encrypt = "Encrypt";
    public const string Metadata = "Metadata";
    public const string PdfA = "PdfA";
    public const string Redact = "Redact";

    /// <summary>Typ → klasa opcji.</summary>
    public static readonly IReadOnlyDictionary<string, Type> OptionTypes = new Dictionary<string, Type>
    {
        [Sanitize] = typeof(SanitizeOptions),
        [Watermark] = typeof(WatermarkOptions),
        [HeaderFooter] = typeof(HeaderFooterOptions),
        [PageNumbers] = typeof(PageNumberOptions),
        [Optimize] = typeof(OptimizeOptions),
        [Flatten] = typeof(FlattenOptions),
        [Ocr] = typeof(OcrOptions),
        [Encrypt] = typeof(EncryptStepOptions),
        [Metadata] = typeof(MetadataStepOptions),
        [PdfA] = typeof(PdfAStepOptions),
        [Redact] = typeof(RedactStepOptions),
    };
}

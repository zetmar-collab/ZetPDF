using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Zabezpieczenia, podpisy, porównanie, Kreator działań – implementacja: ZetPDF.Security (security-signatures)
// =====================================================================================

public interface ISecurityService
{
    /// <summary>
    /// Analiza zaszyfrowanego pliku (oryginalne bajty) i hasła: metoda, rewizja, uprawnienia, czy hasło właściciela
    /// (PDFsharp otwiera w trybie Modify tylko z hasłem właściciela – zweryfikowane).
    /// Dla pliku bez szyfrowania zwraca <see cref="PdfSecurityInfo.None"/>.
    /// </summary>
    Task<PdfSecurityInfo> AnalyzeAsync(byte[] originalPdf, string? password, CancellationToken ct = default);

    /// <summary>Szyfruje (PDFsharp: AES-256 = SetEncryptionToV5, AES-128 = V4UsingAES). Wejście bez szyfrowania.</summary>
    Task<byte[]> EncryptAsync(byte[] pdf, PdfEncryptionSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Ustawienia do ponownego zaszyfrowania przy zapisie dokumentu otwartego z hasłem.
    /// Przy haśle użytkownika (właściciela nieznane) – nowe losowe hasło właściciela, te same uprawnienia (patrz DECYZJE).
    /// </summary>
    PdfEncryptionSettings? CreateResaveSettings(PdfSecurityInfo info);
}

public enum CertificateSource { PfxFile, WindowsStore }

/// <summary>Wybrany identyfikator cyfrowy. Hasła PFX nie zapisujemy na dysku.</summary>
public sealed record SigningCertificate(CertificateSource Source, X509Certificate2 Certificate, string? PfxPath = null);

public sealed record SignatureAppearance
{
    public bool ShowName { get; init; } = true;
    public bool ShowDate { get; init; } = true;
    public bool ShowReason { get; init; } = true;
    public bool ShowLocation { get; init; } = true;
    public byte[]? Image { get; init; }
}

public sealed record SignRequest
{
    public required SigningCertificate Certificate { get; init; }
    /// <summary>Istniejące puste pole podpisu (pełna nazwa) albo null → nowe pole na PageIndex/Rect.</summary>
    public string? FieldName { get; init; }
    public int PageIndex { get; init; }
    /// <summary>Prostokąt widocznego podpisu; pusty = podpis niewidoczny.</summary>
    public PdfRect Rect { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string ContactInfo { get; init; } = string.Empty;
    public SignatureAppearance Appearance { get; init; } = new();
    /// <summary>Adres TSA (z Ustawień); null/pusty = bez znacznika czasu. Połączenie sieciowe na polecenie użytkownika.</summary>
    public string? TsaUrl { get; init; }
    /// <summary>Podpis w profilu PAdES (ETSI.CAdES.detached, ESS signing-certificate-v2, bez atrybutu czasu podpisu).</summary>
    public bool Pades { get; init; }
}

/// <summary>Stan unieważnienia podpisu według danych, które przeszły weryfikację kryptograficzną.</summary>
public enum SignatureRevocationStatus { NotChecked, Good, Revoked, Unknown }

public sealed record LongTermSignatureStatus(string FieldName, SignatureRevocationStatus Status);

/// <summary>Wynik pobrania danych długoterminowych. <see cref="Pdf"/>: dokument z dopisanym /DSS albo bez zmian przy samym sprawdzaniu. Problemy: tekst techniczny na adres.</summary>
public sealed record LongTermResult(byte[] Pdf, int Certificates, int OcspResponses, int Crls, SignatureRevocationStatus Status, IReadOnlyList<LongTermSignatureStatus> Signatures, IReadOnlyList<string> Problems)
{
    /// <summary>Adresy spoza zatwierdzonego zbioru (np. z certyfikatu pobranego po drodze). NIE zostały wywołane; wymagają kolejnej zgody.</summary>
    public IReadOnlyList<string> PendingUrls { get; init; } = Array.Empty<string>();
}

/// <summary>Adresy zatwierdzone przez użytkownika w oknie. Połączenie pod inny adres nie nastąpi. <see cref="Extra"/>: wpisane ręcznie, zatwierdzone z założenia.</summary>
public sealed record LongTermConnectionPlan(IReadOnlyCollection<string> Approved, IReadOnlyList<string> Extra)
{
    /// <summary>Zgoda na adresy w sieci lokalnej (opcja w oknie, domyślnie wyłączona, niezapamiętywana). Pętli zwrotnej i adresów łącza nie obejmuje.</summary>
    public bool AllowLocalNetwork { get; init; }

    public static LongTermConnectionPlan Of(IEnumerable<string> approved, IReadOnlyList<string>? extra = null, bool allowLocalNetwork = false) =>
        new(approved.ToList(), extra ?? Array.Empty<string>()) { AllowLocalNetwork = allowLocalNetwork };
}

/// <summary>
/// Dane długoterminowe podpisu (PAdES B-LT, B-LTA). Jedyne metody tego kontraktu, które łączą się z siecią, to dwie wymienione niżej; wywołuje je wyłącznie polecenie
/// użytkownika po pokazaniu adresów. Weryfikacja <see cref="ISignatureService.ValidateAsync"/> pozostaje bez sieci i korzysta z /DSS.
/// </summary>
public interface ILongTermSignatureService
{
    /// <summary>Adresy OCSP, CRL i wystawców z certyfikatu podpisującego i jego łańcucha (bez sieci).</summary>
    IReadOnlyList<string> GetCertificateUrls(X509Certificate2 certificate);

    /// <summary>Adresy z certyfikatów podpisów dokumentu (bez sieci).</summary>
    Task<IReadOnlyList<string>> GetDocumentUrlsAsync(byte[] pdf, string? password, CancellationToken ct = default);

    /// <summary>SIEĆ. Pobiera dane i dopisuje /DSS przyrostowo.</summary>
    Task<LongTermResult> AddLongTermDataAsync(byte[] pdf, string? password, LongTermConnectionPlan plan, CancellationToken ct = default);

    /// <summary>SIEĆ. Sprawdza unieważnienia, dokument bez zmian.</summary>
    Task<LongTermResult> CheckOnlineAsync(byte[] pdf, string? password, LongTermConnectionPlan plan, CancellationToken ct = default);
}

public enum SignatureStatus { Valid, ValidButModifiedAfter, Invalid, UnknownSigner, Error }

public sealed record SignatureValidationResult
{
    public required string FieldName { get; init; }
    public required SignatureStatus Status { get; init; }
    public string SignerName { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public DateTimeOffset? SigningTime { get; init; }
    public bool HasTimestamp { get; init; }
    public DateTimeOffset? TimestampTime { get; init; }
    /// <summary>Hash zakresu ByteRange zgodny z podpisem CMS.</summary>
    public bool IntegrityOk { get; init; }
    /// <summary>ByteRange obejmuje cały plik (brak późniejszych rewizji).</summary>
    public bool CoversWholeDocument { get; init; }
    public bool CertificateValidAtSigningTime { get; init; }
    /// <summary>Łańcuch zbudowany z magazynu Windows, BEZ pobierania CRL/OCSP.</summary>
    public bool ChainTrusted { get; init; }
    public string Details { get; init; } = string.Empty;
    public int PageIndex { get; init; }
    public PdfRect Rect { get; init; }
    /// <summary>Stan unieważnienia z danych /DSS, bez sieci.</summary>
    public SignatureRevocationStatus RevocationStatus { get; init; }
    public bool HasDss { get; init; }
    /// <summary>Po podpisie doszły wyłącznie dane długoterminowe: /DSS i znaczniki czasu dokumentu.</summary>
    public bool ModifiedOnlyByLongTermData { get; init; }
    /// <summary>Poziom według struktury pliku (B-B, B-T, B-LT, B-LTA) albo pusty. To nie jest wynik zewnętrznego walidatora.</summary>
    public string BaselineLevel { get; init; } = string.Empty;
}

public sealed record DigitalIdRequest(string Name, string Organization, string Email, int ValidityYears, string Password);

public interface ISignatureService
{
    /// <summary>Podpisuje KOŃCOWE bajty (już zaszyfrowane, jeśli trzeba – IDocumentManager.GetBytesForSaveAsync). Zwraca podpisany plik.</summary>
    Task<byte[]> SignAsync(byte[] finalPdf, SignRequest request, string? password, CancellationToken ct = default);

    Task<IReadOnlyList<SignatureValidationResult>> ValidateAsync(byte[] pdf, string? password, CancellationToken ct = default);

    /// <summary>Znacznik czasu dokumentu (DocTimeStamp, ETSI.RFC3161) – jeśli wykonalne.</summary>
    Task<byte[]> TimestampDocumentAsync(byte[] finalPdf, string tsaUrl, string? password, CancellationToken ct = default);

    /// <summary>Certyfikaty z kluczem prywatnym z CurrentUser\My.</summary>
    IReadOnlyList<X509Certificate2> GetStoreCertificates();

    /// <summary>Tworzy identyfikator z podpisem własnym, zapisuje PFX w IAppPaths.DigitalIdsDirectory. Zwraca ścieżkę.</summary>
    Task<string> CreateSelfSignedIdAsync(DigitalIdRequest request, CancellationToken ct = default);
}

// ------------------------------------------------------------------ porównanie

public enum CompareChangeKind { Inserted, Deleted, Changed }

public sealed record CompareChange(int Index, CompareChangeKind Kind, int PageA, int PageB, string TextA, string TextB, IReadOnlyList<PdfRect> RectsA, IReadOnlyList<PdfRect> RectsB);

public sealed record CompareOptions
{
    public bool IgnoreCase { get; init; }
    public bool IgnoreWhitespace { get; init; } = true;
    public bool CompareGraphics { get; init; }
}

public sealed record CompareResult(string NameA, string NameB, int PageCountA, int PageCountB, IReadOnlyList<(int PageA, int PageB)> PageMap, IReadOnlyList<CompareChange> Changes);

public enum CompareReportFormat { Text, Html }

public interface ICompareService
{
    Task<CompareResult> CompareAsync(byte[] pdfA, string nameA, byte[] pdfB, string nameB, CompareOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task ExportReportAsync(CompareResult result, string path, CompareReportFormat format, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Kreator działań

/// <summary>Krok działania: typ (np. "Sanitize", "Watermark", "HeaderFooter", "Optimize", "Encrypt", "Ocr", "Metadata") + opcje JSON.</summary>
public sealed record ActionStep(string StepType, JsonElement Options);

public sealed record ActionDefinition
{
    public required string Id { get; init; }
    /// <summary>Nazwa (wbudowane: klucz lokalizacji z prefiksem "Security.Action.").</summary>
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool IsBuiltIn { get; init; }
    public IReadOnlyList<ActionStep> Steps { get; init; } = Array.Empty<ActionStep>();
}

public sealed record ActionRunOptions
{
    public required string OutputDirectory { get; init; }
    public string FileNameSuffix { get; init; } = string.Empty;
    public bool OverwriteExisting { get; init; }
}

/// <param name="Warning">Plik zapisany, ale z zastrzeżeniem (np. „Archiwizuj” nie mogło zrobić z niego PDF/A).</param>
public sealed record ActionFileResult(string InputPath, string? OutputPath, bool Success, string? Error, string? Warning = null);

public sealed record ActionRunReport(IReadOnlyList<ActionFileResult> Files)
{
    public int Succeeded => Files.Count(f => f.Success);
    public int Failed => Files.Count(f => !f.Success);
}

public interface IActionWizard
{
    /// <summary>Wbudowane (Przygotuj do dystrybucji, Zoptymalizuj dla Internetu, Archiwizuj) + zapisane w IAppPaths.ActionsDirectory.</summary>
    Task<IReadOnlyList<ActionDefinition>> GetActionsAsync(CancellationToken ct = default);
    Task SaveActionAsync(ActionDefinition action, CancellationToken ct = default);
    Task DeleteActionAsync(string actionId, CancellationToken ct = default);

    /// <summary>Typy kroków dostępne w edytorze (id typu → klucz nazwy).</summary>
    IReadOnlyDictionary<string, string> StepTypes { get; }

    /// <summary>Uruchamia działanie na plikach; nie nadpisuje oryginałów (chyba że OverwriteExisting i zgoda w UI).</summary>
    Task<ActionRunReport> RunAsync(ActionDefinition action, IReadOnlyList<string> inputFiles, ActionRunOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

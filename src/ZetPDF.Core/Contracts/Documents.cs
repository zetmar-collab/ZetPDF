using System.Collections.ObjectModel;
using ZetPDF.Core.Documents;

namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Model dokumentu i system poleceń z Cofnij/Ponów (implementacja: ZetPDF.Core.Documents)
// =====================================================================================

/// <summary>
/// Zarządca otwartych dokumentów (karty). Implementacja w Core (<see cref="DocumentManager"/>).
/// Wszystkie metody wołamy z wątku UI; ciężka praca dzieje się w tle, zdarzenia wracają na wątek UI.
/// </summary>
public interface IDocumentManager
{
    ReadOnlyObservableCollection<IPdfDocumentSession> Documents { get; }

    /// <summary>Dokument w aktywnej karcie (ustawia powłoka przy zmianie karty).</summary>
    IPdfDocumentSession? ActiveDocument { get; set; }

    event EventHandler<IPdfDocumentSession?>? ActiveDocumentChanged;
    event EventHandler<IPdfDocumentSession>? DocumentOpened;
    event EventHandler<IPdfDocumentSession>? DocumentClosed;

    /// <summary>
    /// Otwiera plik. Gdy potrzebne hasło, a <paramref name="password"/> jest puste/złe – pyta przez
    /// <see cref="IDialogService.AskPassword"/>. Zwraca null, gdy użytkownik anulował.
    /// Wyjątki: <see cref="PdfCorruptException"/>, <see cref="IOException"/>.
    /// Jeśli plik jest już otwarty – aktywuje istniejącą kartę i ją zwraca.
    /// </summary>
    Task<IPdfDocumentSession?> OpenAsync(string path, string? password = null, CancellationToken ct = default);

    /// <summary>Tworzy nowy, niezapisany dokument z bajtów (np. wynik konwersji, scalania, "Pusty").</summary>
    Task<IPdfDocumentSession> CreateNewAsync(byte[] pdf, string displayName, CancellationToken ct = default);

    /// <summary>Zapisuje (szyfruje, jeśli trzeba) do <see cref="IPdfDocumentSession.FilePath"/> lub do <paramref name="path"/> (Zapisz jako).</summary>
    Task SaveAsync(IPdfDocumentSession session, string? path = null, CancellationToken ct = default);

    /// <summary>
    /// Bajty gotowe do zapisu: bieżący zrzut + szyfrowanie z <see cref="DocumentSnapshot.Encryption"/>.
    /// Używa tego np. podpis cyfrowy (szyfrowanie musi nastąpić PRZED podpisem).
    /// </summary>
    Task<byte[]> GetBytesForSaveAsync(IPdfDocumentSession session, CancellationToken ct = default);

    /// <summary>
    /// Rejestruje hak przed zapisem: przekształcenie bajtów BEZ szyfrowania wykonywane w <see cref="GetBytesForSaveAsync"/>
    /// (więc przy Zapisz, Zapisz jako i podpisie) przed szyfrowaniem, w kolejności rejestracji. Sesja (zrzut, historia)
    /// się nie zmienia – zmienia się tylko zapisany plik. Dispose wyniku wyrejestrowuje hak.
    /// </summary>
    IDisposable AddSaveTransform(SaveTransform transform);

    /// <summary>Wczytuje ponownie plik z dysku (Przywróć) – czyści historię Cofnij/Ponów.</summary>
    Task RevertAsync(IPdfDocumentSession session, CancellationToken ct = default);

    /// <summary>Zamyka kartę BEZ pytania o zapis (pytanie zadaje powłoka wcześniej).</summary>
    void Close(IPdfDocumentSession session);
}

/// <summary>
/// Hak przed zapisem (<see cref="IDocumentManager.AddSaveTransform"/>). Wołany w tle; zwróć <paramref name="pdf"/>
/// bez zmian, gdy hak nie dotyczy sesji. Nie modyfikuj tablicy wejściowej.
/// </summary>
public delegate Task<byte[]> SaveTransform(IPdfDocumentSession session, byte[] pdf, CancellationToken ct);

/// <summary>
/// Jeden otwarty dokument. Bieżąca wersja to niezmienny <see cref="DocumentSnapshot"/> z bajtami PDF
/// BEZ szyfrowania (szyfrowanie stosuje się dopiero przy zapisie). Każda zmiana = <see cref="IDocumentCommand"/>
/// wykonany przez <see cref="ExecuteAsync"/>; Cofnij/Ponów przywraca zrzuty.
/// </summary>
public interface IPdfDocumentSession
{
    Guid Id { get; }

    /// <summary>Pełna ścieżka pliku lub null dla dokumentu jeszcze nie zapisanego.</summary>
    string? FilePath { get; }

    /// <summary>Nazwa w karcie (nazwa pliku lub nazwa nadana przy tworzeniu).</summary>
    string DisplayName { get; }

    /// <summary>Bieżący zrzut (bajty bez szyfrowania + ustawienia szyfrowania do zastosowania przy zapisie).</summary>
    DocumentSnapshot Current { get; }

    /// <summary>Czy są niezapisane zmiany (porównanie z wersją zapisaną, więc Cofnij do niej zeruje flagę).</summary>
    bool IsModified { get; }

    /// <summary>Informacje o zabezpieczeniach pliku źródłowego (z chwili otwarcia).</summary>
    PdfSecurityInfo SecurityInfo { get; }

    /// <summary>Efektywne uprawnienia w ZetPDF (wszystkie przy haśle właściciela lub braku szyfrowania).</summary>
    DocumentPermissions Permissions { get; }

    IUndoRedoService History { get; }

    /// <summary>
    /// Wykonuje polecenie w tle (jedno naraz na dokument – kolejne czekają), dodaje wynik do historii,
    /// podnosi <see cref="Changed"/> na wątku UI. Zwraca false, gdy polecenie nic nie zmieniło.
    /// Wyjątki polecenia przechodzą do wołającego (stan dokumentu pozostaje bez zmian).
    /// <see cref="DocumentPermissionException"/>, gdy brak wymaganego uprawnienia.
    /// </summary>
    Task<bool> ExecuteAsync(IDocumentCommand command, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Po każdej zmianie zrzutu (polecenie, Cofnij, Ponów, Przywróć). Na wątku UI.</summary>
    event EventHandler<DocumentChangedEventArgs>? Changed;

    /// <summary>Zmiana FilePath/DisplayName/IsModified (do wiązań w karcie). Na wątku UI.</summary>
    event EventHandler? StateChanged;

    /// <summary>Czy dane uprawnienie jest dostępne (skrót dla poleceń wstążki – CanExecute).</summary>
    bool Can(DocumentPermissions permission) => (Permissions & permission) == permission;
}

/// <summary>Historia Cofnij/Ponów jednego dokumentu.</summary>
public interface IUndoRedoService
{
    bool CanUndo { get; }
    bool CanRedo { get; }

    /// <summary>Klucz lokalizacji opisu operacji do cofnięcia (np. "Pages.Undo.Delete") lub null.</summary>
    string? UndoDescriptionKey { get; }
    string? RedoDescriptionKey { get; }

    /// <summary>Limit poziomów (z Ustawień, domyślnie 30). Starsze zrzuty są usuwane.</summary>
    int Limit { get; set; }

    Task UndoAsync(CancellationToken ct = default);
    Task RedoAsync(CancellationToken ct = default);

    /// <summary>Usuwa całą historię (np. po Przywróć).</summary>
    void Clear();

    event EventHandler? HistoryChanged;
}

/// <summary>
/// Operacja edycyjna. Z bieżącego zrzutu tworzy nowy. NIE modyfikuje wejścia (zrzuty są niezmienne).
/// Implementacje: zwykle <see cref="Commands.DelegateDocumentCommand"/> opakowujący metodę usługi
/// "bajty → bajty" (np. <see cref="IStampingService.AddWatermarkAsync"/>).
/// </summary>
public interface IDocumentCommand
{
    /// <summary>Klucz lokalizacji opisu (pokazywany jako "Cofnij: …").</summary>
    string DescriptionKey { get; }

    /// <summary>Uprawnienie wymagane do wykonania (sprawdzane przez sesję).</summary>
    DocumentPermissions RequiredPermission { get; }

    /// <summary>Wykonuje się na wątku puli (Task.Run) – nie dotykaj UI. Zwróć null, jeśli nic się nie zmieniło.</summary>
    Task<DocumentEditResult?> ExecuteAsync(DocumentEditContext context, CancellationToken ct);
}

/// <summary>Wejście polecenia.</summary>
public sealed record DocumentEditContext(
    IPdfDocumentSession Session,
    DocumentSnapshot Input,
    IProgress<OperationProgress>? Progress);

/// <summary>Wynik polecenia: nowy zrzut + co się zmieniło (żeby widok/panele odświeżyły tylko to, co trzeba).</summary>
public sealed record DocumentEditResult(
    DocumentSnapshot Output,
    DocumentChangeKind Kind,
    IReadOnlyList<int>? AffectedPages = null);

[Flags]
public enum DocumentChangeKind
{
    None = 0,
    /// <summary>Liczba/kolejność stron (miniatury i widok przebudowują listę stron).</summary>
    PageStructure = 1,
    /// <summary>Treść stron (render ponownie).</summary>
    PageContent = 2,
    Annotations = 4,
    FormFields = 8,
    Bookmarks = 16,
    Metadata = 32,
    Security = 64,
    Attachments = 128,
    All = 0xFFFF,
}

public sealed class DocumentChangedEventArgs : EventArgs
{
    public DocumentChangedEventArgs(DocumentChangeKind kind, IReadOnlyList<int>? affectedPages, DocumentChangeSource source)
    {
        Kind = kind;
        AffectedPages = affectedPages;
        Source = source;
    }

    public DocumentChangeKind Kind { get; }

    /// <summary>Indeksy stron (0-based) w NOWYM dokumencie lub null = nie wiadomo/wszystkie.</summary>
    public IReadOnlyList<int>? AffectedPages { get; }

    public DocumentChangeSource Source { get; }
}

public enum DocumentChangeSource { Command, Undo, Redo, Revert, Save }

/// <summary>Postęp operacji. Fraction 0..1 lub null (nieokreślony). Text – już przetłumaczony opis kroku.</summary>
public readonly record struct OperationProgress(double? Fraction, string? Text = null, int Current = 0, int Total = 0)
{
    public static OperationProgress Of(int current, int total, string? text = null) =>
        new(total > 0 ? (double)current / total : null, text, current, total);
}

// ------------------------------------------------------------------ zabezpieczenia

/// <summary>Uprawnienia dokumentu (odpowiednik bitów /P, ale jako flagi dodatnie).</summary>
[Flags]
public enum DocumentPermissions
{
    None = 0,
    Print = 1,
    PrintHighQuality = 2,
    /// <summary>Modyfikacja treści (edycja tekstu/obiektów, oznaczenia stron, redakcja…).</summary>
    ModifyContent = 4,
    CopyContent = 8,
    /// <summary>Dodawanie/zmiana komentarzy (i wypełnianie formularzy przy rev. 2).</summary>
    Annotate = 16,
    FillForms = 32,
    ExtractForAccessibility = 64,
    /// <summary>Wstawianie, usuwanie, obracanie stron, zakładki.</summary>
    AssembleDocument = 128,
    All = Print | PrintHighQuality | ModifyContent | CopyContent | Annotate | FillForms | ExtractForAccessibility | AssembleDocument,
}

public enum PdfEncryptionMethod { None, Rc4_40, Rc4_128, Aes128, Aes256, Unknown }

/// <summary>Ustawienia szyfrowania stosowane przy zapisie (część <see cref="DocumentSnapshot"/>, więc Cofnij działa).</summary>
public sealed record PdfEncryptionSettings(
    string? UserPassword,
    string OwnerPassword,
    PdfEncryptionMethod Method = PdfEncryptionMethod.Aes256,
    DocumentPermissions Permissions = DocumentPermissions.All)
{
    /// <summary>
    /// Hasło właściciela wygenerował ZetPDF (plik otwarto bez hasła właściciela – DECYZJE D-08). Przy pustym
    /// haśle otwarcia powłoka przed zapisem pyta, co zrobić z ochroną (DECYZJE D-23).
    /// </summary>
    public bool GeneratedOwnerPassword { get; init; }
}

/// <summary>Co wiadomo o zabezpieczeniach pliku w chwili otwarcia.</summary>
public sealed record PdfSecurityInfo(
    bool IsEncrypted,
    PdfEncryptionMethod Method,
    int Revision,
    DocumentPermissions FilePermissions,
    bool OpenedWithOwnerPassword,
    /// <summary>Hasło, którym otwarto (do ponownego szyfrowania przy zapisie); null gdy brak szyfrowania.</summary>
    string? Password)
{
    public static PdfSecurityInfo None { get; } = new(false, PdfEncryptionMethod.None, 0, DocumentPermissions.All, true, null);
}

// ------------------------------------------------------------------ wyjątki domenowe

/// <summary>Dokument wymaga hasła (nie podano).</summary>
public class PdfPasswordRequiredException : Exception
{
    public PdfPasswordRequiredException(string? message = null) : base(message ?? "Password required.") { }
}

/// <summary>Podane hasło jest nieprawidłowe.</summary>
public class PdfInvalidPasswordException : PdfPasswordRequiredException
{
    public PdfInvalidPasswordException(string? message = null) : base(message ?? "Invalid password.") { }
}

/// <summary>Plik uszkodzony / nie jest PDF / nieobsługiwany.</summary>
public class PdfCorruptException : Exception
{
    public PdfCorruptException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Operacja niedozwolona przez uprawnienia dokumentu.</summary>
public class DocumentPermissionException : Exception
{
    public DocumentPermissionException(DocumentPermissions required)
        : base($"Operation requires permission: {required}.") => Required = required;

    public DocumentPermissions Required { get; }
}

// ------------------------------------------------------------------ metadane (Właściwości dokumentu, Ctrl+D)

/// <summary>Metadane z /Info (XMP aktualizowany przez implementację, jeśli istnieje).</summary>
public sealed record DocumentMetadata
{
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    public string Producer { get; init; } = string.Empty;
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public int PageCount { get; init; }
    public string PdfVersion { get; init; } = string.Empty;
}

/// <summary>Odczyt/zapis metadanych. Implementacja: Core (PDFsharp).</summary>
public interface IMetadataService
{
    Task<DocumentMetadata> GetAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Zapisuje Title/Author/Subject/Keywords/Creator (pozostałe pola ignorowane; Modified = teraz).</summary>
    Task<byte[]> SetAsync(byte[] pdf, DocumentMetadata metadata, CancellationToken ct = default);
}

namespace ZetPDF.Core.Contracts;

// =====================================================================================
//  Komentarze, formularze, załączniki – implementacja: ZetPDF.Annotations (annotations-forms)
//  Konwencja "bajty → bajty" jak w Pages.cs. Adnotacje zapisywane jako standardowe adnotacje PDF
//  z wyglądem /AP (widoczne w innych czytnikach).
// =====================================================================================

public enum AnnotationKind
{
    Highlight, Underline, Squiggly, StrikeOut, AreaHighlight,
    Note, FreeText, Typewriter, Callout,
    Square, Circle, Line, Arrow, Polygon, Polyline, Ink,
    Stamp, FileAttachment, Link, Redact, Widget, Popup, Other,
}

public enum AnnotationReviewState { None, Accepted, Rejected, Cancelled, Completed }

/// <summary>
/// Stały identyfikator adnotacji w dokumencie: strona + /NM (nadawane przez ZetPDF przy tworzeniu; dla adnotacji
/// bez /NM – generowane deterministycznie z numeru obiektu "obj:{n}").
/// </summary>
public readonly record struct AnnotationId(int PageIndex, string Name);

/// <summary>Adnotacja odczytana z dokumentu.</summary>
public sealed record AnnotationInfo
{
    public required AnnotationId Id { get; init; }
    public required AnnotationKind Kind { get; init; }
    public required PdfRect Rect { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Contents { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public PdfColor? Color { get; init; }
    public double Opacity { get; init; } = 1;
    public AnnotationReviewState State { get; init; }
    /// <summary>Odpowiedź na (IRT) – null dla komentarza głównego.</summary>
    public AnnotationId? InReplyTo { get; init; }
    public bool IsHidden { get; init; }
    public bool IsLocked { get; init; }
}

/// <summary>Opis nowej adnotacji. Wypełnij pola właściwe dla Kind (pozostałe ignorowane).</summary>
public sealed record AnnotationSpec
{
    public required AnnotationKind Kind { get; init; }
    public required int PageIndex { get; init; }
    /// <summary>Prostokąt (dla adiustacji tekstu wyliczany z Quads, jeśli pusty).</summary>
    public PdfRect Rect { get; init; }
    /// <summary>Highlight/Underline/Squiggly/StrikeOut – czworokąty wierszy (z PdfTextSelection).</summary>
    public IReadOnlyList<PdfQuad>? Quads { get; init; }
    /// <summary>Line/Arrow: 2 punkty; Polygon/Polyline: wierzchołki; Callout: 2–3 punkty linii.</summary>
    public IReadOnlyList<PdfPoint>? Points { get; init; }
    /// <summary>Ink: lista kresek.</summary>
    public IReadOnlyList<IReadOnlyList<PdfPoint>>? InkStrokes { get; init; }
    public string Contents { get; init; } = string.Empty;
    public string? Author { get; init; }
    public PdfColor Color { get; init; } = PdfColor.Yellow;
    public PdfColor? FillColor { get; init; }
    public double Opacity { get; init; } = 1;
    public double LineWidth { get; init; } = 1;
    public TextStyle? TextStyle { get; init; }
    /// <summary>Note: nazwa ikony (Comment, Note, Help, Insert, Key, NewParagraph, Paragraph).</summary>
    public string? IconName { get; init; }
    /// <summary>Stamp standardowy: identyfikator wzoru (np. "Approved", "Confidential", "Draft"); tekst wg języka.</summary>
    public string? StampName { get; init; }
    /// <summary>Stamp z obrazu / podpis odręczny jako obraz (PNG/JPG).</summary>
    public byte[]? ImageData { get; init; }
    /// <summary>FileAttachment: plik do osadzenia.</summary>
    public string? AttachmentPath { get; init; }
}

/// <summary>Zmiana istniejącej adnotacji (null = bez zmian).</summary>
public sealed record AnnotationUpdate
{
    public PdfRect? Rect { get; init; }
    public string? Contents { get; init; }
    public PdfColor? Color { get; init; }
    public double? Opacity { get; init; }
    public double? LineWidth { get; init; }
    public AnnotationReviewState? State { get; init; }
    public bool? IsHidden { get; init; }
    /// <summary>
    /// FreeText (pole tekstowe, maszyna do pisania, objaśnienie): rodzina i rozmiar czcionki oraz kolor tekstu
    /// (ramka bez zmian; Bold/Italic nieobsługiwane). Dla innych rodzajów ignorowane. Stosowane po <see cref="Color"/>.
    /// </summary>
    public TextStyle? TextStyle { get; init; }
}

public interface IAnnotationService
{
    Task<IReadOnlyList<AnnotationInfo>> GetAnnotationsAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default);

    /// <summary>Dodaje adnotację; zwraca nowe bajty i id.</summary>
    Task<(byte[] Pdf, AnnotationId Id)> AddAsync(byte[] pdf, AnnotationSpec spec, CancellationToken ct = default);
    Task<byte[]> UpdateAsync(byte[] pdf, AnnotationId id, AnnotationUpdate update, CancellationToken ct = default);
    Task<byte[]> DeleteAsync(byte[] pdf, IReadOnlyList<AnnotationId> ids, CancellationToken ct = default);
    /// <summary>Usuwa wszystkie komentarze (bez Link i Widget).</summary>
    Task<byte[]> DeleteAllAsync(byte[] pdf, CancellationToken ct = default);
    Task<(byte[] Pdf, AnnotationId Id)> AddReplyAsync(byte[] pdf, AnnotationId parent, string text, string author, CancellationToken ct = default);

    /// <summary>Eksport komentarzy do XFDF (zgodny z Adobe XFDF 3.0).</summary>
    Task ExportXfdfAsync(byte[] pdf, Stream output, string? sourceFileName, CancellationToken ct = default);
    /// <summary>Import XFDF lub FDF (rozpoznanie po treści), z deduplikacją po /NM.</summary>
    Task<byte[]> ImportCommentsAsync(byte[] pdf, Stream input, CancellationToken ct = default);
}

// ------------------------------------------------------------------ formularze

public enum FormFieldKind { Text, CheckBox, RadioButton, ComboBox, ListBox, PushButton, Signature, Date }

public sealed record FormFieldInfo
{
    public required string FullName { get; init; }
    public required FormFieldKind Kind { get; init; }
    public required int PageIndex { get; init; }
    public required PdfRect Rect { get; init; }
    public string Value { get; init; } = string.Empty;
    public string DefaultValue { get; init; } = string.Empty;
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    /// <summary>Wartości eksportu (pole wyboru/opcja).</summary>
    public IReadOnlyList<string> ExportValues { get; init; } = Array.Empty<string>();
    public string Tooltip { get; init; } = string.Empty;
    public bool Required { get; init; }
    public bool ReadOnly { get; init; }
    public bool Multiline { get; init; }
    /// <summary>Pole podpisu: czy podpisane.</summary>
    public bool IsSigned { get; init; }
    public int TabOrder { get; init; }
}

public sealed record FormFieldSpec
{
    public required FormFieldKind Kind { get; init; }
    public required string Name { get; init; }
    public required int PageIndex { get; init; }
    public required PdfRect Rect { get; init; }
    public string DefaultValue { get; init; } = string.Empty;
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    /// <summary>RadioButton: wartość eksportu tej opcji w grupie Name.</summary>
    public string? ExportValue { get; init; }
    public string Tooltip { get; init; } = string.Empty;
    public bool Required { get; init; }
    public bool ReadOnly { get; init; }
    public bool Multiline { get; init; }
    public TextStyle Style { get; init; } = new() { FontSize = 0 }; // 0 = auto
    /// <summary>Date: format daty, np. "yyyy-MM-dd".</summary>
    public string? DateFormat { get; init; }
    /// <summary>PushButton: akcja (bez JavaScript).</summary>
    public FormButtonAction ButtonAction { get; init; }
    public string? ButtonUrl { get; init; }
    public string? ButtonCaption { get; init; }
}

public enum FormButtonAction { None, ResetForm, OpenUrl }

public enum FormDataFormat { Fdf, Xfdf, Csv }

public interface IFormService
{
    Task<IReadOnlyList<FormFieldInfo>> GetFieldsAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Ustawia wartości (nazwa pełna → wartość; pole wyboru: wartość eksportu lub "Off"). Regeneruje wygląd.</summary>
    Task<byte[]> SetValuesAsync(byte[] pdf, IReadOnlyDictionary<string, string> values, CancellationToken ct = default);
    Task<byte[]> ResetAsync(byte[] pdf, IReadOnlyList<string>? fieldNames = null, CancellationToken ct = default);

    Task<byte[]> CreateFieldAsync(byte[] pdf, FormFieldSpec spec, CancellationToken ct = default);
    Task<byte[]> UpdateFieldAsync(byte[] pdf, string fullName, FormFieldSpec spec, CancellationToken ct = default);
    Task<byte[]> DeleteFieldsAsync(byte[] pdf, IReadOnlyList<string> fullNames, CancellationToken ct = default);
    Task<byte[]> SetTabOrderAsync(byte[] pdf, int pageIndex, IReadOnlyList<string> orderedFieldNames, CancellationToken ct = default);

    Task ExportDataAsync(byte[] pdf, Stream output, FormDataFormat format, CancellationToken ct = default);
    Task<byte[]> ImportDataAsync(byte[] pdf, Stream input, FormDataFormat format, CancellationToken ct = default);
}

// ------------------------------------------------------------------ załączniki

public sealed record AttachmentInfo(string Name, string Description, long Size, DateTimeOffset? Modified, int? PageIndex);

public interface IAttachmentService
{
    /// <summary>Pliki osadzone (EmbeddedFiles) i adnotacje FileAttachment (PageIndex != null).</summary>
    Task<IReadOnlyList<AttachmentInfo>> GetAttachmentsAsync(byte[] pdf, CancellationToken ct = default);
    Task<byte[]> ExtractAsync(byte[] pdf, string name, CancellationToken ct = default);
    Task<byte[]> AddAsync(byte[] pdf, string filePath, string description, CancellationToken ct = default);
    Task<byte[]> RemoveAsync(byte[] pdf, string name, CancellationToken ct = default);
}

using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Pdf;

/// <summary>Wzór stempla standardowego: identyfikator (/Name), klucz tekstu (pl/en w zasobach), kolor.</summary>
internal sealed record StampDefinition(string Id, string TextKey, PdfColor Color);

/// <summary>Własne wzory stempli ZetPDF (nie kopie stempli Adobe/Foxit) – tekst wg języka interfejsu, data i autor.</summary>
internal static class StampLibrary
{
    private static readonly PdfColor Green = new(255, 22, 128, 54);
    private static readonly PdfColor Red = new(255, 196, 30, 36);
    private static readonly PdfColor Blue = new(255, 30, 78, 160);

    public static IReadOnlyList<StampDefinition> Standard { get; } = new[]
    {
        new StampDefinition("Approved", "Annot.Stamp.Approved", Green),
        new StampDefinition("Rejected", "Annot.Stamp.Rejected", Red),
        new StampDefinition("Confidential", "Annot.Stamp.Confidential", Red),
        new StampDefinition("Urgent", "Annot.Stamp.Urgent", Red),
        new StampDefinition("Draft", "Annot.Stamp.Draft", Blue),
        new StampDefinition("Final", "Annot.Stamp.Final", Green),
        new StampDefinition("ForInformation", "Annot.Stamp.ForInformation", Blue),
        new StampDefinition("ForComment", "Annot.Stamp.ForComment", Blue),
        new StampDefinition("Completed", "Annot.Stamp.Completed", Green),
    };

    /// <summary>Znaczniki Wypełnij i podpisz rysowane wektorowo jako /Stamp.</summary>
    public const string Check = "ZetCheck";
    public const string Cross = "ZetCross";
    public const string Dot = "ZetDot";

    public static bool IsFillSignMark(string? name) => name is Check or Cross or Dot;

    public static StampDefinition? Find(string? id) =>
        id is null ? null : Standard.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}

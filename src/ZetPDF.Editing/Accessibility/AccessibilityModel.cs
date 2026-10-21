namespace ZetPDF.Editing.Accessibility;

/// <summary>Wynik reguły: Zaliczone / Niezaliczone / Do sprawdzenia ręcznego.</summary>
public enum AccessCheckStatus { Passed, Failed, Manual }

/// <summary>Reguły podstawowego sprawdzenia ułatwień dostępu (podzbiór PDF/UA i reguł Foxit/Acrobat).</summary>
public enum AccessRule
{
    DocTitle,
    DisplayDocTitle,
    Language,
    Tagged,
    StructureTree,
    FigureAltText,
    TabOrder,
    FieldDescriptions,
    TextPresence,
    Bookmarks,
    ReadingOrder,
}

/// <summary>
/// Wynik jednej reguły. Tekst szczegółów: klucz zasobu <see cref="DetailKey"/> z argumentami <see cref="DetailArgs"/>.
/// <see cref="Pages"/> – strony, których dotyczy problem (0-based).
/// </summary>
public sealed record AccessCheckResult(AccessRule Rule, AccessCheckStatus Status, string DetailKey, IReadOnlyList<object> DetailArgs, bool CanFix, IReadOnlyList<int> Pages)
{
    public string NameKey => "Edit.Access.Rule." + Rule;
    public string ExplainKey => "Edit.Access.Explain." + Rule;
}

public sealed record AccessReport(IReadOnlyList<AccessCheckResult> Results, int PageCount, bool IsTagged)
{
    public int Passed => Results.Count(r => r.Status == AccessCheckStatus.Passed);
    public int Failed => Results.Count(r => r.Status == AccessCheckStatus.Failed);
    public int Manual => Results.Count(r => r.Status == AccessCheckStatus.Manual);
    public AccessCheckResult this[AccessRule rule] => Results.First(r => r.Rule == rule);
}

/// <summary>Wartości dla napraw wymagających danych (tytuł, język). Puste = propozycja automatyczna.</summary>
public sealed record AccessFixOptions
{
    public string? Title { get; init; }
    public string? Language { get; init; }
}

/// <summary>Element struktury /Figure (kolejność w drzewie struktury), strona i tekst alternatywny.</summary>
public sealed record AccessFigure(int Index, int PageIndex, string? Alt, string? ActualText);

/// <summary>Właściwości ułatwień dostępu ustawiane w oknie „Tytuł, język i tekst alternatywny”. null = bez zmian.</summary>
public sealed record AccessProperties
{
    public string? Title { get; init; }
    public string? Language { get; init; }
    public bool? DisplayDocTitle { get; init; }
    /// <summary>Tekst alternatywny wg <see cref="AccessFigure.Index"/> (pusty tekst usuwa /Alt).</summary>
    public IReadOnlyDictionary<int, string>? AltTexts { get; init; }
}

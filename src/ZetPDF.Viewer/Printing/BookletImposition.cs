namespace ZetPDF.Viewer.Printing;

/// <summary>
/// Kolejność składki broszury: arkusz złożony na pół, 2 strony na stronie arkusza. Liczba stron dopełniona pustymi do wielokrotności 4;
/// puste trafiają na koniec, przed ostatnią okładką nic się nie przesuwa. Dla 8 stron: (8,1), (2,7), (6,3), (4,5).
/// </summary>
public static class BookletImposition
{
    /// <summary>Znacznik pustej strony w wyniku.</summary>
    public const int Blank = -1;

    /// <summary>Liczba stron po dopełnieniu do wielokrotności 4.</summary>
    public static int PaddedCount(int pages) => pages <= 0 ? 0 : (pages + 3) / 4 * 4;

    /// <summary>
    /// Strony arkusza kolejno: lewa i prawa strona każdej strony arkusza, numery od 1, <see cref="Blank"/> dla pustej.
    /// Elementy nieparzyste (0, 2, …) to przód arkusza, pozostałe to tył.
    /// </summary>
    public static IReadOnlyList<(int Left, int Right)> Sides(int pages)
    {
        int padded = PaddedCount(pages);
        var sides = new List<(int, int)>(padded / 2);
        int Page(int number) => number <= pages ? number : Blank;
        for (int k = 0; k < padded / 2; k++)
            sides.Add(k % 2 == 0 ? (Page(padded - k), Page(k + 1)) : (Page(k + 1), Page(padded - k)));
        return sides;
    }

    /// <summary>Strony stron arkusza jako indeksy z listy wybranych stron dokumentu; pusta to <see cref="Blank"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Map(IReadOnlyList<int> selectedPages) =>
        Sides(selectedPages.Count)
            .Select(s => (IReadOnlyList<int>)new[] { Pick(selectedPages, s.Left), Pick(selectedPages, s.Right) })
            .ToList();

    private static int Pick(IReadOnlyList<int> pages, int number) => number == Blank ? Blank : pages[number - 1];
}

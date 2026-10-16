using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.Bookmarks;

/// <summary>
/// Operacje na niezmiennym drzewie zakładek (<see cref="BookmarkNode"/>) adresowanym ścieżkami indeksów
/// (np. [1, 0] = pierwsze dziecko drugiej zakładki najwyższego poziomu). Czysta logika – testy jednostkowe.
/// Zapis do dokumentu: <see cref="CommitAsync"/> (polecenie z Cofnij/Ponów, <see cref="IBookmarkService"/>).
/// </summary>
public static class BookmarkTree
{
    public static BookmarkNode Get(IReadOnlyList<BookmarkNode> tree, IReadOnlyList<int> path)
    {
        if (path.Count == 0) throw new ArgumentException("Empty path.", nameof(path));
        var list = tree;
        BookmarkNode node = null!;
        foreach (int i in path)
        {
            node = list[i];
            list = node.Children;
        }
        return node;
    }

    /// <summary>Wstawia węzeł pod rodzicem (pusta ścieżka = najwyższy poziom) na pozycji index.</summary>
    public static IReadOnlyList<BookmarkNode> Insert(IReadOnlyList<BookmarkNode> tree, IReadOnlyList<int> parentPath, int index, BookmarkNode node)
    {
        if (parentPath.Count == 0)
        {
            var l = tree.ToList();
            l.Insert(Math.Clamp(index, 0, l.Count), node);
            return l;
        }
        return Update(tree, parentPath, p =>
        {
            var c = p.Children.ToList();
            c.Insert(Math.Clamp(index, 0, c.Count), node);
            return p with { Children = c, IsOpen = true };
        });
    }

    public static IReadOnlyList<BookmarkNode> Remove(IReadOnlyList<BookmarkNode> tree, IReadOnlyList<int> path)
    {
        if (path.Count == 1)
        {
            var l = tree.ToList();
            l.RemoveAt(path[0]);
            return l;
        }
        return Update(tree, path.Take(path.Count - 1).ToArray(), p =>
        {
            var c = p.Children.ToList();
            c.RemoveAt(path[^1]);
            return p with { Children = c };
        });
    }

    public static IReadOnlyList<BookmarkNode> Replace(IReadOnlyList<BookmarkNode> tree, IReadOnlyList<int> path, Func<BookmarkNode, BookmarkNode> change) =>
        Update(tree, path, change);

    public static IReadOnlyList<BookmarkNode> Rename(IReadOnlyList<BookmarkNode> tree, IReadOnlyList<int> path, string title) =>
        Update(tree, path, n => n with { Title = title });

    /// <summary>Czy <paramref name="candidate"/> leży w poddrzewie <paramref name="ancestor"/> (lub jest nim).</summary>
    public static bool IsSameOrDescendant(IReadOnlyList<int> ancestor, IReadOnlyList<int> candidate) =>
        candidate.Count >= ancestor.Count && ancestor.SequenceEqual(candidate.Take(ancestor.Count));

    /// <summary>
    /// Przenosi węzeł pod nowego rodzica na pozycję index (liczoną PRZED usunięciem źródła).
    /// Zwraca null, gdy ruch jest niedozwolony (do własnego poddrzewa). Zwraca też ścieżkę węzła po przeniesieniu.
    /// </summary>
    public static (IReadOnlyList<BookmarkNode> Tree, IReadOnlyList<int> NewPath)? Move(IReadOnlyList<BookmarkNode> tree, IReadOnlyList<int> from, IReadOnlyList<int> toParent, int index)
    {
        if (IsSameOrDescendant(from, toParent)) return null;
        var node = Get(tree, from);
        var fromParent = from.Take(from.Count - 1).ToArray();
        // korekta ścieżki rodzica docelowego i indeksu po usunięciu źródła
        var parent = toParent.ToArray();
        if (parent.Length > fromParent.Length && fromParent.SequenceEqual(parent.Take(fromParent.Length)) && parent[fromParent.Length] > from[^1])
            parent[fromParent.Length]--;
        if (fromParent.SequenceEqual(toParent) && index > from[^1]) index--;
        var without = Remove(tree, from);
        var result = Insert(without, parent, index, node);
        var siblings = parent.Length == 0 ? result : Get(result, parent).Children;
        int finalIndex = Math.Clamp(index, 0, siblings.Count - 1);
        return (result, parent.Append(finalIndex).ToArray());
    }

    private static IReadOnlyList<BookmarkNode> Update(IReadOnlyList<BookmarkNode> list, IReadOnlyList<int> path, Func<BookmarkNode, BookmarkNode> change)
    {
        var copy = list.ToList();
        int i = path[0];
        copy[i] = path.Count == 1 ? change(copy[i]) : copy[i] with { Children = Update(copy[i].Children, path.Skip(1).ToArray(), change) };
        return copy;
    }

    /// <summary>Zapisuje drzewo jako polecenie dokumentu (Cofnij/Ponów). Zwraca false, gdy nic się nie zmieniło.</summary>
    public static Task<bool> CommitAsync(IPdfDocumentSession session, IBookmarkService service, IReadOnlyList<BookmarkNode> tree, string undoKey)
    {
        var cmd = new DelegateDocumentCommand(undoKey, (pdf, _, ct) => service.SetBookmarksAsync(pdf, tree, ct), DocumentChangeKind.Bookmarks, DocumentPermissions.AssembleDocument);
        return session.ExecuteAsync(cmd);
    }
}

using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ZetPDF.Pages.Engine;

internal sealed record OutlineItem(string Title, int PageIndex, bool Open, IReadOnlyList<OutlineItem> Children);

/// <summary>Odczyt drzewa zakładek ze słowników (niezależnie od trybu otwarcia) i zapis do nowego dokumentu.</summary>
internal static class Outlines
{
    public static IReadOnlyList<OutlineItem> Read(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        if (PdfIo.GetDict(catalog, "/Outlines") is not { } root) return Array.Empty<OutlineItem>();
        var pageMap = PdfIo.PageIndexMap(doc);
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        return ReadLevel(doc, PdfIo.GetDict(root, "/First"), pageMap, visited, 0);
    }

    private static List<OutlineItem> ReadLevel(PdfDocument doc, PdfDictionary? item, Dictionary<PdfObjectID, int> pageMap, HashSet<PdfDictionary> visited, int depth)
    {
        var list = new List<OutlineItem>();
        while (item is not null && depth < 32 && visited.Add(item))
        {
            var title = PdfIo.GetText(item.Elements["/Title"]) ?? string.Empty;
            int page = ResolvePage(doc, item, pageMap);
            var count = PdfIo.GetNumber(item.Elements["/Count"]) ?? 0;
            var children = ReadLevel(doc, PdfIo.GetDict(item, "/First"), pageMap, visited, depth + 1);
            list.Add(new OutlineItem(title, page, count > 0, children));
            item = PdfIo.GetDict(item, "/Next");
        }
        return list;
    }

    /// <summary>Indeks strony celu zakładki/łącza (/Dest lub /A GoTo, także nazwane cele) albo -1.</summary>
    public static int ResolvePage(PdfDocument doc, PdfDictionary item, Dictionary<PdfObjectID, int> pageMap)
    {
        var dest = PdfIo.Resolve(item.Elements["/Dest"]);
        if (dest is null && PdfIo.GetDict(item, "/A") is { } action && PdfIo.GetName(action, "/S") == "/GoTo")
            dest = PdfIo.Resolve(action.Elements["/D"]);
        return DestPage(doc, dest, pageMap, 0);
    }

    private static int DestPage(PdfDocument doc, PdfItem? dest, Dictionary<PdfObjectID, int> pageMap, int depth)
    {
        if (depth > 4 || dest is null) return -1;
        switch (dest)
        {
            case PdfArray arr when arr.Elements.Count > 0:
                return arr.Elements[0] is PdfReference r && pageMap.TryGetValue(r.ObjectID, out var idx) ? idx : -1;
            case PdfDictionary d:
                return DestPage(doc, PdfIo.Resolve(d.Elements["/D"]), pageMap, depth + 1);
            case PdfName n:
                return DestPage(doc, LookupNamed(doc, n.Value.TrimStart('/')), pageMap, depth + 1);
            case PdfString s:
                return DestPage(doc, LookupNamed(doc, s.Value), pageMap, depth + 1);
        }
        return -1;
    }

    private static PdfItem? LookupNamed(PdfDocument doc, string name)
    {
        var catalog = doc.Internals.Catalog;
        if (PdfIo.GetDict(catalog, "/Dests") is { } dests && dests.Elements.ContainsKey("/" + name))
            return PdfIo.Resolve(dests.Elements["/" + name]);
        if (PdfIo.GetDict(catalog, "/Names") is { } names && PdfIo.GetDict(names, "/Dests") is { } tree)
            return FindInNameTree(tree, name, 0);
        return null;
    }

    public static PdfItem? FindInNameTree(PdfDictionary node, string key, int depth)
    {
        if (depth > 32) return null;
        if (PdfIo.GetArray(node, "/Names") is { } pairs)
            for (int i = 0; i + 1 < pairs.Elements.Count; i += 2)
                if (PdfIo.GetText(pairs.Elements[i]) == key) return PdfIo.Resolve(pairs.Elements[i + 1]);
        if (PdfIo.GetArray(node, "/Kids") is { } kids)
            foreach (var k in kids.Elements)
                if (PdfIo.Resolve(k) is PdfDictionary kd && FindInNameTree(kd, key, depth + 1) is { } found) return found;
        return null;
    }

    /// <summary>Dopisuje zakładki do kolekcji; pageOf zwraca stronę docelową (null = pomiń cel, ale zachowaj dzieci).</summary>
    public static void Write(PdfOutlineCollection target, IEnumerable<OutlineItem> items, Func<int, PdfPage?> pageOf)
    {
        foreach (var item in items)
        {
            var page = item.PageIndex >= 0 ? pageOf(item.PageIndex) : null;
            if (page is null)
            {
                // cel spoza zakresu: dzieci przenosimy poziom wyżej
                Write(target, item.Children, pageOf);
                continue;
            }
            var o = target.Add(string.IsNullOrEmpty(item.Title) ? " " : item.Title, page, item.Open);
            Write(o.Outlines, item.Children, pageOf);
        }
    }
}

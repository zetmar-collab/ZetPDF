using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ZetPDF.Pages.Engine;

/// <summary>
/// Porządkowanie odwołań po usunięciu/zastąpieniu stron: cele (/Dest, /D akcji GoTo, nazwane cele, /OpenAction)
/// wskazujące usuniętą stronę są przekierowywane na stronę zastępczą, a pola formularza z widżetami
/// na usuniętych stronach – usuwane z /AcroForm. Dzięki temu usunięte strony nie trafiają do pliku jako sieroty.
/// </summary>
internal static class References
{
    private static readonly HashSet<string> DestTypes = new(StringComparer.Ordinal) { "/XYZ", "/Fit", "/FitH", "/FitV", "/FitR", "/FitB", "/FitBH", "/FitBV" };

    /// <summary>Mapa: usunięta strona → strona zastępcza (pierwsza pozostała za nią, inaczej ostatnia przed nią).</summary>
    public static Dictionary<PdfObjectID, PdfReference> ReplacementMap(PdfDocument doc, ISet<int> removedIndices)
    {
        var map = new Dictionary<PdfObjectID, PdfReference>();
        int n = doc.PageCount;
        for (int i = 0; i < n; i++)
        {
            if (!removedIndices.Contains(i) || doc.Pages[i].Reference is not { } r) continue;
            PdfReference? repl = null;
            for (int j = i + 1; j < n && repl is null; j++) if (!removedIndices.Contains(j)) repl = doc.Pages[j].Reference;
            for (int j = i - 1; j >= 0 && repl is null; j--) if (!removedIndices.Contains(j)) repl = doc.Pages[j].Reference;
            if (repl is not null) map[r.ObjectID] = repl;
        }
        return map;
    }

    /// <summary>Przekierowuje cele wskazujące strony z mapy.</summary>
    public static int Redirect(PdfDocument doc, IReadOnlyDictionary<PdfObjectID, PdfReference> map)
    {
        if (map.Count == 0) return 0;
        int changed = 0;
        var visited = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        foreach (var obj in doc.Internals.GetAllObjects()) changed += Visit(obj, map, visited, 0);
        if (doc.Internals.Catalog is { } cat) changed += Visit(cat, map, visited, 0);
        return changed;
    }

    private static int Visit(PdfItem item, IReadOnlyDictionary<PdfObjectID, PdfReference> map, HashSet<PdfItem> visited, int depth)
    {
        if (depth > 64 || !visited.Add(item)) return 0;
        int changed = 0;
        switch (item)
        {
            case PdfArray arr:
                if (arr.Elements.Count >= 2 && arr.Elements[0] is PdfReference r && map.TryGetValue(r.ObjectID, out var repl)
                    && arr.Elements[1] is PdfName n && DestTypes.Contains(n.Value))
                {
                    arr.Elements[0] = repl;
                    changed++;
                }
                foreach (var e in arr.Elements)
                    if (e is PdfArray or PdfDictionary) changed += Visit(e, map, visited, depth + 1);
                break;
            case PdfDictionary dict:
                foreach (var key in dict.Elements.Keys.ToArray())
                {
                    if (key is "/Parent" or "/P" or "/Kids" or "/Pages") continue;
                    var e = dict.Elements[key];
                    if (e is PdfArray or PdfDictionary) changed += Visit(e, map, visited, depth + 1);
                }
                break;
        }
        return changed;
    }

    /// <summary>Usuwa z /AcroForm pola, których widżety leżały na usuniętych stronach.</summary>
    public static void RemoveFields(PdfDocument doc, ISet<PdfObjectID> widgetIds, ISet<PdfObjectID> removedPageIds)
    {
        if (widgetIds.Count == 0) return;
        if (PdfIo.GetDict(doc.Internals.Catalog, "/AcroForm") is not { } acro) return;
        if (PdfIo.GetArray(acro, "/Fields") is not { } fields) return;
        Prune(fields, widgetIds, removedPageIds, 0);
    }

    /// <summary>Zwraca true, gdy tablica po przycięciu jest pusta.</summary>
    private static bool Prune(PdfArray arr, ISet<PdfObjectID> widgetIds, ISet<PdfObjectID> removedPageIds, int depth)
    {
        if (depth > 32) return false;
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            var e = arr.Elements[i];
            var d = PdfIo.Resolve(e) as PdfDictionary;
            if (d is null) continue;
            bool remove = false;
            if (e is PdfReference r && widgetIds.Contains(r.ObjectID)) remove = true;
            else if (d.Elements["/P"] is PdfReference p && removedPageIds.Contains(p.ObjectID) && PdfIo.GetName(d, "/Subtype") == "/Widget") remove = true;
            else if (PdfIo.GetArray(d, "/Kids") is { } kids && kids.Elements.Count > 0)
                remove = Prune(kids, widgetIds, removedPageIds, depth + 1);
            if (remove) arr.Elements.RemoveAt(i);
        }
        return arr.Elements.Count == 0;
    }

    /// <summary>Przekierowanie z jednej strony na drugą (Zastąp).</summary>
    public static void RedirectTo(PdfDocument doc, PdfObjectID from, PdfReference to) =>
        Redirect(doc, new Dictionary<PdfObjectID, PdfReference> { [from] = to });
}

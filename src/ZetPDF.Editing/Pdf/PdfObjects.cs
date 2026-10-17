using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Pdf;

/// <summary>Niskopoziomowe pomocniki PDFsharp: rozwiązywanie referencji, liczby, adnotacje strony.</summary>
internal static class PdfObjects
{
    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static double Number(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        PdfNumber n => double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0,
        _ => 0,
    };

    public static PdfRect? Rect(PdfItem? item)
    {
        if (Resolve(item) is not PdfArray a || a.Elements.Count < 4) return null;
        double x1 = Number(a.Elements[0]), y1 = Number(a.Elements[1]), x2 = Number(a.Elements[2]), y2 = Number(a.Elements[3]);
        return new PdfRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    public static PdfArray RectArray(PdfDocument doc, PdfRect r) =>
        new(doc, new PdfReal(r.Left), new PdfReal(r.Bottom), new PdfReal(r.Right), new PdfReal(r.Top));

    public static PdfArray Numbers(PdfDocument doc, IEnumerable<double> values) =>
        new(doc, values.Select(v => (PdfItem)new PdfReal(Math.Round(v, 4))).ToArray());

    /// <summary>Słowniki adnotacji strony (w kolejności /Annots).</summary>
    public static List<PdfDictionary> Annotations(PdfPage page)
    {
        var list = new List<PdfDictionary>();
        if (Resolve(page.Elements["/Annots"]) is PdfArray arr)
            foreach (var e in arr.Elements)
                if (Resolve(e) is PdfDictionary d) list.Add(d);
        return list;
    }

    public static string Subtype(PdfDictionary annot) => annot.Elements.GetName("/Subtype");

    /// <summary>Dodaje adnotację (obiekt pośredni) do /Annots strony.</summary>
    public static void AddAnnotation(PdfPage page, PdfDictionary annot)
    {
        var doc = page.Owner;
        if (annot.Reference is null) doc.Internals.AddObject(annot);
        annot.Elements["/P"] = page.Reference!;
        var arr = Resolve(page.Elements["/Annots"]) as PdfArray;
        if (arr is null)
        {
            arr = new PdfArray(doc);
            page.Elements["/Annots"] = arr;
        }
        arr.Elements.Add(annot.Reference!);
    }

    /// <summary>Usuwa wskazane adnotacje ze strony (i pola formularza, jeśli to widżety).</summary>
    public static int RemoveAnnotations(PdfPage page, Func<PdfDictionary, bool> predicate)
    {
        if (Resolve(page.Elements["/Annots"]) is not PdfArray arr) return 0;
        var removed = new List<PdfDictionary>();
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            if (Resolve(arr.Elements[i]) is PdfDictionary d && predicate(d))
            {
                arr.Elements.RemoveAt(i);
                removed.Add(d);
            }
        }
        foreach (var w in removed.Where(d => Subtype(d) == "/Widget")) RemoveField(page.Owner, w);
        return removed.Count;
    }

    private static void RemoveField(PdfDocument doc, PdfDictionary widget)
    {
        if (Resolve(doc.Internals.Catalog.Elements["/AcroForm"]) is not PdfDictionary acro) return;
        if (Resolve(acro.Elements["/Fields"]) is not PdfArray fields) return;
        // Widżet może być samym polem albo dzieckiem pola (/Parent).
        var parent = Resolve(widget.Elements["/Parent"]) as PdfDictionary;
        if (parent is not null && Resolve(parent.Elements["/Kids"]) is PdfArray kids)
        {
            for (int i = kids.Elements.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Resolve(kids.Elements[i]), widget)) kids.Elements.RemoveAt(i);
            if (kids.Elements.Count > 0) return;
            widget = parent;
        }
        for (int i = fields.Elements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Resolve(fields.Elements[i]), widget)) fields.Elements.RemoveAt(i);
    }

    public static int PageIndexOf(PdfDocument doc, PdfItem? pageRef)
    {
        var target = Resolve(pageRef);
        for (int i = 0; i < doc.PageCount; i++)
            if (ReferenceEquals(doc.Pages[i], target) || ReferenceEquals(doc.Pages[i].Reference, pageRef)) return i;
        if (target is PdfInteger n && n.Value >= 0 && n.Value < doc.PageCount) return n.Value;
        return -1;
    }
}

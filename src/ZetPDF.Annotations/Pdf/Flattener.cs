using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Spłaszczanie: wygląd /AP /N adnotacji (i pól formularza) rysowany jako XObject w treści strony, adnotacja usuwana.
/// Macierz wg PDF 32000-1 §12.5.5: BBox przekształcony przez /Matrix dopasowany do /Rect.
/// </summary>
internal static class Flattener
{
    public static int Flatten(PdfDocument doc, bool includeFormFields, IReadOnlyCollection<AnnotationId>? onlyIds)
    {
        var ids = onlyIds is null ? null : new HashSet<string>(onlyIds.Select(i => i.Name), StringComparer.Ordinal);
        var flattenedDicts = new List<PdfDictionary>();
        int count = 0;
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            var content = new StringBuilder();
            int n = 0;
            foreach (var a in AnnotationStore.All(doc, p).ToList())
            {
                var d = a.Dict;
                var kind = AnnotModel.KindOf(d);
                if (kind is AnnotationKind.Popup or AnnotationKind.Link) continue;
                if (kind == AnnotationKind.Widget ? !includeFormFields : !AnnotModel.IsComment(kind)) continue;
                if (ids is not null && !ids.Contains(a.Id.Name)) continue;
                if (AnnotationStore.IsReply(d) || AnnotationStore.IsStateAnnot(d)) continue;
                int flags = Int(d, "/F");
                flattenedDicts.Add(d);
                if ((flags & AnnotationStore.FlagHidden) != 0) continue;
                var form = NormalAppearance(d);
                var rect = PdfLow.Rect(d);
                if (form is null || rect is not { } r || r.IsEmpty) continue;
                var matrix = PlacementMatrix(form, r);
                if (matrix is null) continue;
                string name = $"ZetFlat{p}_{n++}_{Guid.NewGuid().ToString("N")[..6]}";
                AddXObject(doc, page, name, form);
                var mtx = matrix.Value;
                content.Append($"q {Num(mtx.A)} {Num(mtx.B)} {Num(mtx.C)} {Num(mtx.D)} {Num(mtx.E)} {Num(mtx.F)} cm /{name} Do Q\n");
                count++;
            }
            if (content.Length > 0) AppendContent(doc, page, content.ToString());
        }
        AnnotationStore.Remove(doc, flattenedDicts);
        if (includeFormFields) RemoveFlattenedFields(doc, flattenedDicts);
        return count;
    }

    /// <summary>Strumień wyglądu normalnego (z uwzględnieniem stanu /AS dla pól wyboru).</summary>
    public static PdfDictionary? NormalAppearance(PdfDictionary annot)
    {
        var ap = Dict(annot, "/AP");
        if (ap is null) return null;
        var n = Get(ap, "/N");
        if (n is PdfDictionary nd && nd.Stream is not null) return nd;
        if (n is PdfDictionary states)
        {
            var asName = NameOf(annot, "/AS");
            if (asName is not null && Get(states, asName) is PdfDictionary s && s.Stream is not null) return s;
        }
        return null;
    }

    /// <summary>Macierz rysująca formę tak, by jej (przekształcony) BBox wypełnił prostokąt.</summary>
    public static PdfMatrix? PlacementMatrix(PdfDictionary form, PdfRect rect)
    {
        var bb = Numbers(ArrayOf(form, "/BBox"));
        if (bb.Length < 4) return null;
        var mm = Numbers(ArrayOf(form, "/Matrix"));
        var fm = mm.Length == 6 ? new PdfMatrix(mm[0], mm[1], mm[2], mm[3], mm[4], mm[5]) : PdfMatrix.Identity;
        var pts = new[] { new PdfPoint(bb[0], bb[1]), new PdfPoint(bb[2], bb[1]), new PdfPoint(bb[0], bb[3]), new PdfPoint(bb[2], bb[3]) }.Select(fm.Transform).ToArray();
        double l = pts.Min(p => p.X), b = pts.Min(p => p.Y), r = pts.Max(p => p.X), t = pts.Max(p => p.Y);
        if (r - l < 1e-6 || t - b < 1e-6) return null;
        double sx = rect.Width / (r - l), sy = rect.Height / (t - b);
        var a = new PdfMatrix(sx, 0, 0, sy, rect.Left - l * sx, rect.Bottom - b * sy);
        return fm.Then(a);
    }

    private static void AddXObject(PdfDocument doc, PdfPage page, string name, PdfDictionary form)
    {
        var res = Dict(page, "/Resources");
        if (res is null)
        {
            res = NewDict(doc);
            page.Elements["/Resources"] = res;
        }
        var xo = Dict(res, "/XObject");
        if (xo is null)
        {
            xo = NewDict(doc);
            res.Elements["/XObject"] = xo;
        }
        if (form.Reference is null) doc.Internals.AddObject(form);
        xo.Elements["/" + name] = form.Reference!;
    }

    /// <summary>Dokłada treść na końcu strony; istniejącą treść otacza q/Q (izolacja stanu graficznego).</summary>
    public static void AppendContent(PdfDocument doc, PdfPage page, string ops)
    {
        var pre = NewDict(doc);
        pre.CreateStream(Encoding.ASCII.GetBytes("q\n"));
        doc.Internals.AddObject(pre);
        var post = NewDict(doc);
        post.CreateStream(Encoding.ASCII.GetBytes("\nQ\n" + ops));
        doc.Internals.AddObject(post);
        var arr = new PdfArray(doc);
        arr.Elements.Add(pre.Reference!);
        var existing = page.Elements["/Contents"];
        switch (Deref(existing))
        {
            case PdfArray old:
                foreach (var it in old.Elements) arr.Elements.Add(it);
                break;
            case PdfDictionary single:
                arr.Elements.Add(existing is PdfReference r ? r : Indirect(doc, single).Reference!);
                break;
        }
        arr.Elements.Add(post.Reference!);
        page.Elements["/Contents"] = arr;
    }

    private static void RemoveFlattenedFields(PdfDocument doc, List<PdfDictionary> widgets)
    {
        var acro = Dict(doc.Internals.Catalog, "/AcroForm");
        if (acro is null) return;
        var set = new HashSet<PdfDictionary>(widgets, ReferenceEqualityComparer.Instance);
        if (ArrayOf(acro, "/Fields") is { } fields) Prune(fields, set);
        if (ArrayOf(acro, "/Fields") is not { Elements.Count: > 0 }) doc.Internals.Catalog.Elements.Remove("/AcroForm");
    }

    /// <summary>Usuwa widżety z drzewa pól; pole bez dzieci znika.</summary>
    private static void Prune(PdfArray arr, HashSet<PdfDictionary> doomed)
    {
        for (int i = arr.Elements.Count - 1; i >= 0; i--)
        {
            if (Deref(arr.Elements[i]) is not PdfDictionary f) continue;
            if (doomed.Contains(f)) { arr.Elements.RemoveAt(i); continue; }
            if (ArrayOf(f, "/Kids") is { } kids)
            {
                Prune(kids, doomed);
                if (kids.Elements.Count == 0) arr.Elements.RemoveAt(i);
            }
        }
    }
}

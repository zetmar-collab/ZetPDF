using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>Adnotacja znaleziona w dokumencie: słownik, strona, indeks w /Annots i identyfikator.</summary>
internal sealed record FoundAnnot(PdfDictionary Dict, int PageIndex, int Index, AnnotationId Id);

/// <summary>
/// Operacje na adnotacjach otwartego dokumentu PDFsharp (synchroniczne – wołane wewnątrz PdfSharpEdit.Modify/Read).
/// Konwencje: /NM nadawane przez ZetPDF; odpowiedzi = /Text z /IRT i /RT /R; stan recenzji = /Text z /IRT, /State,
/// /StateModel /Review (jak Acrobat). Odpowiedzi i stany mają pusty /AP, żeby czytniki nie rysowały ich ikon.
/// </summary>
internal static class AnnotationStore
{
    public const int FlagHidden = 2;
    public const int FlagPrint = 4;
    public const int FlagNoZoom = 8;
    public const int FlagNoRotate = 16;
    public const int FlagLocked = 128;

    public static AnnotationId IdOf(PdfDictionary d, int pageIndex, int index)
    {
        var nm = Text(d, "/NM");
        if (!string.IsNullOrEmpty(nm)) return new AnnotationId(pageIndex, nm);
        if (d.Reference is { } r) return new AnnotationId(pageIndex, "obj:" + r.ObjectNumber);
        return new AnnotationId(pageIndex, "idx:" + index);
    }

    public static IEnumerable<FoundAnnot> All(PdfDocument doc, int? pageIndex = null)
    {
        int from = pageIndex ?? 0, to = pageIndex ?? doc.PageCount - 1;
        for (int p = Math.Max(0, from); p <= Math.Min(doc.PageCount - 1, to); p++)
            foreach (var (d, i) in AnnotDicts(doc.Pages[p]))
                yield return new FoundAnnot(d, p, i, IdOf(d, p, i));
    }

    public static FoundAnnot? Find(PdfDocument doc, AnnotationId id)
    {
        if (id.PageIndex >= 0 && id.PageIndex < doc.PageCount)
            foreach (var a in All(doc, id.PageIndex))
                if (a.Id.Name == id.Name) return a;
        // /NM jest unikalne w dokumencie – szukamy też na innych stronach (np. po przeniesieniu strony)
        foreach (var a in All(doc))
            if (a.Id.Name == id.Name) return a;
        return null;
    }

    public static bool IsStateAnnot(PdfDictionary d) => d.Elements.ContainsKey("/State") && d.Elements.ContainsKey("/IRT");

    public static bool IsReply(PdfDictionary d) => d.Elements.ContainsKey("/IRT") && !IsStateAnnot(d);

    /// <summary>Lista adnotacji do kontraktu (bez okienek Popup i adnotacji stanu – stan wliczany do rodzica).</summary>
    public static List<AnnotationInfo> Read(PdfDocument doc, int? pageIndex)
    {
        var all = All(doc).ToList();
        var byDict = new Dictionary<PdfDictionary, FoundAnnot>(ReferenceEqualityComparer.Instance);
        foreach (var a in all) byDict[a.Dict] = a;

        // stan recenzji: najnowsza adnotacja stanu (model Review) dla rodzica
        var states = new Dictionary<PdfDictionary, (AnnotationReviewState State, DateTimeOffset When)>(ReferenceEqualityComparer.Instance);
        foreach (var a in all.Where(x => IsStateAnnot(x.Dict)))
        {
            var model = Text(a.Dict, "/StateModel");
            if (model.Length > 0 && !model.Equals("Review", StringComparison.OrdinalIgnoreCase)) continue;
            if (Dict(a.Dict, "/IRT") is not { } parent) continue;
            var when = ParseDate(Text(a.Dict, "/M")) ?? ParseDate(Text(a.Dict, "/CreationDate")) ?? DateTimeOffset.MinValue;
            var st = ParseState(Text(a.Dict, "/State"));
            if (!states.TryGetValue(parent, out var cur) || when >= cur.When) states[parent] = (st, when);
        }

        var result = new List<AnnotationInfo>();
        foreach (var a in all)
        {
            if (pageIndex is { } pi && a.PageIndex != pi) continue;
            var d = a.Dict;
            var kind = AnnotModel.KindOf(d);
            if (kind == AnnotationKind.Popup || IsStateAnnot(d)) continue;
            var m = AnnotModel.Read(d);
            int flags = Int(d, "/F");
            AnnotationId? irt = null;
            if (Dict(d, "/IRT") is { } parent && byDict.TryGetValue(parent, out var pa)) irt = pa.Id;
            result.Add(new AnnotationInfo
            {
                Id = a.Id,
                Kind = kind,
                Rect = m.Rect,
                Author = Text(d, "/T"),
                Contents = m.Contents,
                Subject = Text(d, "/Subj"),
                Created = ParseDate(Text(d, "/CreationDate")),
                Modified = ParseDate(Text(d, "/M")),
                Color = m.Color,
                Opacity = m.Opacity,
                State = states.TryGetValue(d, out var s) ? s.State : AnnotationReviewState.None,
                InReplyTo = irt,
                IsHidden = (flags & FlagHidden) != 0,
                IsLocked = (flags & FlagLocked) != 0,
            });
        }
        return result;
    }

    public static AnnotationReviewState ParseState(string s) => s.ToLowerInvariant() switch
    {
        "accepted" => AnnotationReviewState.Accepted,
        "rejected" => AnnotationReviewState.Rejected,
        "cancelled" or "canceled" => AnnotationReviewState.Cancelled,
        "completed" => AnnotationReviewState.Completed,
        _ => AnnotationReviewState.None,
    };

    public static string StateName(AnnotationReviewState s) => s switch
    {
        AnnotationReviewState.Accepted => "Accepted",
        AnnotationReviewState.Rejected => "Rejected",
        AnnotationReviewState.Cancelled => "Cancelled",
        AnnotationReviewState.Completed => "Completed",
        _ => "None",
    };

    /// <summary>Wspólne pola nowej adnotacji.</summary>
    public static PdfDictionary NewAnnot(PdfDocument doc, PdfPage page, string subtype, string name, string author, DateTimeOffset now, int flags = FlagPrint)
    {
        var d = NewDict(doc, "/Annot");
        d.Elements["/Subtype"] = Name(subtype);
        d.Elements["/NM"] = Str(name);
        d.Elements["/T"] = Str(author);
        d.Elements["/CreationDate"] = new PdfString(FormatDate(now));
        d.Elements["/M"] = new PdfString(FormatDate(now));
        d.Elements["/F"] = new PdfInteger(flags);
        if (page.Reference is not null) d.Elements["/P"] = page.Reference;
        doc.Internals.AddObject(d);
        return d;
    }

    /// <summary>Pusty wygląd (odpowiedzi, stany) – PDFium nie wygeneruje wtedy ikony notatki.</summary>
    public static void SetEmptyAppearance(PdfDocument doc, PdfDictionary d)
    {
        var r = PdfLow.Rect(d) ?? new PdfRect(0, 0, 1, 1);
        var form = FormXObject(doc, new PdfRect(0, 0, Math.Max(1, r.Width), Math.Max(1, r.Height)), string.Empty);
        var ap = NewDict(doc);
        ap.Elements["/N"] = form.Reference!;
        d.Elements["/AP"] = ap;
    }

    /// <summary>Okienko /Popup dla notatki (zamknięte), na prawo od ikony.</summary>
    public static void AddPopup(PdfDocument doc, PdfPage page, PdfDictionary parent, PdfArray annots)
    {
        var r = PdfLow.Rect(parent) ?? default;
        double pw = page.Width.Point;
        double left = Math.Min(r.Right + 4, Math.Max(0, pw - 204));
        var popup = NewDict(doc, "/Annot");
        popup.Elements["/Subtype"] = Name("/Popup");
        popup.Elements["/Rect"] = RectArray(doc, new PdfRect(left, r.Top - 120, left + 200, r.Top));
        popup.Elements["/Parent"] = parent.Reference!;
        popup.Elements["/Open"] = new PdfBoolean(false);
        popup.Elements["/F"] = new PdfInteger(FlagPrint | FlagNoZoom | FlagNoRotate);
        if (page.Reference is not null) popup.Elements["/P"] = page.Reference;
        doc.Internals.AddObject(popup);
        parent.Elements["/Popup"] = popup.Reference!;
        annots.Elements.Add(popup.Reference!);
    }

    /// <summary>Usuwa adnotacje wraz z ich okienkami, odpowiedziami i stanami (rekurencyjnie). Zwraca liczbę usuniętych.</summary>
    public static int Remove(PdfDocument doc, IReadOnlyCollection<PdfDictionary> targets)
    {
        var doomed = new HashSet<PdfDictionary>(targets, ReferenceEqualityComparer.Instance);
        var all = All(doc).ToList();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var a in all)
            {
                if (doomed.Contains(a.Dict)) continue;
                var parent = Dict(a.Dict, "/IRT") ?? (AnnotModel.KindOf(a.Dict) == AnnotationKind.Popup ? Dict(a.Dict, "/Parent") : null);
                if (parent is not null && doomed.Contains(parent)) { doomed.Add(a.Dict); grew = true; }
            }
        }
        int removed = 0;
        for (int p = 0; p < doc.PageCount; p++)
        {
            var arr = Annots(doc.Pages[p]);
            if (arr is null) continue;
            for (int i = arr.Elements.Count - 1; i >= 0; i--)
                if (Deref(arr.Elements[i]) is PdfDictionary d && doomed.Contains(d))
                {
                    arr.Elements.RemoveAt(i);
                    removed++;
                }
        }
        return removed;
    }

    public static void SetFlag(PdfDictionary d, int flag, bool on)
    {
        int f = Int(d, "/F");
        f = on ? f | flag : f & ~flag;
        d.Elements["/F"] = new PdfInteger(f);
    }

    public static void Touch(PdfDictionary d, DateTimeOffset now) => d.Elements["/M"] = new PdfString(FormatDate(now));
}

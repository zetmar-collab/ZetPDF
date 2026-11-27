using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ZetPDF.Pages.Engine;

/// <summary>
/// Formularz przy kopiowaniu stron między dokumentami (wyodrębnianie, dzielenie, scalanie, wstawianie, zastępowanie,
/// przeplatanie, duplikowanie). PDFsharp przenosi widżety razem z polami nadrzędnymi, ale nie przenosi /AcroForm, więc pól
/// nie dało się wypełnić. Ta klasa po imporcie odbudowuje /Fields z pól, których widżety leżą na skopiowanych stronach
/// (odcina widżety z pozostałych stron), przenosi /DA, /DR, /Q, /NeedAppearances, /SigFlags i zmienia nazwy pól, które
/// kolidują z polami dokumentu docelowego (inaczej dwa pola o tej samej nazwie dzieliłyby wartość).
/// </summary>
internal sealed class FormCarry
{
    // Klucz tymczasowy na stronie źródłowej: słownik z zasobami formularza przechodzi przez import razem ze stroną
    // (PDFsharp nie udostępnia importu pojedynczych obiektów). Usuwany ze strony źródłowej i docelowej.
    private const string StashKey = "/ZetPDFCarriedAcroForm";
    private static readonly string[] CarriedKeys = { "/DA", "/DR", "/Q", "/NeedAppearances", "/SigFlags" };
    private const int MaxDepth = 32;

    private readonly PdfDocument _dest;
    private readonly List<PdfPage> _added = new();

    public FormCarry(PdfDocument dest) => _dest = dest;

    /// <summary>Importuje stronę funkcją <paramref name="add"/> (AddPage/Insert) z zasobami formularza dokumentu źródłowego.</summary>
    public PdfPage Import(PdfDocument src, PdfPage srcPage, Func<PdfPage, PdfPage> add)
    {
        // PDFsharp kopiuje ze strony tylko wybrane klucze, za to adnotacje w całości – zasoby formularza jadą w pierwszym
        // widżecie strony (strona bez widżetów i tak nie wnosi pól).
        PdfDictionary? carrier = null;
        if (PdfIo.GetDict(src.Internals.Catalog, "/AcroForm") is { } acro && FirstWidget(srcPage) is { } widget && !widget.Elements.ContainsKey(StashKey))
        {
            var carried = new PdfDictionary(src);
            foreach (var key in CarriedKeys)
            {
                if (acro.Elements[key] is not { } value) continue;
                carried.Elements[key] = value is PdfReference ? value : value.Clone();
            }
            if (carried.Elements.Count > 0)
            {
                widget.Elements[StashKey] = carried;
                carrier = widget;
            }
        }
        try
        {
            var page = add(srcPage);
            _added.Add(page);
            return page;
        }
        finally
        {
            carrier?.Elements.Remove(StashKey);
        }
    }

    private static PdfDictionary? FirstWidget(PdfPage page) =>
        PdfIo.GetArray(page, "/Annots")?.Elements.Select(PdfIo.Resolve).OfType<PdfDictionary>().FirstOrDefault(d => PdfIo.GetName(d, "/Subtype") == "/Widget");

    /// <summary>Po imporcie wszystkich stron: pola trafiają do /AcroForm dokumentu docelowego.</summary>
    public void Complete()
    {
        var widgetIds = new HashSet<PdfObjectID>();
        var widgets = new List<PdfReference>();
        var carried = new List<PdfDictionary>();
        foreach (var page in _added.Distinct())
        {
            if (PdfIo.GetArray(page, "/Annots") is not { } annots) continue;
            foreach (var e in annots.Elements)
            {
                if (PdfIo.Resolve(e) is not PdfDictionary d || PdfIo.GetName(d, "/Subtype") != "/Widget") continue;
                if (PdfIo.GetDict(d, StashKey) is { } stash) carried.Add(stash);
                d.Elements.Remove(StashKey);
                if (e is not PdfReference r) continue;
                if (page.Reference is { } pageRef) d.Elements["/P"] = pageRef;
                if (widgetIds.Add(r.ObjectID)) widgets.Add(r);
            }
        }
        if (widgets.Count == 0) return;

        var roots = new List<PdfReference>();
        var rootIds = new HashSet<PdfObjectID>();
        foreach (var w in widgets)
        {
            if (RootOf(w) is { } root && rootIds.Add(root.ObjectID)) roots.Add(root);
        }
        roots.RemoveAll(r => !Prune((PdfDictionary)r.Value, widgetIds, 0));
        if (roots.Count == 0) return;

        var acro = PdfIo.GetDict(_dest.Internals.Catalog, "/AcroForm");
        if (acro is null)
        {
            acro = new PdfDictionary(_dest);
            _dest.Internals.AddObject(acro);
            _dest.Internals.Catalog.Elements["/AcroForm"] = acro.Reference!;
        }
        var fields = PdfIo.GetArray(acro, "/Fields");
        if (fields is null)
        {
            fields = new PdfArray(_dest);
            acro.Elements["/Fields"] = fields;
        }

        var present = new HashSet<PdfObjectID>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in fields.Elements)
        {
            if (e is PdfReference r) present.Add(r.ObjectID);
            if (PdfIo.Resolve(e) is PdfDictionary d && PdfIo.GetText(d.Elements["/T"]) is { } t) names.Add(t);
        }
        foreach (var root in roots)
        {
            if (!present.Add(root.ObjectID)) continue;
            var d = (PdfDictionary)root.Value;
            if (PdfIo.GetText(d.Elements["/T"]) is { } name)
            {
                if (names.Contains(name))
                {
                    name = UniqueName(name, names);
                    d.Elements["/T"] = Text(name);
                }
                names.Add(name);
            }
            fields.Elements.Add(root);
        }
        foreach (var c in carried) MergeSettings(acro, c);
    }

    /// <summary>Pole najwyższego poziomu dla widżetu; null, gdy w łańcuchu nie ma danych pola (sam wygląd bez pola).</summary>
    private static PdfReference? RootOf(PdfReference widget)
    {
        var current = widget;
        bool isField = false;
        var seen = new HashSet<PdfObjectID>();
        for (int depth = 0; depth <= MaxDepth && seen.Add(current.ObjectID); depth++)
        {
            var d = (PdfDictionary)current.Value;
            if (d.Elements.ContainsKey("/FT") || d.Elements.ContainsKey("/T")) isField = true;
            if (d.Elements["/Parent"] is PdfReference parent && parent.Value is PdfDictionary) current = parent;
            else return isField ? current : null;
        }
        return null; // cykl lub zbyt głęboko
    }

    /// <summary>Odcina widżety spoza skopiowanych stron. False = z pola nic nie zostało.</summary>
    private static bool Prune(PdfDictionary field, HashSet<PdfObjectID> widgetIds, int depth)
    {
        if (depth > MaxDepth) return false;
        if (PdfIo.GetArray(field, "/Kids") is not { } kids || kids.Elements.Count == 0) return true; // pole połączone z widżetem
        for (int i = kids.Elements.Count - 1; i >= 0; i--)
        {
            var e = kids.Elements[i];
            bool keep = PdfIo.Resolve(e) switch
            {
                PdfDictionary k when PdfIo.GetName(k, "/Subtype") == "/Widget" => e is PdfReference r && widgetIds.Contains(r.ObjectID),
                PdfDictionary k => Prune(k, widgetIds, depth + 1),
                _ => false,
            };
            if (!keep) kids.Elements.RemoveAt(i);
        }
        return kids.Elements.Count > 0;
    }

    private static void MergeSettings(PdfDictionary acro, PdfDictionary carried)
    {
        foreach (var key in new[] { "/DA", "/Q" })
        {
            if (!acro.Elements.ContainsKey(key) && carried.Elements[key] is { } v) acro.Elements[key] = v;
        }
        if (PdfIo.Resolve(carried.Elements["/NeedAppearances"]) is PdfBoolean { Value: true }) acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
        if (PdfIo.GetNumber(carried.Elements["/SigFlags"]) is { } flags)
            acro.Elements["/SigFlags"] = new PdfInteger((int)(PdfIo.GetNumber(acro.Elements["/SigFlags"]) ?? 0) | (int)flags);

        if (carried.Elements["/DR"] is not { } dr) return;
        if (PdfIo.GetDict(acro, "/DR") is not { } target)
        {
            acro.Elements["/DR"] = dr;
            return;
        }
        if (PdfIo.Resolve(dr) is not PdfDictionary source) return;
        // Zasoby obu dokumentów: brakujące kategorie i nazwy (np. czcionki /Helv) dopisywane, istniejące zostają.
        foreach (var category in source.Elements.Keys.ToArray())
        {
            if (PdfIo.Resolve(source.Elements[category]) is not PdfDictionary from) continue;
            if (PdfIo.GetDict(target, category) is not { } to)
            {
                target.Elements[category] = source.Elements[category];
                continue;
            }
            foreach (var name in from.Elements.Keys.ToArray())
            {
                if (!to.Elements.ContainsKey(name)) to.Elements[name] = from.Elements[name];
            }
        }
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        for (int k = 2; ; k++)
        {
            var candidate = $"{name}_{k}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private static PdfString Text(string s) =>
        s.All(c => c < 128) ? new PdfString(s) : new PdfString(s, PdfStringEncoding.Unicode);
}

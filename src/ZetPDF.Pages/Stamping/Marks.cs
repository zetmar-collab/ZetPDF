using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Engine;

namespace ZetPDF.Pages.Stamping;

/// <summary>
/// Rozpoznawalne oznaczenia stron (decyzja projektowa – patrz docs\ograniczenia\pages-engine.md):
/// każde oznaczenie to osobny XObject typu Form z kluczem /ZetPDFMark /&lt;Rodzaj&gt; (i opcjami JSON w /ZetPDFOptions),
/// wywoływany z OSOBNEGO strumienia treści strony, również oznaczonego /ZetPDFMark, w sekwencji
/// /Artifact &lt;&lt;/Type /Pagination /Subtype …&gt;&gt; BDC … EMC (artefakt – czytniki ekranu go pomijają).
/// Usuwanie: usunięcie oznaczonych strumieni strony i wpisów zasobów; dodatkowo oznaczone XObjecty są opróżniane,
/// gdyby inny program scalił strumienie treści (wtedy wywołanie zostaje, ale nic nie rysuje).
/// </summary>
internal static class Marks
{
    public const string Watermark = "/Watermark";
    public const string Background = "/Background";
    public const string HeaderFooter = "/HeaderFooter";
    public const string PageNumbers = "/PageNumbers";
    public const string Bates = "/Bates";

    public static string KindName(PageMarkKind k) => k switch
    {
        PageMarkKind.Watermark => Watermark,
        PageMarkKind.Background => Background,
        PageMarkKind.HeaderFooter => HeaderFooter,
        PageMarkKind.PageNumbers => PageNumbers,
        PageMarkKind.Bates => Bates,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static PageMarkKind KindOf(string? name) => name switch
    {
        Watermark => PageMarkKind.Watermark,
        Background => PageMarkKind.Background,
        HeaderFooter => PageMarkKind.HeaderFooter,
        PageNumbers => PageMarkKind.PageNumbers,
        Bates => PageMarkKind.Bates,
        _ => PageMarkKind.None,
    };

    /// <summary>Oznacza formularz rodzajem i opcjami.</summary>
    public static void Tag(PdfDictionary form, string kind, string? optionsJson)
    {
        form.Elements[PdfIo.MarkKey] = new PdfName(kind);
        if (optionsJson is not null) form.Elements[PdfIo.OptionsKey] = new PdfString(optionsJson, PdfStringEncoding.Unicode);
    }

    /// <summary>
    /// Umieszcza formularz na stronie. local = przekształcenie przestrzeni formularza (BBox) do układu wyświetlanego strony
    /// (po /Rotate, początek lewy-dolny widocznej ramki).
    /// </summary>
    public static void Place(PdfDocument doc, PdfPage page, PdfDictionary form, string kind, PdfMatrix local, double opacity, bool behind)
    {
        var frame = PdfIo.Frame(page);
        var m = local.Then(frame.ToUser);
        var res = page.Resources;
        var xo = PdfIo.SubResources(res, "/XObject");
        var name = PdfIo.UniqueName(xo, "ZMk");
        xo.Elements[name] = form.Reference!;
        string gs = string.Empty;
        if (opacity < 0.999)
        {
            var egs = PdfIo.SubResources(res, "/ExtGState");
            var gname = PdfIo.UniqueName(egs, "ZGs");
            egs.Elements[gname] = FormFactory.Opacity(doc, opacity).Reference!;
            gs = gname + " gs ";
        }
        var tag = kind switch
        {
            Background => "/Artifact <</Type /Background>> BDC",
            Watermark => "/Artifact <</Type /Pagination /Subtype /Watermark>> BDC",
            Bates or PageNumbers => "/Artifact <</Type /Pagination /Subtype /Footer>> BDC",
            _ => "/Artifact <</Type /Pagination>> BDC",
        };
        var content = PdfIo.Latin1($"{tag} q {gs}{PdfIo.Matrix(m)} cm {name} Do Q EMC\n");
        if (behind) PdfIo.PrependContent(page, content, kind);
        else PdfIo.AppendContent(page, content, kind);
    }

    /// <summary>Usuwa oznaczenia danych rodzajów. Zwraca liczbę usuniętych wywołań.</summary>
    public static int Remove(PdfDocument doc, ISet<string> kinds)
    {
        int removed = 0;
        foreach (var page in doc.Pages)
        {
            if (PdfIo.Resolve(page.Elements["/Contents"]) is PdfArray arr)
            {
                for (int i = arr.Elements.Count - 1; i >= 0; i--)
                    if (PdfIo.Resolve(arr.Elements[i]) is PdfDictionary d && PdfIo.GetName(d, PdfIo.MarkKey) is { } k && kinds.Contains(k))
                    {
                        arr.Elements.RemoveAt(i);
                        removed++;
                    }
                // brak innych oznaczeń → zdejmujemy owinięcie q/Q
                bool anyMark = arr.Elements.Any(e => PdfIo.Resolve(e) is PdfDictionary d && PdfIo.GetName(d, PdfIo.MarkKey) is { } k && k != PdfIo.WrapMark);
                if (!anyMark)
                    for (int i = arr.Elements.Count - 1; i >= 0; i--)
                        if (PdfIo.Resolve(arr.Elements[i]) is PdfDictionary d && PdfIo.GetName(d, PdfIo.MarkKey) == PdfIo.WrapMark) arr.Elements.RemoveAt(i);
            }
            else if (PdfIo.Resolve(page.Elements["/Contents"]) is PdfDictionary single && PdfIo.GetName(single, PdfIo.MarkKey) is { } k1 && kinds.Contains(k1))
            {
                page.Elements.Remove("/Contents");
                removed++;
            }
            if (PdfIo.GetDict(page.Resources, "/XObject") is { } xo)
                foreach (var key in xo.Elements.Keys.ToArray())
                    if (PdfIo.Resolve(xo.Elements[key]) is PdfDictionary f && PdfIo.GetName(f, PdfIo.MarkKey) is { } fk && kinds.Contains(fk))
                        xo.Elements.Remove(key);
        }
        // zabezpieczenie: oznaczone formularze, do których coś jeszcze się odwołuje, nic nie rysują
        foreach (var obj in doc.Internals.GetAllObjects().OfType<PdfDictionary>())
            if (PdfIo.GetName(obj, PdfIo.MarkKey) is { } k && kinds.Contains(k) && PdfIo.GetName(obj, "/Subtype") == "/Form")
            {
                PdfIo.SetStreamData(obj, Array.Empty<byte>());
                obj.Elements["/Resources"] = new PdfDictionary();
            }
        return removed;
    }

    public static PageMarkKind Detect(PdfDocument doc)
    {
        var kinds = PageMarkKind.None;
        foreach (var page in doc.Pages)
            foreach (var d in PdfIo.ContentDicts(page))
                kinds |= KindOf(PdfIo.GetName(d, PdfIo.MarkKey));
        return kinds;
    }

    /// <summary>Opcje (JSON) zapisane w pierwszym oznaczeniu danego rodzaju.</summary>
    public static string? ReadOptions(PdfDocument doc, string kind)
    {
        foreach (var page in doc.Pages)
            if (PdfIo.GetDict(page, "/Resources") is { } res && PdfIo.GetDict(res, "/XObject") is { } xo)
                foreach (var key in xo.Elements.Keys)
                    if (PdfIo.Resolve(xo.Elements[key]) is PdfDictionary f && PdfIo.GetName(f, PdfIo.MarkKey) == kind
                        && PdfIo.GetText(f.Elements[PdfIo.OptionsKey]) is { } json)
                        return json;
        return null;
    }
}

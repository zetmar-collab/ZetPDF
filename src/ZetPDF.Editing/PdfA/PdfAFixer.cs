using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Editing.Pdf;

namespace ZetPDF.Editing.PdfA;

/// <summary>Zmiany strukturalne konwersji: usuwanie skryptów, akcji, multimediów, załączników i kluczy zabronionych, flagi adnotacji, metadane, OutputIntent.</summary>
internal static class PdfAFixer
{
    public static void RemoveForbiddenStructure(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        foreach (var key in PdfAInspector.CatalogForbiddenKeys) catalog.Elements.Remove(key);
        catalog.Elements.Remove("/AA");
        catalog.Elements.Remove("/Version");
        catalog.Elements.Remove("/Perms");
        CleanAction(catalog, "/OpenAction");
        if (PdfObjects.Resolve(catalog.Elements["/Names"]) is PdfDictionary names)
            foreach (var key in new[] { "/JavaScript", "/EmbeddedFiles", "/Renditions", "/AlternatePresentations" }) names.Elements.Remove(key);
        if (PdfObjects.Resolve(catalog.Elements["/AcroForm"]) is PdfDictionary acro)
        {
            acro.Elements.Remove("/XFA");
            acro.Elements.Remove("/NeedAppearances");
        }
        CleanOutlines(catalog);
        FixOptionalContent(catalog);

        foreach (var page in doc.Pages)
        {
            page.Elements.Remove("/AA");
            page.Elements.Remove("/PresSteps");
            page.Elements.Remove("/Metadata");
            FixAnnotations(page);
        }
        foreach (var o in doc.Internals.GetAllObjects())
        {
            if (o is not PdfDictionary d) continue;
            FixStream(d);
            FixGraphicsState(d);
        }
    }

    private static void CleanAction(PdfDictionary owner, string key)
    {
        if (PdfObjects.Resolve(owner.Elements[key]) is not PdfDictionary a) return;
        if (PdfAInspector.IsForbiddenAction(a)) owner.Elements.Remove(key);
        else a.Elements.Remove("/Next");
    }

    private static void CleanOutlines(PdfDictionary catalog)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem?>();
        stack.Push(catalog.Elements["/Outlines"]);
        while (stack.Count > 0)
        {
            if (PdfObjects.Resolve(stack.Pop()) is not PdfDictionary d || !seen.Add(d)) continue;
            CleanAction(d, "/A");
            stack.Push(d.Elements["/First"]);
            stack.Push(d.Elements["/Next"]);
        }
    }

    private static void FixOptionalContent(PdfDictionary catalog)
    {
        if (PdfObjects.Resolve(catalog.Elements["/OCProperties"]) is not PdfDictionary oc) return;
        int n = 0;
        void Config(PdfDictionary c)
        {
            c.Elements.Remove("/AS");
            if (!c.Elements.ContainsKey("/Name")) c.Elements["/Name"] = new PdfString("Config " + ++n);
        }
        if (PdfObjects.Resolve(oc.Elements["/D"]) is PdfDictionary d) Config(d);
        if (PdfObjects.Resolve(oc.Elements["/Configs"]) is PdfArray cs)
            foreach (var c in cs.Elements) if (PdfObjects.Resolve(c) is PdfDictionary cd) Config(cd);
    }

    private static void FixAnnotations(PdfPage page)
    {
        var removed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        PdfObjects.RemoveAnnotations(page, a =>
        {
            bool remove = ShouldRemove(a);
            if (remove) removed.Add(a);
            return remove;
        });
        // Okienka usuniętych komentarzy nie mogą zostać same.
        PdfObjects.RemoveAnnotations(page, a => PdfObjects.Subtype(a) == "/Popup" && PdfObjects.Resolve(a.Elements["/Parent"]) is PdfDictionary p && removed.Contains(p));
        foreach (var a in PdfObjects.Annotations(page))
        {
            CleanAction(a, "/A");
            a.Elements.Remove("/AA");
            if (PdfObjects.Subtype(a) == "/Popup") continue;
            if (PdfObjects.Resolve(a.Elements["/Popup"]) is PdfDictionary pop && removed.Contains(pop)) a.Elements.Remove("/Popup");
            a.Elements.SetInteger("/F", (a.Elements.GetInteger("/F") | 4) & ~256);
            if (PdfObjects.Resolve(a.Elements["/AP"]) is PdfDictionary ap)
            {
                ap.Elements.Remove("/D");
                ap.Elements.Remove("/R");
            }
        }
    }

    private static bool ShouldRemove(PdfDictionary a)
    {
        var subtype = PdfObjects.Subtype(a);
        if (PdfAInspector.MultimediaAnnotationTypes.Contains(subtype) || subtype == "/FileAttachment") return true;
        if (subtype == "/Popup") return false;
        if ((a.Elements.GetInteger("/F") & (1 | 2 | 32)) != 0) return true;
        if (subtype == "/Link") return false;
        bool empty = PdfObjects.Rect(a.Elements["/Rect"]) is { } r && (r.Width <= 0 || r.Height <= 0);
        return !empty && !(PdfObjects.Resolve(a.Elements["/AP"]) is PdfDictionary ap && ap.Elements.ContainsKey("/N"));
    }

    private static void FixStream(PdfDictionary d)
    {
        if (d.Stream is null) return;
        var subtype = d.Elements.GetName("/Subtype");
        if (subtype is "/Image" or "/Form")
        {
            d.Elements.Remove("/Interpolate");
            d.Elements.Remove("/OPI");
            d.Elements.Remove("/Alternates");
        }
        if (subtype == "/Form")
        {
            d.Elements.Remove("/Ref");
            d.Elements.Remove("/PS");
        }
        if (d.Elements.GetName("/Type") == "/Metadata") return;
        var filter = PdfObjects.Resolve(d.Elements["/Filter"]);
        bool lzw = filter is PdfName { Value: "/LZWDecode" } || (filter is PdfArray arr && arr.Elements.OfType<PdfName>().Any(n => n.Value == "/LZWDecode"));
        if (lzw && d.Stream.TryUncompress()) d.Stream.Zip();
    }

    private static void FixGraphicsState(PdfDictionary d)
    {
        if (d.Stream is not null) return;
        if (d.Elements.ContainsKey("/TR")) d.Elements.Remove("/TR");
        if (d.Elements.ContainsKey("/HTP")) d.Elements.Remove("/HTP");
        if (d.Elements.ContainsKey("/TR2") && d.Elements.GetName("/TR2") != "/Default") d.Elements["/TR2"] = new PdfName("/Default");
        if (PdfObjects.Resolve(d.Elements["/BM"]) is PdfName bm && !PdfAInspector.BlendModes.Contains(bm.Value)) d.Elements["/BM"] = new PdfName("/Normal");
    }

    /// <summary>Profil wyjściowy sRGB, daty i wersja pliku. Metadane XMP dopisuje później <see cref="PdfAMetadata.WriteToSavedFile"/>.</summary>
    public static void WriteArchiveStructure(PdfDocument doc, byte[] iccProfile)
    {
        var catalog = doc.Internals.Catalog;
        var modified = DateTime.Now;
        if (doc.Info.CreationDate == default) doc.Info.CreationDate = modified;
        doc.Info.ModificationDate = modified;

        var profile = new PdfDictionary(doc);
        profile.Elements.SetInteger("/N", 3);
        profile.CreateStream(iccProfile);
        profile.Stream.Zip();
        doc.Internals.AddObject(profile);
        var intent = new PdfDictionary(doc);
        intent.Elements["/Type"] = new PdfName("/OutputIntent");
        intent.Elements["/S"] = new PdfName("/GTS_PDFA1");
        intent.Elements["/OutputConditionIdentifier"] = new PdfString("sRGB IEC61966-2.1");
        intent.Elements["/Info"] = new PdfString("sRGB IEC61966-2.1");
        intent.Elements["/DestOutputProfile"] = profile.Reference!;
        doc.Internals.AddObject(intent);
        catalog.Elements["/OutputIntents"] = new PdfArray(doc, intent.Reference!);
        doc.Version = Math.Max(doc.Version, 17);
    }
}

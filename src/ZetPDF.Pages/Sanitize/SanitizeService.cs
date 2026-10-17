using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Content;
using ZetPDF.Pages.Engine;
using ZetPDF.Pages.Optimizer;

namespace ZetPDF.Pages.Sanitize;

/// <summary>
/// Oczyszczanie dokumentu: metadane (Info + XMP + /Metadata obiektów + /PieceInfo), komentarze, załączniki
/// (EmbeddedFiles + FileAttachment + /AF), JavaScript (Names/JavaScript, OpenAction, /AA, akcje JS w łączach,
/// polach, zakładkach, łańcuchy /Next), ukryte warstwy (treść OCG wyłączonych usuwana ze strumieni, potem całe
/// /OCProperties – reszta treści staje się zwykła), dane formularza (opcja), łącza do plików zewnętrznych, miniatury.
/// Usunięte obiekty nie trafiają do pliku (PDFsharp zapisuje tylko obiekty osiągalne).
/// </summary>
public sealed class SanitizeService : ISanitizeService
{
    private static readonly HashSet<string> ExternalActions = new(StringComparer.Ordinal) { "/Launch", "/GoToR", "/GoToE", "/ImportData", "/SubmitForm" };

    public Task<SanitizeResult> SanitizeAsync(byte[] pdf, SanitizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            var catalog = doc.Internals.Catalog;
            int meta = 0, comments = 0, attachments = 0, scripts = 0, layers = 0, links = 0;
            var names = PdfIo.GetDict(catalog, "/Names");

            progress?.Report(new OperationProgress(0.1));
            if (options.HiddenLayers) layers = RemoveHiddenLayers(doc, ct);
            ct.ThrowIfCancellationRequested();

            if (options.JavaScript)
            {
                if (names is not null && names.Elements.ContainsKey("/JavaScript"))
                {
                    scripts += CountNameTree(PdfIo.GetDict(names, "/JavaScript"));
                    names.Elements.Remove("/JavaScript");
                }
                scripts += StripActions(catalog, js: true, external: false);
            }
            if (options.ExternalLinks) links += StripActions(catalog, js: false, external: true);

            if (options.Attachments)
            {
                if (names is not null && names.Elements.ContainsKey("/EmbeddedFiles"))
                {
                    attachments += CountNameTree(PdfIo.GetDict(names, "/EmbeddedFiles"));
                    names.Elements.Remove("/EmbeddedFiles");
                }
                if (catalog.Elements.ContainsKey("/AF")) { catalog.Elements.Remove("/AF"); attachments++; }
                if (catalog.Elements.ContainsKey("/Collection")) catalog.Elements.Remove("/Collection");
            }
            if (names is not null && names.Elements.Count == 0) catalog.Elements.Remove("/Names");

            // strony i adnotacje
            int pc = doc.PageCount;
            for (int p = 0; p < pc; p++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress(0.2 + 0.6 * p / Math.Max(1, pc)));
                var page = doc.Pages[p];
                if (options.Thumbnails) page.Elements.Remove("/Thumb");
                if (options.JavaScript && page.Elements.ContainsKey("/AA")) { page.Elements.Remove("/AA"); scripts++; }
                if (options.Attachments && page.Elements.ContainsKey("/AF")) page.Elements.Remove("/AF");
                if (PdfIo.GetArray(page, "/Annots") is not { } annots) continue;
                for (int i = annots.Elements.Count - 1; i >= 0; i--)
                {
                    if (PdfIo.Resolve(annots.Elements[i]) is not PdfDictionary a) continue;
                    var sub = PdfIo.GetName(a, "/Subtype");
                    bool remove = false;
                    if (sub == "/FileAttachment" && options.Attachments) { remove = true; attachments++; }
                    else if (sub is not ("/Link" or "/Widget" or "/Popup") && options.Comments) { remove = true; comments++; }
                    else if (sub == "/Popup" && options.Comments) remove = true;
                    if (!remove && options.JavaScript)
                    {
                        int n = StripActions(a, js: true, external: false);
                        scripts += n;
                        if (n > 0 && sub == "/Link" && !a.Elements.ContainsKey("/A") && !a.Elements.ContainsKey("/Dest")) remove = true;
                    }
                    if (!remove && options.ExternalLinks && sub == "/Link")
                    {
                        int n = StripActions(a, js: false, external: true);
                        links += n;
                        if (n > 0 && !a.Elements.ContainsKey("/A") && !a.Elements.ContainsKey("/Dest")) remove = true;
                    }
                    if (remove) annots.Elements.RemoveAt(i);
                }
                if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
            }

            // pola formularza, zakładki
            if (PdfIo.GetDict(catalog, "/AcroForm") is { } acro)
            {
                if (options.JavaScript || options.ExternalLinks)
                    foreach (var f in AllFields(acro))
                    {
                        if (options.JavaScript) scripts += StripActions(f, js: true, external: false);
                        if (options.ExternalLinks) links += StripActions(f, js: false, external: true);
                    }
                if (options.FormData) ClearFormData(acro);
                if (options.JavaScript || options.FormData) acro.Elements.Remove("/XFA");
            }
            if (PdfIo.GetDict(catalog, "/Outlines") is { } outlines)
                foreach (var o in OutlineItems(outlines))
                {
                    if (options.JavaScript) scripts += StripActions(o, js: true, external: false);
                    if (options.ExternalLinks) links += StripActions(o, js: false, external: true);
                }

            if (options.Metadata) meta = OptimizerService.RemoveMetadata(doc);
            progress?.Report(new OperationProgress(0.9));
            var result = PdfIo.Save(doc);
            progress?.Report(new OperationProgress(1));
            return new SanitizeResult(result, meta, comments, attachments, scripts, layers, links);
        }, ct);

    // ------------------------------------------------------------------ akcje

    /// <summary>Usuwa akcje JavaScript (js) lub zewnętrzne (external) z /A, /OpenAction, /AA i łańcuchów /Next.</summary>
    private static int StripActions(PdfDictionary owner, bool js, bool external)
    {
        int removed = 0;
        foreach (var key in new[] { "/A", "/OpenAction" })
        {
            if (PdfIo.Resolve(owner.Elements[key]) is not PdfDictionary action) continue;
            if (Matches(action, js, external)) { owner.Elements.Remove(key); removed++; }
            else removed += StripNext(action, js, external, 0);
        }
        if (PdfIo.GetDict(owner, "/AA") is { } aa)
        {
            if (js)
            {
                // akcje dodatkowe (wyzwalacze) to w praktyce JavaScript – usuwamy w całości, jeśli zawierają JS
                if (aa.Elements.Values.Any(v => PdfIo.Resolve(v) is PdfDictionary ad && ContainsMatch(ad, true, false, 0))) { owner.Elements.Remove("/AA"); removed++; }
            }
            else
                foreach (var k in aa.Elements.Keys.ToArray())
                    if (PdfIo.Resolve(aa.Elements[k]) is PdfDictionary ad && Matches(ad, js, external)) { aa.Elements.Remove(k); removed++; }
        }
        return removed;
    }

    private static int StripNext(PdfDictionary action, bool js, bool external, int depth)
    {
        if (depth > 16) return 0;
        int removed = 0;
        var next = PdfIo.Resolve(action.Elements["/Next"]);
        if (next is PdfDictionary nd)
        {
            if (Matches(nd, js, external)) { action.Elements.Remove("/Next"); removed++; }
            else removed += StripNext(nd, js, external, depth + 1);
        }
        else if (next is PdfArray arr)
        {
            for (int i = arr.Elements.Count - 1; i >= 0; i--)
                if (PdfIo.Resolve(arr.Elements[i]) is PdfDictionary ad)
                {
                    if (Matches(ad, js, external)) { arr.Elements.RemoveAt(i); removed++; }
                    else removed += StripNext(ad, js, external, depth + 1);
                }
        }
        return removed;
    }

    private static bool ContainsMatch(PdfDictionary action, bool js, bool external, int depth)
    {
        if (depth > 16) return false;
        if (Matches(action, js, external)) return true;
        return PdfIo.Resolve(action.Elements["/Next"]) switch
        {
            PdfDictionary nd => ContainsMatch(nd, js, external, depth + 1),
            PdfArray arr => arr.Elements.Any(e => PdfIo.Resolve(e) is PdfDictionary d && ContainsMatch(d, js, external, depth + 1)),
            _ => false,
        };
    }

    private static bool Matches(PdfDictionary action, bool js, bool external)
    {
        var s = PdfIo.GetName(action, "/S");
        if (js && (s is "/JavaScript" or "/Rendition" || action.Elements.ContainsKey("/JS"))) return true;
        if (external)
        {
            if (s is not null && ExternalActions.Contains(s)) return true;
            if (s == "/URI" && PdfIo.GetText(action.Elements["/URI"]) is { } uri && uri.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ warstwy

    /// <summary>Usuwa treść warstw wyłączonych (OCG z /D /OFF lub /BaseState /OFF), potem /OCProperties. Zwraca liczbę usuniętych warstw.</summary>
    private static int RemoveHiddenLayers(PdfDocument doc, CancellationToken ct)
    {
        var catalog = doc.Internals.Catalog;
        if (PdfIo.GetDict(catalog, "/OCProperties") is not { } ocp) return 0;
        var all = (PdfIo.GetArray(ocp, "/OCGs")?.Elements.OfType<PdfReference>() ?? Enumerable.Empty<PdfReference>()).Select(r => r.ObjectID).ToHashSet();
        var off = new HashSet<PdfObjectID>();
        if (PdfIo.GetDict(ocp, "/D") is { } d)
        {
            bool baseOff = PdfIo.GetName(d, "/BaseState") == "/OFF";
            var on = (PdfIo.GetArray(d, "/ON")?.Elements.OfType<PdfReference>().Select(r => r.ObjectID) ?? Enumerable.Empty<PdfObjectID>()).ToHashSet();
            if (baseOff) foreach (var id in all) if (!on.Contains(id)) off.Add(id);
            foreach (var r in PdfIo.GetArray(d, "/OFF")?.Elements.OfType<PdfReference>() ?? Enumerable.Empty<PdfReference>()) off.Add(r.ObjectID);
        }
        if (off.Count > 0)
        {
            foreach (var page in doc.Pages)
            {
                ct.ThrowIfCancellationRequested();
                var res = PdfIo.GetDict(page, "/Resources");
                foreach (var c in PdfIo.ContentDicts(page).ToList())
                {
                    var data = PdfIo.StreamData(c);
                    var nd = StripOptionalContent(data, res, off);
                    if (!ReferenceEquals(nd, data)) PdfIo.SetStreamData(c, nd);
                }
                if (PdfIo.GetArray(page, "/Annots") is { } annots)
                    for (int i = annots.Elements.Count - 1; i >= 0; i--)
                        if (PdfIo.Resolve(annots.Elements[i]) is PdfDictionary a && IsHidden(a.Elements["/OC"], off)) annots.Elements.RemoveAt(i);
            }
            foreach (var obj in doc.Internals.GetAllObjects().OfType<PdfDictionary>())
            {
                if (obj.Stream is null) continue;
                if (PdfIo.GetName(obj, "/Subtype") is "/Form" or "/Image" && IsHidden(obj.Elements["/OC"], off))
                {
                    // XObject warstwy ukrytej: pusty (formularz) / przezroczysty 1x1 nie jest potrzebny – formularz opróżniamy
                    if (PdfIo.GetName(obj, "/Subtype") == "/Form") { PdfIo.SetStreamData(obj, Array.Empty<byte>()); obj.Elements["/Resources"] = new PdfDictionary(); }
                    else { obj.Elements["/ImageMask"] = new PdfBoolean(true); obj.Elements.Remove("/ColorSpace"); obj.Elements.Remove("/SMask"); obj.Elements["/Decode"] = PdfIo.NumberArray(null, 1, 0); obj.Elements.SetInteger("/BitsPerComponent", 1); obj.Elements.SetInteger("/Width", 1); obj.Elements.SetInteger("/Height", 1); obj.Elements.Remove("/Filter"); obj.Elements.Remove("/DecodeParms"); obj.Stream.Value = new byte[] { 0 }; }
                }
                else if (PdfIo.GetName(obj, "/Subtype") == "/Form")
                {
                    var data = PdfIo.StreamData(obj);
                    var nd = StripOptionalContent(data, PdfIo.GetDict(obj, "/Resources"), off);
                    if (!ReferenceEquals(nd, data)) PdfIo.SetStreamData(obj, nd);
                }
            }
        }
        catalog.Elements.Remove("/OCProperties");
        return off.Count;
    }

    private static bool IsHidden(PdfItem? oc, HashSet<PdfObjectID> off)
    {
        if (oc is PdfReference r && off.Contains(r.ObjectID)) return true;
        // OCMD: /OCGs z polityką AnyOn (domyślnie) – ukryte, gdy wszystkie wskazane są wyłączone
        if (PdfIo.Resolve(oc) is PdfDictionary md && PdfIo.GetName(md, "/Type") == "/OCMD")
        {
            var ocgs = PdfIo.Resolve(md.Elements["/OCGs"]);
            var ids = ocgs switch
            {
                PdfArray a => a.Elements.OfType<PdfReference>().Select(x => x.ObjectID).ToList(),
                _ => md.Elements["/OCGs"] is PdfReference single ? new List<PdfObjectID> { single.ObjectID } : new List<PdfObjectID>(),
            };
            return ids.Count > 0 && ids.All(off.Contains);
        }
        return false;
    }

    /// <summary>Wycina z treści sekwencje /OC /nazwa BDC … EMC dla warstw ukrytych (z zagnieżdżeniem BMC/BDC).</summary>
    internal static byte[] StripOptionalContent(byte[] content, PdfDictionary? resources, HashSet<PdfObjectID> off)
    {
        if (content.Length == 0) return content;
        var props = resources is null ? null : PdfIo.GetDict(resources, "/Properties");
        var lexer = new ContentLexer(content);
        var output = new MemoryStream(content.Length);
        int copied = 0, depth = 0, cutDepth = -1, cutStart = 0;
        bool changed = false;
        foreach (var op in ContentParser.Operations(lexer))
        {
            switch (op.Operator)
            {
                case "BMC": depth++; break;
                case "BDC":
                    depth++;
                    if (cutDepth < 0 && op.Operands.Count == 2 && lexer.Text(op.Operands[0]) == "/OC" && op.Operands[1].Kind == TokenKind.Name
                        && props?.Elements[lexer.Text(op.Operands[1])] is { } prop && IsHidden(prop, off))
                    {
                        cutDepth = depth;
                        cutStart = op.Start;
                    }
                    break;
                case "EMC":
                    if (cutDepth == depth)
                    {
                        output.Write(content, copied, cutStart - copied);
                        copied = op.End;
                        cutDepth = -1;
                        changed = true;
                    }
                    depth = Math.Max(0, depth - 1);
                    break;
            }
        }
        if (!changed) return content;
        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
    }

    // ------------------------------------------------------------------ formularze, zakładki, drzewa nazw

    private static IEnumerable<PdfDictionary> AllFields(PdfDictionary acro)
    {
        var stack = new Stack<(PdfDictionary, int)>();
        foreach (var f in PdfIo.GetArray(acro, "/Fields")?.Elements ?? Enumerable.Empty<PdfItem>())
            if (PdfIo.Resolve(f) is PdfDictionary d) stack.Push((d, 0));
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var (d, depth) = stack.Pop();
            if (!seen.Add(d) || depth > 32) continue;
            yield return d;
            foreach (var k in PdfIo.GetArray(d, "/Kids")?.Elements ?? Enumerable.Empty<PdfItem>())
                if (PdfIo.Resolve(k) is PdfDictionary kd) stack.Push((kd, depth + 1));
        }
    }

    private static void ClearFormData(PdfDictionary acro)
    {
        foreach (var f in AllFields(acro))
        {
            f.Elements.Remove("/V");
            if (f.Elements.ContainsKey("/AS")) f.Elements["/AS"] = new PdfName("/Off");
            // wygląd pól tekstowych/list zawiera starą wartość – usuwamy, czytnik odtworzy pusty
            var ft = PdfIo.GetName(f, "/FT");
            if (ft is "/Tx" or "/Ch" || (ft is null && PdfIo.GetName(f, "/Subtype") == "/Widget" && !f.Elements.ContainsKey("/AS")))
                f.Elements.Remove("/AP");
        }
        acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
    }

    private static IEnumerable<PdfDictionary> OutlineItems(PdfDictionary root)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfDictionary>();
        if (PdfIo.GetDict(root, "/First") is { } first) stack.Push(first);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (!seen.Add(item) || seen.Count > 100000) continue;
            yield return item;
            if (PdfIo.GetDict(item, "/Next") is { } next) stack.Push(next);
            if (PdfIo.GetDict(item, "/First") is { } child) stack.Push(child);
        }
    }

    private static int CountNameTree(PdfDictionary? node, int depth = 0)
    {
        if (node is null || depth > 32) return 0;
        int n = (PdfIo.GetArray(node, "/Names")?.Elements.Count ?? 0) / 2;
        foreach (var k in PdfIo.GetArray(node, "/Kids")?.Elements ?? Enumerable.Empty<PdfItem>())
            n += CountNameTree(PdfIo.Resolve(k) as PdfDictionary, depth + 1);
        return Math.Max(n, node.Elements.Count > 0 && n == 0 ? 1 : n);
    }
}

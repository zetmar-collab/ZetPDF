using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using ZetPDF.Editing.Pdf;
using ZetPDF.Ocr.Pdf;

namespace ZetPDF.Editing.PdfA;

/// <summary>Czcionka użyta do rysowania: dany słownik i strony (0-based), na których działa.</summary>
internal sealed record PdfAFontUse(PdfDictionary Font, string Name, HashSet<int> Pages);

/// <summary>Fakty o dokumencie potrzebne regułom PDF/A-2b. Zbierane tylko do odczytu; poprawki robi <see cref="PdfAFixer"/>.</summary>
internal sealed class PdfAFacts
{
    public bool HeaderOk, BinaryCommentOk, DataAfterEof, HasId, Encrypted;
    public bool HasXmp, XmpFiltered, XmpUnreadable;
    public string? PdfaPart, PdfaConformance;
    public bool HasOutputIntent, IntentProfileValid;
    public int IntentComponents;
    public Dictionary<PdfDictionary, PdfAFontUse> Fonts = new(ReferenceEqualityComparer.Instance);
    public HashSet<int> CmykPages = new(), RgbPages = new(), PostScriptPages = new(), UnreadablePages = new(), WidgetsWithoutAppearance = new();
    public int JavaScript, ForbiddenActions, CatalogAdditionalActions;
    public List<string> ActionNames = new();
    public int MultimediaAnnotations, RenditionNames, AlternatePresentations;
    public int Attachments, AttachmentAnnotations;
    public int AnnotationsNoAppearance, AnnotationsHidden, AnnotationsBadFlags, AnnotationsExtraAppearance;
    public bool HasXfa, NeedAppearances, Signed, Transparency;
    public int ImagesInterpolate, XObjectsForbiddenKeys, GraphicsStateProblems;
    public int LzwStreams, ExternalStreams, OptionalContentProblems, CatalogForbiddenKeys, OtherMetadata;
    public List<int> BadPageSizes = new();
}

/// <summary>Odczyt faktów: bajty pliku, struktura przez PDFsharp, operatory treści stron i formularzy.</summary>
internal static class PdfAInspector
{
    public static readonly HashSet<string> MultimediaAnnotationTypes = new() { "/3D", "/Sound", "/Screen", "/Movie", "/RichMedia" };
    public static readonly HashSet<string> BlendModes = new() { "/Normal", "/Compatible", "/Multiply", "/Screen", "/Overlay", "/Darken", "/Lighten", "/ColorDodge", "/ColorBurn", "/HardLight", "/SoftLight", "/Difference", "/Exclusion", "/Hue", "/Saturation", "/Color", "/Luminosity" };
    public static readonly string[] CatalogForbiddenKeys = { "/Requirements", "/NeedsRendering", "/Collection" };

    /// <summary>Bajty pliku: nagłówek, komentarz binarny, koniec pliku, /ID i /Encrypt w ostatnim trailerze.</summary>
    public static PdfAFacts ReadFileLevel(byte[] pdf)
    {
        var f = new PdfAFacts();
        f.HeaderOk = pdf.Length > 8 && Encoding.ASCII.GetString(pdf, 0, 7) == "%PDF-1." && pdf[7] is >= (byte)'0' and <= (byte)'7';
        int lineEnd = Array.FindIndex(pdf, 0, Math.Min(pdf.Length, 64), b => b is 10 or 13);
        if (lineEnd > 0)
        {
            int p = lineEnd + 1;
            while (p < pdf.Length && pdf[p] is 10 or 13) p++;
            f.BinaryCommentOk = p + 4 < pdf.Length && pdf[p] == (byte)'%' && pdf.Skip(p + 1).Take(4).All(b => b >= 128);
        }
        var text = Encoding.Latin1.GetString(pdf);
        int eof = text.LastIndexOf("%%EOF", StringComparison.Ordinal);
        if (eof >= 0) f.DataAfterEof = text.AsSpan(eof + 5).Trim("\r\n").Length > 0;
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (sx >= 0)
        {
            var m = Regex.Match(text[sx..], @"startxref\s+(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int at) && at >= 0 && at < text.Length)
            {
                string dict;
                if (string.CompareOrdinal(text, at, "xref", 0, 4) == 0)
                {
                    int tr = text.IndexOf("trailer", at, StringComparison.Ordinal);
                    int end = tr < 0 ? -1 : text.IndexOf("startxref", tr, StringComparison.Ordinal);
                    dict = tr < 0 || end < 0 ? string.Empty : text[tr..end];
                }
                else
                {
                    int end = text.IndexOf("stream", at, StringComparison.Ordinal);
                    dict = end < 0 ? string.Empty : text[at..end];
                }
                f.HasId = Regex.IsMatch(dict, @"/ID\s*\[");
                f.Encrypted = Regex.IsMatch(dict, @"/Encrypt\b");
            }
        }
        return f;
    }

    public static void InspectDocument(PdfDocument doc, PdfAFacts f, CancellationToken ct)
    {
        var catalog = doc.Internals.Catalog;
        ReadXmp(catalog, f);
        ReadIntent(catalog, f);
        ReadCatalog(catalog, f);
        ReadActions(doc, f);
        var walker = new ContentScan(f);
        for (int i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            walker.Page(doc.Pages[i], i);
            ReadAnnotations(doc.Pages[i], i, f, walker);
            ReadPage(doc.Pages[i], i, f);
        }
        ReadAllStreams(doc, f);
    }

    private static void ReadXmp(PdfDictionary catalog, PdfAFacts f)
    {
        if (PdfObjects.Resolve(catalog.Elements["/Metadata"]) is not PdfDictionary meta || meta.Stream is null) return;
        f.HasXmp = true;
        f.XmpFiltered = meta.Elements.ContainsKey("/Filter");
        try
        {
            var (part, conformance) = ReadPdfaId(PageContent.Decoded(meta));
            f.PdfaPart = part;
            f.PdfaConformance = conformance;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException) { f.XmpUnreadable = true; }
    }

    internal static (string? Part, string? Conformance) ReadPdfaId(byte[] xmp)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(xmp), settings);
        var x = XDocument.Load(reader);
        XNamespace ns = PdfAMetadata.PdfaIdNamespace;
        string? Value(string name) =>
            x.Descendants(ns + name).FirstOrDefault()?.Value.Trim() ?? x.Descendants().Attributes(ns + name).FirstOrDefault()?.Value.Trim();
        return (Value("part"), Value("conformance"));
    }

    private static void ReadIntent(PdfDictionary catalog, PdfAFacts f)
    {
        if (PdfObjects.Resolve(catalog.Elements["/OutputIntents"]) is not PdfArray arr) return;
        foreach (var e in arr.Elements)
        {
            if (PdfObjects.Resolve(e) is not PdfDictionary d || d.Elements.GetName("/S") != "/GTS_PDFA1") continue;
            f.HasOutputIntent = true;
            if (PdfObjects.Resolve(d.Elements["/DestOutputProfile"]) is PdfDictionary profile && profile.Stream is not null)
            {
                f.IntentComponents = profile.Elements.GetInteger("/N");
                f.IntentProfileValid = IccProfile.IsValid(PageContent.Decoded(profile), f.IntentComponents);
            }
        }
    }

    private static void ReadCatalog(PdfDictionary catalog, PdfAFacts f)
    {
        f.CatalogForbiddenKeys = CatalogForbiddenKeys.Count(catalog.Elements.ContainsKey);
        if (PdfObjects.Resolve(catalog.Elements["/AcroForm"]) is PdfDictionary acro)
        {
            f.HasXfa = acro.Elements.ContainsKey("/XFA");
            f.NeedAppearances = acro.Elements.GetBoolean("/NeedAppearances");
            f.Signed = (acro.Elements.GetInteger("/SigFlags") & 1) != 0;
        }
        if (PdfObjects.Resolve(catalog.Elements["/Names"]) is PdfDictionary names)
        {
            f.Attachments = CountNames(names.Elements["/EmbeddedFiles"]);
            f.RenditionNames = CountNames(names.Elements["/Renditions"]);
            if (names.Elements.ContainsKey("/AlternatePresentations")) f.AlternatePresentations = 1;
        }
        if (PdfObjects.Resolve(catalog.Elements["/OCProperties"]) is PdfDictionary oc)
        {
            void Config(PdfDictionary c) { if (!c.Elements.ContainsKey("/Name") || c.Elements.ContainsKey("/AS")) f.OptionalContentProblems++; }
            if (PdfObjects.Resolve(oc.Elements["/D"]) is PdfDictionary d) Config(d);
            if (PdfObjects.Resolve(oc.Elements["/Configs"]) is PdfArray cs)
                foreach (var c in cs.Elements) if (PdfObjects.Resolve(c) is PdfDictionary cd) Config(cd);
        }
    }

    /// <summary>Liczba wpisów drzewa nazw (/Names + /Kids, odporne na cykle).</summary>
    internal static int CountNames(PdfItem? tree)
    {
        int count = 0;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem?>();
        stack.Push(tree);
        while (stack.Count > 0)
        {
            if (PdfObjects.Resolve(stack.Pop()) is not PdfDictionary d || !seen.Add(d)) continue;
            if (PdfObjects.Resolve(d.Elements["/Names"]) is PdfArray n) count += n.Elements.Count / 2;
            if (PdfObjects.Resolve(d.Elements["/Kids"]) is PdfArray k) foreach (var e in k.Elements) stack.Push(e);
        }
        return count;
    }

    /// <summary>Akcje we wszystkich słownikach dokumentu (<see cref="PdfAActions"/>). Każda liczona raz.</summary>
    private static void ReadActions(PdfDocument doc, PdfAFacts f)
    {
        var fields = PdfAActions.FieldTree(doc);
        var counted = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Report(PdfDictionary action, bool always)
        {
            if ((!always && PdfAActions.IsAllowed(action)) || !counted.Add(action)) return;
            var s = action.Elements.GetName("/S");
            if (s == "/JavaScript") f.JavaScript++;
            else { f.ForbiddenActions++; if (f.ActionNames.Count < 8) f.ActionNames.Add(s.Length > 0 ? s.TrimStart('/') : "?"); }
        }
        foreach (var d in PdfATree.Dictionaries(doc))
        {
            if (d.Elements.ContainsKey("/AA"))
            {
                f.CatalogAdditionalActions++;
                if (PdfObjects.Resolve(d.Elements["/AA"]) is PdfDictionary triggers)
                    foreach (var key in triggers.Elements.Keys)
                        foreach (var (_, action) in PdfAActions.Chain(triggers.Elements[key]!)) Report(action, false);
            }
            bool field = PdfAActions.IsField(d, fields);
            foreach (var key in PdfAActions.PlaceKeys)
                foreach (var start in PdfAActions.ActionsAt(d, key))
                    foreach (var (_, action) in PdfAActions.Chain(start)) Report(action, field && key == "/A"); // pole nie może mieć /A żadnego typu
        }
        // Drzewo nazw jest miejscem na skrypty także wtedy, gdy nie da się ich rozpoznać.
        if (PdfObjects.Resolve(doc.Internals.Catalog.Elements["/Names"]) is PdfDictionary names && names.Elements.ContainsKey("/JavaScript"))
            f.JavaScript += Math.Max(1, CountNames(names.Elements["/JavaScript"]));
    }

    private static void ReadPage(PdfPage page, int index, PdfAFacts f)
    {
        if (page.Elements.ContainsKey("/PresSteps")) f.AlternatePresentations++;
        if (page.Elements.ContainsKey("/Metadata")) f.OtherMetadata++;
        var box = PdfObjects.Rect(page.Elements["/MediaBox"]);
        if (box is { } b && (b.Width < 3 || b.Height < 3 || b.Width > 14400 || b.Height > 14400)) f.BadPageSizes.Add(index);
        if (PdfObjects.Resolve(page.Elements["/Group"]) is PdfDictionary g && g.Elements.GetName("/S") == "/Transparency")
        {
            f.Transparency = true;
            if (g.Elements.GetName("/CS") == "/DeviceCMYK") f.CmykPages.Add(index);
        }
    }

    private static void ReadAnnotations(PdfPage page, int index, PdfAFacts f, ContentScan walker)
    {
        foreach (var a in PdfObjects.Annotations(page))
        {
            var subtype = PdfObjects.Subtype(a);
            if (MultimediaAnnotationTypes.Contains(subtype)) { f.MultimediaAnnotations++; continue; }
            if (subtype == "/FileAttachment") { f.AttachmentAnnotations++; continue; }
            if (subtype == "/Popup") continue;
            int flags = a.Elements.GetInteger("/F");
            if ((flags & (1 | 2 | 32)) != 0) { f.AnnotationsHidden++; continue; }
            if ((flags & 4) == 0 || (flags & 256) != 0) f.AnnotationsBadFlags++;
            if (subtype == "/Link") continue;
            var rect = PdfObjects.Rect(a.Elements["/Rect"]);
            bool empty = rect is { } r && (r.Width <= 0 || r.Height <= 0);
            var ap = PdfObjects.Resolve(a.Elements["/AP"]) as PdfDictionary;
            if (ap is null || !ap.Elements.ContainsKey("/N"))
            {
                // Pole bez wyglądu może mieć wartość: nie wolno go usunąć, a PDFium narysuje je na obrazie strony.
                if (!empty && subtype == "/Widget") f.WidgetsWithoutAppearance.Add(index);
                else if (!empty) f.AnnotationsNoAppearance++;
                continue;
            }
            if (ap.Elements.ContainsKey("/D") || ap.Elements.ContainsKey("/R")) f.AnnotationsExtraAppearance++;
            walker.Appearance(ap.Elements["/N"], index);
        }
    }

    private static void ReadAllStreams(PdfDocument doc, PdfAFacts f)
    {
        foreach (var o in doc.Internals.GetAllObjects())
        {
            if (o is not PdfDictionary d || d.Stream is null) continue;
            if (d.Elements.ContainsKey("/F") || d.Elements.ContainsKey("/FFilter")) f.ExternalStreams++;
            var filter = PdfObjects.Resolve(d.Elements["/Filter"]);
            bool lzw = filter is PdfName { Value: "/LZWDecode" } || (filter is PdfArray arr && arr.Elements.OfType<PdfName>().Any(n => n.Value == "/LZWDecode"));
            if (lzw) f.LzwStreams++;
        }
    }

    /// <summary>Operatory treści i zasoby stron, formularzy i wzorów. Wynik formularza jest pamiętany, bo dokumenty współdzielą zasoby.</summary>
    private sealed class ContentScan
    {
        private readonly PdfAFacts _f;
        /// <summary>Największe zagnieżdżenie formularzy i wzorów; głębiej treść liczy się jako nieczytelna.</summary>
        private const int MaxFormDepth = 32;
        private readonly Dictionary<PdfDictionary, Use> _forms = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfDictionary> _active = new(ReferenceEqualityComparer.Instance);

        private sealed class Use
        {
            public readonly HashSet<PdfDictionary> Fonts = new(ReferenceEqualityComparer.Instance);
            public bool Cmyk, Rgb, PostScript, Unreadable;
            public void Add(Use o) { Fonts.UnionWith(o.Fonts); Cmyk |= o.Cmyk; Rgb |= o.Rgb; PostScript |= o.PostScript; Unreadable |= o.Unreadable; }
        }

        public ContentScan(PdfAFacts f) => _f = f;

        public void Page(PdfPage page, int index)
        {
            var use = new Use();
            var resources = PdfObjects.Resolve(page.Elements["/Resources"]) as PdfDictionary;
            Content(PageContent.ReadAll(page), resources, use);
            Resources(resources, use);
            Publish(use, index);
        }

        public void Appearance(PdfItem? normal, int index)
        {
            var use = new Use();
            var node = PdfObjects.Resolve(normal) as PdfDictionary;
            if (node is null) return;
            if (node.Stream is not null) use.Add(Form(node, null));
            else foreach (var key in node.Elements.Keys)
                if (PdfObjects.Resolve(node.Elements[key]) is PdfDictionary state && state.Stream is not null) use.Add(Form(state, null));
            Publish(use, index);
        }

        private void Publish(Use use, int index)
        {
            foreach (var font in use.Fonts)
            {
                if (!_f.Fonts.TryGetValue(font, out var entry))
                    _f.Fonts[font] = entry = new PdfAFontUse(font, PdfAFontEmbedder.BaseName(font), new HashSet<int>());
                entry.Pages.Add(index);
            }
            if (use.Cmyk) _f.CmykPages.Add(index);
            if (use.Rgb) _f.RgbPages.Add(index);
            if (use.PostScript) _f.PostScriptPages.Add(index);
            if (use.Unreadable) _f.UnreadablePages.Add(index);
        }

        private Use Form(PdfDictionary form, PdfDictionary? parentResources)
        {
            // Cykl albo zbyt głębokie zagnieżdżenie: nie awaria, tylko strona, której nie da się sprawdzić.
            if (_active.Contains(form) || _active.Count >= MaxFormDepth) return new Use { Unreadable = true };
            if (_forms.TryGetValue(form, out var known)) return known;
            var use = new Use();
            _forms[form] = use;
            _active.Add(form);
            try { ReadForm(form, parentResources, use); }
            finally { _active.Remove(form); }
            return use;
        }

        private void ReadForm(PdfDictionary form, PdfDictionary? parentResources, Use use)
        {
            if (form.Elements.ContainsKey("/Ref") || form.Elements.ContainsKey("/OPI") || form.Elements.ContainsKey("/PS") || form.Elements.ContainsKey("/Alternates")) _f.XObjectsForbiddenKeys++;
            if (PdfObjects.Resolve(form.Elements["/Group"]) is PdfDictionary g && g.Elements.GetName("/S") == "/Transparency") _f.Transparency = true;
            var resources = PdfObjects.Resolve(form.Elements["/Resources"]) as PdfDictionary ?? parentResources;
            byte[] data;
            try { data = PageContent.Decoded(form); }
            catch (Exception) { use.Unreadable = true; return; }
            Content(data, resources, use);
            if (!ReferenceEquals(resources, parentResources)) Resources(resources, use);
        }

        private void Resources(PdfDictionary? res, Use use)
        {
            if (res is null) return;
            foreach (var d in Entries(res, "/ExtGState"))
            {
                var bm = PdfObjects.Resolve(d.Elements["/BM"]);
                if (bm is PdfName n && !BlendModes.Contains(n.Value)) _f.GraphicsStateProblems++;
                if (d.Elements.ContainsKey("/TR") || d.Elements.ContainsKey("/HTP")) _f.GraphicsStateProblems++;
                if (d.Elements.ContainsKey("/TR2") && d.Elements.GetName("/TR2") != "/Default") _f.GraphicsStateProblems++;
                if (bm is PdfName { Value: not ("/Normal" or "/Compatible") } || (d.Elements.ContainsKey("/SMask") && d.Elements.GetName("/SMask") != "/None") ||
                    (d.Elements.ContainsKey("/ca") && PdfObjects.Number(d.Elements["/ca"]) < 1) || (d.Elements.ContainsKey("/CA") && PdfObjects.Number(d.Elements["/CA"]) < 1))
                    _f.Transparency = true;
                if (PdfObjects.Resolve(d.Elements["/SMask"]) is PdfDictionary mask && PdfObjects.Resolve(mask.Elements["/G"]) is PdfDictionary maskForm && maskForm.Stream is not null) use.Add(Form(maskForm, res));
                if (PdfObjects.Resolve(d.Elements["/Font"]) is PdfArray fa && fa.Elements.Count > 0 && PdfObjects.Resolve(fa.Elements[0]) is PdfDictionary font) use.Fonts.Add(font);
            }
            if (PdfObjects.Resolve(res.Elements["/ColorSpace"]) is PdfDictionary spaces)
                foreach (var key in spaces.Elements.Keys) ColorSpace(spaces.Elements[key], use);
            foreach (var d in Entries(res, "/Shading")) ColorSpace(d.Elements["/ColorSpace"], use);
            foreach (var d in Entries(res, "/Pattern"))
            {
                if (d.Stream is not null) use.Add(Form(d, res));
                else if (PdfObjects.Resolve(d.Elements["/Shading"]) is PdfDictionary sh) ColorSpace(sh.Elements["/ColorSpace"], use);
            }
            foreach (var d in Entries(res, "/XObject"))
            {
                var subtype = d.Elements.GetName("/Subtype");
                if (subtype == "/PS" || d.Elements.GetName("/Subtype2") == "/PS") { use.PostScript = true; continue; }
                if (subtype == "/Form") { use.Add(Form(d, res)); continue; }
                if (subtype != "/Image") continue;
                ColorSpace(d.Elements["/ColorSpace"], use);
                if (d.Elements.GetBoolean("/Interpolate")) _f.ImagesInterpolate++;
                if (d.Elements.ContainsKey("/OPI") || d.Elements.ContainsKey("/Alternates")) _f.XObjectsForbiddenKeys++;
                if (d.Elements.ContainsKey("/SMask")) _f.Transparency = true;
            }
        }

        private static IEnumerable<PdfDictionary> Entries(PdfDictionary res, string key)
        {
            if (PdfObjects.Resolve(res.Elements[key]) is not PdfDictionary dict) yield break;
            foreach (var k in dict.Elements.Keys)
                if (PdfObjects.Resolve(dict.Elements[k]) is PdfDictionary d) yield return d;
        }

        private static void ColorSpace(PdfItem? item, Use use, int depth = 0)
        {
            if (depth > 8) { use.Unreadable = true; return; }
            switch (PdfObjects.Resolve(item))
            {
                case PdfName n:
                    if (n.Value is "/DeviceCMYK" or "/CMYK") use.Cmyk = true;
                    else if (n.Value is "/DeviceRGB" or "/RGB") use.Rgb = true;
                    break;
                case PdfArray a when a.Elements.Count > 0:
                    var kind = (PdfObjects.Resolve(a.Elements[0]) as PdfName)?.Value;
                    if (kind is "/Separation" or "/DeviceN" && a.Elements.Count > 2) ColorSpace(a.Elements[2], use, depth + 1);
                    else if (kind is "/Indexed" or "/I" or "/Pattern" && a.Elements.Count > 1) ColorSpace(a.Elements[1], use, depth + 1);
                    break;
            }
        }

        /// <summary>Operatory: kolor urządzenia i czcionki faktycznie rysujące (tryb tekstu inny niż 3; q/Q zachowują stan).</summary>
        private static void Content(byte[] data, PdfDictionary? resources, Use use)
        {
            var fonts = resources is null ? null : PdfObjects.Resolve(resources.Elements["/Font"]) as PdfDictionary;
            var state = new Stack<(string? Font, int Tr)>();
            string? font = null;
            int tr = 0;
            try
            {
                foreach (var op in ZetPDF.Core.Documents.PdfContentParser.Parse(data).OfType<COperator>())
                {
                    switch (op.OpCode.Name)
                    {
                        case "q": state.Push((font, tr)); break;
                        case "Q": if (state.Count > 0) (font, tr) = state.Pop(); break;
                        case "Tf" when op.Operands.Count > 0 && op.Operands[0] is CName n: font = n.Name; break;
                        case "Tr" when op.Operands.Count > 0 && op.Operands[0] is CInteger i: tr = i.Value; break;
                        case "Tj" or "TJ" or "'" or "\"":
                            if (tr != 3 && font is not null && fonts is not null && PdfObjects.Resolve(fonts.Elements[font]) is PdfDictionary fd) use.Fonts.Add(fd);
                            break;
                        case "k" or "K": use.Cmyk = true; break;
                        case "rg" or "RG": use.Rgb = true; break;
                        case "cs" or "CS" when op.Operands.Count > 0 && op.Operands[0] is CName cn:
                            if (cn.Name == "/DeviceCMYK") use.Cmyk = true;
                            else if (cn.Name == "/DeviceRGB") use.Rgb = true;
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Treści nie da się odczytać: wszystko z zasobów traktujemy jako użyte, a stronę jako wymagającą rasteryzacji.
                use.Unreadable = true;
                if (fonts is not null)
                    foreach (var k in fonts.Elements.Keys)
                        if (PdfObjects.Resolve(fonts.Elements[k]) is PdfDictionary fd) use.Fonts.Add(fd);
            }
        }
    }
}

/// <summary>Wszystkie słowniki dokumentu: obiekty pośrednie i słowniki w nich zagnieżdżone. Iteracyjnie i ze zbiorem odwiedzonych, więc głębokość ani cykle nie są groźne.</summary>
internal static class PdfATree
{
    public static List<PdfDictionary> Dictionaries(PdfDocument doc)
    {
        var result = new List<PdfDictionary>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfItem>(doc.Internals.GetAllObjects());
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (!seen.Add(item)) continue;
            if (item is PdfDictionary d)
            {
                result.Add(d);
                foreach (var key in d.Elements.Keys)
                    if (d.Elements[key] is PdfDictionary or PdfArray) stack.Push(d.Elements[key]!);
            }
            else if (item is PdfArray a)
            {
                foreach (var e in a.Elements)
                    if (e is PdfDictionary or PdfArray) stack.Push(e);
            }
        }
        return result;
    }
}

/// <summary>Minimalne sprawdzenie profilu ICC: nagłówek, sygnatura, przestrzeń zgodna z liczbą składowych.</summary>
internal static class IccProfile
{
    public static bool IsValid(byte[] data, int components)
    {
        if (data.Length < 128 || Encoding.ASCII.GetString(data, 36, 4) != "acsp") return false;
        var space = Encoding.ASCII.GetString(data, 16, 4);
        return (components, space) is (3, "RGB ") or (4, "CMYK") or (1, "GRAY") or (3, "Lab ");
    }

    public static string SystemProfilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color", "sRGB Color Space Profile.icm");

    /// <summary>Profil sRGB z Windows, czytany w czasie działania (nie jest dołączany do programu). Null, gdy go nie ma lub jest niepoprawny.</summary>
    public static byte[]? ReadSystemSrgb()
    {
        try
        {
            var data = File.ReadAllBytes(SystemProfilePath);
            return IsValid(data, 3) ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

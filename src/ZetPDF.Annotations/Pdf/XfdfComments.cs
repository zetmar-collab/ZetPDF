using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Komentarze w XFDF (Adobe XFDF 3.0, przestrzeń http://ns.adobe.com/xfdf/). Atrybuty: page (0-based), rect "l,b,r,t",
/// color "#RRGGBB", name (= /NM), title (autor), subject, date/creationdate (format PDF), opacity, flags, width,
/// coords (QuadPoints), inreplyto/replyType, state/statemodel; elementy contents, vertices, inklist/gesture,
/// defaultappearance, defaultstyle. Dane prywatne ZetPDF w przestrzeni urn:zetpdf (ignorowane przez inne programy).
/// </summary>
internal static class XfdfComments
{
    public static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";
    public static readonly XNamespace Zet = "urn:zetpdf";

    private static readonly Dictionary<string, string> SubtypeToElement = new(StringComparer.Ordinal)
    {
        ["/Text"] = "text", ["/FreeText"] = "freetext", ["/Line"] = "line", ["/Square"] = "square", ["/Circle"] = "circle",
        ["/Polygon"] = "polygon", ["/PolyLine"] = "polyline", ["/Highlight"] = "highlight", ["/Underline"] = "underline",
        ["/Squiggly"] = "squiggly", ["/StrikeOut"] = "strikeout", ["/Stamp"] = "stamp", ["/Ink"] = "ink",
        ["/FileAttachment"] = "fileattachment", ["/Caret"] = "caret", ["/Redact"] = "redact",
    };

    private static string Nums(IEnumerable<double> v) => string.Join(",", v.Select(Num));

    // ================================================================== eksport

    public static XDocument Export(PdfDocument doc, string? sourceFileName)
    {
        var annotsEl = new XElement(Ns + "annots");
        var all = AnnotationStore.All(doc).ToList();
        var nameOf = new Dictionary<PdfDictionary, string>(ReferenceEqualityComparer.Instance);
        foreach (var a in all) nameOf[a.Dict] = a.Id.Name;
        foreach (var a in all)
        {
            var d = a.Dict;
            var subtype = NameOf(d, "/Subtype") ?? string.Empty;
            if (!SubtypeToElement.TryGetValue(subtype, out var elName)) continue;
            var m = AnnotModel.Read(d);
            var el = new XElement(Ns + elName);
            el.SetAttributeValue("page", a.PageIndex);
            el.SetAttributeValue("rect", Nums(new[] { m.Rect.Left, m.Rect.Bottom, m.Rect.Right, m.Rect.Top }));
            el.SetAttributeValue("name", a.Id.Name);
            Attr(el, "title", Text(d, "/T"));
            Attr(el, "subject", Text(d, "/Subj"));
            Attr(el, "date", Text(d, "/M"));
            Attr(el, "creationdate", Text(d, "/CreationDate"));
            el.SetAttributeValue("flags", FlagsText(Int(d, "/F")));
            if (PdfLow.Color(d, "/C") is { } c) el.SetAttributeValue("color", Hex(c));
            if (d.Elements.ContainsKey("/CA")) el.SetAttributeValue("opacity", Num(Number(d, "/CA", 1)));
            if (Dict(d, "/BS") is { } bs && bs.Elements.ContainsKey("/W")) el.SetAttributeValue("width", Num(Number(bs, "/W", 1)));
            if (PdfLow.Color(d, "/IC") is { } ic) el.SetAttributeValue("interior-color", Hex(ic));
            if (NameOf(d, "/Name") is { } icon) el.SetAttributeValue("icon", icon.TrimStart('/'));
            if (Dict(d, "/IRT") is { } parent && nameOf.TryGetValue(parent, out var pn))
            {
                el.SetAttributeValue("inreplyto", pn);
                el.SetAttributeValue("replyType", NameOf(d, "/RT") == "/Group" ? "group" : "reply");
            }
            if (d.Elements.ContainsKey("/State")) Attr(el, "state", Text(d, "/State"));
            if (d.Elements.ContainsKey("/StateModel")) Attr(el, "statemodel", Text(d, "/StateModel"));
            if (m.Quads.Count > 0) el.SetAttributeValue("coords", Nums(Numbers(ArrayOf(d, "/QuadPoints"))));
            switch (subtype)
            {
                case "/Line":
                    if (m.Points.Count >= 2)
                    {
                        el.SetAttributeValue("start", Nums(new[] { m.Points[0].X, m.Points[0].Y }));
                        el.SetAttributeValue("end", Nums(new[] { m.Points[1].X, m.Points[1].Y }));
                    }
                    if (ArrayOf(d, "/LE") is { Elements.Count: 2 } le)
                    {
                        el.SetAttributeValue("head", TextOf(le.Elements[0]));
                        el.SetAttributeValue("tail", TextOf(le.Elements[1]));
                    }
                    break;
                case "/Polygon" or "/PolyLine":
                    el.Add(new XElement(Ns + "vertices", string.Join(";", m.Points.Select(p => Num(p.X) + "," + Num(p.Y)))));
                    break;
                case "/Ink":
                    var inklist = new XElement(Ns + "inklist");
                    foreach (var s in m.Ink) inklist.Add(new XElement(Ns + "gesture", string.Join(";", s.Select(p => Num(p.X) + "," + Num(p.Y)))));
                    el.Add(inklist);
                    break;
                case "/FreeText":
                    Attr(el, "intent", NameOf(d, "/IT")?.TrimStart('/'));
                    if (m.Kind == AnnotationKind.Callout && m.Points.Count > 0) el.SetAttributeValue("callout", Nums(m.Points.SelectMany(p => new[] { p.X, p.Y })));
                    if (ArrayOf(d, "/RD") is { } rd) el.SetAttributeValue("fringe", Nums(Numbers(rd)));
                    el.SetAttributeValue("justification", Int(d, "/Q"));
                    break;
            }
            if (Dict(d, AnnotModel.PrivateKey) is { } priv)
            {
                if (NameOf(priv, "/Kind") is { } k) el.SetAttributeValue(Zet + "kind", k.TrimStart('/'));
                if (NameOf(priv, "/Stamp") is { } sn) el.SetAttributeValue(Zet + "stamp", sn.TrimStart('/'));
                if (ArrayOf(priv, "/Lines") is { } lines) el.SetAttributeValue(Zet + "lines", string.Join("\n", lines.Elements.Select(TextOf)));
                if (PdfLow.Color(priv, "/Border") is { } bc) el.SetAttributeValue(Zet + "border", Hex(bc));
            }
            var contents = Text(d, "/Contents");
            if (contents.Length > 0) el.Add(new XElement(Ns + "contents", contents));
            if (subtype == "/FreeText")
            {
                Element(el, "defaultappearance", Text(d, "/DA"));
                Element(el, "defaultstyle", Text(d, "/DS"));
            }
            if (Dict(d, "/Popup") is { } popup && PdfLow.Rect(popup) is { } pr)
                el.Add(new XElement(Ns + "popup",
                    new XAttribute("rect", Nums(new[] { pr.Left, pr.Bottom, pr.Right, pr.Top })),
                    new XAttribute("open", popup.Elements.GetBoolean("/Open") ? "yes" : "no"),
                    new XAttribute("page", a.PageIndex),
                    new XAttribute("flags", FlagsText(Int(popup, "/F")))));
            annotsEl.Add(el);
        }
        var root = new XElement(Ns + "xfdf",
            new XAttribute(XNamespace.Xml + "space", "preserve"),
            new XAttribute(XNamespace.Xmlns + "zet", Zet.NamespaceName));
        root.Add(annotsEl);
        if (!string.IsNullOrEmpty(sourceFileName)) root.Add(new XElement(Ns + "f", new XAttribute("href", sourceFileName)));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static void Attr(XElement el, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) el.SetAttributeValue(name, value);
    }

    private static void Element(XElement el, string name, string value)
    {
        if (value.Length > 0) el.Add(new XElement(Ns + name, value));
    }

    private static readonly (int Bit, string Name)[] FlagNames =
    {
        (1, "invisible"), (2, "hidden"), (4, "print"), (8, "nozoom"), (16, "norotate"), (32, "noview"), (64, "readonly"), (128, "locked"), (256, "togglenoview"),
    };

    public static string FlagsText(int f) => string.Join(",", FlagNames.Where(x => (f & x.Bit) != 0).Select(x => x.Name));

    public static int ParseFlags(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return AnnotationStore.FlagPrint;
        int f = 0;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var (bit, name) in FlagNames)
                if (string.Equals(name, part, StringComparison.OrdinalIgnoreCase)) f |= bit;
        return f;
    }

    // ================================================================== import

    public static int Import(PdfDocument doc, byte[] data, IAnnotationTexts texts, DateTimeOffset now)
    {
        XDocument x;
        using (var ms = new MemoryStream(data, writable: false))
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(ms, settings);
            x = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        var root = x.Root ?? throw new InvalidDataException("Empty XFDF.");
        var ns = root.Name.Namespace;
        var annots = root.Element(ns + "annots");
        if (annots is null) return 0;

        var byName = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        foreach (var a in AnnotationStore.All(doc)) byName.TryAdd(a.Id.Name, a.Dict);
        var replies = new List<(PdfDictionary Dict, string Parent)>();
        int count = 0;
        foreach (var el in annots.Elements())
        {
            var subtype = SubtypeToElement.FirstOrDefault(kv => kv.Value == el.Name.LocalName).Key;
            if (subtype is null || subtype == "/FileAttachment") continue;
            int page = int.TryParse((string?)el.Attribute("page"), NumberStyles.Integer, Inv, out int pg) ? pg : 0;
            if (page < 0 || page >= doc.PageCount) continue;
            var name = (string?)el.Attribute("name");
            if (!string.IsNullOrEmpty(name) && byName.ContainsKey(name)) continue; // deduplikacja
            var d = BuildDict(doc, el, ns, subtype, page, name, texts, now);
            if (d is null) continue;
            byName[Text(d, "/NM")] = d;
            var parent = (string?)el.Attribute("inreplyto");
            if (!string.IsNullOrEmpty(parent)) replies.Add((d, parent));
            else if (AnnotModel.IsComment(AnnotModel.KindOf(d)))
            {
                AppearanceBuilder.Apply(doc, d, AnnotModel.Read(d));
                if (subtype == "/Text" && el.Element(ns + "popup") is { } pop) AddPopupFrom(doc, d, pop, page);
            }
            count++;
        }
        foreach (var (d, parent) in replies)
        {
            if (byName.TryGetValue(parent, out var p) && p.Reference is not null) d.Elements["/IRT"] = p.Reference;
            AnnotationStore.SetEmptyAppearance(doc, d);
        }
        return count;
    }

    private static double[] ParseNums(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return System.Array.Empty<double>();
        return s.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, Inv, out var v) ? v : 0).ToArray();
    }

    private static PdfDictionary? BuildDict(PdfDocument doc, XElement el, XNamespace ns, string subtype, int pageIndex, string? name,
        IAnnotationTexts texts, DateTimeOffset now)
    {
        var page = doc.Pages[pageIndex];
        var rect = ParseNums((string?)el.Attribute("rect"));
        if (rect.Length < 4) return null;
        var d = AnnotationStore.NewAnnot(doc, page, subtype, string.IsNullOrEmpty(name) ? NewName() : name,
            (string?)el.Attribute("title") ?? texts.DefaultAuthor, now, ParseFlags((string?)el.Attribute("flags")));
        d.Elements["/Rect"] = RectArray(doc, PdfRect.FromPoints(new(rect[0], rect[1]), new(rect[2], rect[3])));
        if ((string?)el.Attribute("date") is { Length: > 0 } date) d.Elements["/M"] = new PdfString(date);
        if ((string?)el.Attribute("creationdate") is { Length: > 0 } cd) d.Elements["/CreationDate"] = new PdfString(cd);
        if ((string?)el.Attribute("subject") is { Length: > 0 } subj) d.Elements["/Subj"] = Str(subj);
        if (ParseHex((string?)el.Attribute("color")) is { } c) d.Elements["/C"] = ColorArray(doc, c);
        if (ParseHex((string?)el.Attribute("interior-color")) is { } ic) d.Elements["/IC"] = ColorArray(doc, ic);
        if ((string?)el.Attribute("opacity") is { } op && double.TryParse(op, NumberStyles.Float, Inv, out var o)) d.Elements["/CA"] = Real(o);
        if ((string?)el.Attribute("width") is { } ws && double.TryParse(ws, NumberStyles.Float, Inv, out var w))
        {
            var bs = NewDict(doc, "/Border");
            bs.Elements["/W"] = Real(w);
            bs.Elements["/S"] = Name("/S");
            d.Elements["/BS"] = bs;
        }
        if ((string?)el.Attribute("icon") is { Length: > 0 } icon) d.Elements["/Name"] = Name("/" + icon);
        if ((string?)el.Attribute("state") is { Length: > 0 } st) d.Elements["/State"] = Str(st);
        if ((string?)el.Attribute("statemodel") is { Length: > 0 } sm) d.Elements["/StateModel"] = Str(sm);
        if ((string?)el.Attribute("replyType") is { } rt && el.Attribute("inreplyto") is not null) d.Elements["/RT"] = Name(rt == "group" ? "/Group" : "/R");
        var contents = el.Element(ns + "contents")?.Value ?? el.Element(ns + "contents-richtext")?.Value;
        if (!string.IsNullOrEmpty(contents)) d.Elements["/Contents"] = Str(contents.Replace("\r\n", "\r").Replace('\n', '\r').Replace("\r", "\n"));
        var coords = ParseNums((string?)el.Attribute("coords"));
        if (coords.Length >= 8)
        {
            var qp = new PdfArray(doc);
            foreach (var v in coords.Take(coords.Length / 8 * 8)) qp.Elements.Add(Real(v));
            d.Elements["/QuadPoints"] = qp;
        }
        switch (subtype)
        {
            case "/Line":
                var s = ParseNums((string?)el.Attribute("start"));
                var e = ParseNums((string?)el.Attribute("end"));
                if (s.Length >= 2 && e.Length >= 2) d.Elements["/L"] = Arr(doc, s[0], s[1], e[0], e[1]);
                var le = new PdfArray(doc);
                le.Elements.Add(Name("/" + ((string?)el.Attribute("head") ?? "None")));
                le.Elements.Add(Name("/" + ((string?)el.Attribute("tail") ?? "None")));
                d.Elements["/LE"] = le;
                break;
            case "/Polygon" or "/PolyLine":
                var v = ParseNums(el.Element(ns + "vertices")?.Value);
                if (v.Length >= 4) d.Elements["/Vertices"] = Arr(doc, v);
                break;
            case "/Ink":
                var list = new PdfArray(doc);
                foreach (var g in el.Element(ns + "inklist")?.Elements(ns + "gesture") ?? Enumerable.Empty<XElement>())
                {
                    var pts = ParseNums(g.Value);
                    if (pts.Length >= 2) list.Elements.Add(Arr(doc, pts));
                }
                d.Elements["/InkList"] = list;
                break;
            case "/FreeText":
                if ((string?)el.Attribute("intent") is { Length: > 0 } it) d.Elements["/IT"] = Name("/" + it);
                var cl = ParseNums((string?)el.Attribute("callout"));
                if (cl.Length >= 4) d.Elements["/CL"] = Arr(doc, cl);
                var fringe = ParseNums((string?)el.Attribute("fringe"));
                if (fringe.Length == 4) d.Elements["/RD"] = Arr(doc, fringe);
                d.Elements["/DA"] = Str(el.Element(ns + "defaultappearance")?.Value is { Length: > 0 } da ? da : "/Helv 12 Tf 0 g");
                if (el.Element(ns + "defaultstyle")?.Value is { Length: > 0 } ds) d.Elements["/DS"] = Str(ds);
                if ((string?)el.Attribute("justification") is { } j && int.TryParse(j, out int q)) d.Elements["/Q"] = new PdfInteger(q);
                // FreeText: w XFDF "color" to kolor tła (/C) – zostawiamy jak jest
                break;
        }
        var priv = NewDict(doc);
        if ((string?)el.Attribute(Zet + "kind") is { Length: > 0 } k) priv.Elements["/Kind"] = Name("/" + k);
        if ((string?)el.Attribute(Zet + "stamp") is { Length: > 0 } sn) priv.Elements["/Stamp"] = Name("/" + sn);
        if ((string?)el.Attribute(Zet + "lines") is { Length: > 0 } lines)
        {
            var arr = new PdfArray(doc);
            foreach (var l in lines.Split('\n')) arr.Elements.Add(Str(l));
            priv.Elements["/Lines"] = arr;
        }
        if (ParseHex((string?)el.Attribute(Zet + "border")) is { } bc) priv.Elements["/Border"] = ColorArray(doc, bc);
        if (subtype == "/Stamp" && !priv.Elements.ContainsKey("/Lines"))
        {
            // stempel z innego programu: wzór ZetPDF o tej samej nazwie albo tekst z nazwy ikony
            var iconName = (string?)el.Attribute("icon") ?? "Draft";
            var def = StampLibrary.Find(iconName);
            var arr = new PdfArray(doc);
            arr.Elements.Add(Str((def is not null ? texts.StampText(def) : SplitCamel(iconName)).ToUpper(CultureInfo.CurrentCulture)));
            priv.Elements["/Lines"] = arr;
            if (def is not null && !d.Elements.ContainsKey("/C")) d.Elements["/C"] = ColorArray(doc, def.Color);
        }
        if (priv.Elements.Count > 0) d.Elements[AnnotModel.PrivateKey] = priv;
        EnsureAnnots(page).Elements.Add(d.Reference!);
        return d;
    }

    private static string SplitCamel(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.TrimStart('#'))
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AddPopupFrom(PdfDocument doc, PdfDictionary parent, XElement pop, int pageIndex)
    {
        var page = doc.Pages[pageIndex];
        var annots = EnsureAnnots(page);
        AnnotationStore.AddPopup(doc, page, parent, annots);
        var r = ParseNums((string?)pop.Attribute("rect"));
        if (Dict(parent, "/Popup") is { } p && r.Length >= 4)
        {
            p.Elements["/Rect"] = RectArray(doc, PdfRect.FromPoints(new(r[0], r[1]), new(r[2], r[3])));
            p.Elements["/Open"] = new PdfBoolean(string.Equals((string?)pop.Attribute("open"), "yes", StringComparison.OrdinalIgnoreCase));
        }
    }
}

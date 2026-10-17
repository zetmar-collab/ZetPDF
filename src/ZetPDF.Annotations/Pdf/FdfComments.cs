using System.Text;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;
using ZetPDF.Core.Localization;

namespace ZetPDF.Annotations.Pdf;

/// <summary>Komentarze w FDF (Adobe FDF 1.2: katalog /FDF z /Annots; każda adnotacja ma /Page).</summary>
internal static class FdfComments
{
    public static byte[] Export(PdfDocument doc, string? sourceFileName)
    {
        var w = new FdfWriter();
        var annots = new List<int>();
        var list = AnnotationStore.All(doc)
            .Where(a => AnnotModel.KindOf(a.Dict) is var k && AnnotModel.IsComment(k))
            .ToList();
        foreach (var a in list) w.Reserve(a.Dict);
        foreach (var a in list)
        {
            int page = a.PageIndex;
            annots.Add(w.WriteObject(a.Dict, (sb, _) => sb.Append("/Page ").Append(page).Append(' ')));
        }
        var body = new StringBuilder("<< /Annots [");
        body.Append(string.Join(" ", annots.Select(n => n + " 0 R")));
        body.Append(']');
        if (!string.IsNullOrEmpty(sourceFileName)) body.Append(" /F ").Append(FdfWriter.String(sourceFileName));
        body.Append(" >>");
        return w.Build(body.ToString());
    }

    public static int Import(PdfDocument doc, byte[] data, IAnnotationTexts texts, DateTimeOffset now)
    {
        var fdf = FdfParser.Parse(data);
        var root = fdf.FdfRoot ?? throw new InvalidDataException("Not an FDF file (missing /FDF dictionary).").WithMessageKey("Annot.Error.NotFdf");
        var annotsArr = fdf.ResolveArray(root["/Annots"]);
        if (annotsArr is null) return 0;
        var existing = new HashSet<string>(AnnotationStore.All(doc).Select(a => a.Id.Name), StringComparer.Ordinal);
        var converter = new FdfToPdf(doc, fdf);
        var created = new Dictionary<FdfDict, PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new List<(FdfDict Src, PdfDictionary Dst)>();
        int count = 0;
        foreach (var item in annotsArr.Items)
        {
            if (fdf.ResolveDict(item) is not { } src) continue;
            int page = src["/Page"] is FdfNumber pn ? (int)pn.Value : 0;
            if (page < 0 || page >= doc.PageCount) continue;
            var nm = fdf.Text(src["/NM"]);
            if (nm.Length > 0 && existing.Contains(nm)) continue; // deduplikacja
            var d = converter.Dict(src, skip: new[] { "/Page", "/IRT", "/Popup", "/Parent", "/P" });
            if (nm.Length == 0) d.Elements["/NM"] = Str(NewName());
            var pg = doc.Pages[page];
            if (pg.Reference is not null) d.Elements["/P"] = pg.Reference;
            doc.Internals.AddObject(d);
            EnsureAnnots(pg).Elements.Add(d.Reference!);
            created[src] = d;
            pending.Add((src, d));
            existing.Add(Text(d, "/NM"));
            count++;
            var kind = AnnotModel.KindOf(d);
            if (AnnotModel.IsComment(kind) && Dict(d, "/AP") is null && !d.Elements.ContainsKey("/IRT"))
                AppearanceBuilder.Apply(doc, d, AnnotModel.Read(d));
        }
        foreach (var (src, dst) in pending)
        {
            if (fdf.ResolveDict(src["/IRT"]) is { } parentSrc && created.TryGetValue(parentSrc, out var parentDst))
            {
                dst.Elements["/IRT"] = parentDst.Reference!;
                if (Dict(dst, "/AP") is null) AnnotationStore.SetEmptyAppearance(doc, dst);
            }
            else if (AnnotModel.KindOf(dst) == AnnotationKind.Note && Dict(dst, "/AP") is null)
                AppearanceBuilder.Apply(doc, dst, AnnotModel.Read(dst));
        }
        return count;
    }
}

/// <summary>Konwersja wartości FDF na obiekty PDFsharp (strumienie → obiekty pośrednie).</summary>
internal sealed class FdfToPdf
{
    private readonly PdfDocument _doc;
    private readonly FdfDocument _fdf;
    private readonly Dictionary<int, PdfObject> _done = new();

    public FdfToPdf(PdfDocument doc, FdfDocument fdf)
    {
        _doc = doc;
        _fdf = fdf;
    }

    public PdfDictionary Dict(FdfDict src, IEnumerable<string>? skip = null)
    {
        var set = skip is null ? null : new HashSet<string>(skip, StringComparer.Ordinal);
        var d = new PdfDictionary(_doc);
        foreach (var (k, v) in src.Items)
        {
            if (set is not null && set.Contains(k)) continue;
            if (src.Stream is not null && k == "/Length") continue;
            d.Elements[k] = Item(v);
        }
        if (src.Stream is not null) d.CreateStream(src.Stream);
        return d;
    }

    public PdfItem Item(FdfValue v, int depth = 0)
    {
        if (depth > 40) return PdfNull.Value;
        switch (v)
        {
            case FdfRef r:
                if (_done.TryGetValue(r.Number, out var done)) return done.Reference!;
                var target = _fdf.Resolve(r);
                if (target is FdfDict td)
                {
                    var d = new PdfDictionary(_doc);
                    _doc.Internals.AddObject(d);
                    _done[r.Number] = d;
                    var filled = Dict(td, td.Stream is not null ? null : new[] { "/P", "/Parent" });
                    foreach (var key in filled.Elements.Keys.ToList()) d.Elements[key] = filled.Elements[key];
                    if (td.Stream is not null) d.CreateStream(td.Stream);
                    return d.Reference!;
                }
                return target is null ? PdfNull.Value : Item(target, depth + 1);
            case FdfDict dd: return Dict(dd);
            case FdfArray a:
                var arr = new PdfArray(_doc);
                foreach (var e in a.Items) arr.Elements.Add(Item(e, depth + 1));
                return arr;
            case FdfName n: return new PdfName(n.Value);
            case FdfString s: return Str(s.Text);
            case FdfNumber num:
                return Math.Abs(num.Value - Math.Round(num.Value)) < 1e-9 && Math.Abs(num.Value) < int.MaxValue ? new PdfInteger((int)num.Value) : new PdfReal(num.Value);
            case FdfBool b: return new PdfBoolean(b.Value);
            default: return PdfNull.Value;
        }
    }
}

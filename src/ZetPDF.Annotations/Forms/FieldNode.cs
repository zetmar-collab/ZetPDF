using PdfSharp.Pdf;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Forms;

/// <summary>Pole końcowe AcroForm z widżetami (słownik pola może być scalony z widżetem).</summary>
internal sealed class FieldNode
{
    public const int FfReadOnly = 1;
    public const int FfRequired = 1 << 1;
    public const int FfMultiline = 1 << 12;
    public const int FfNoToggleToOff = 1 << 14;
    public const int FfRadio = 1 << 15;
    public const int FfPushButton = 1 << 16;
    public const int FfCombo = 1 << 17;
    public const int FfEdit = 1 << 18;
    public const int FfMultiSelect = 1 << 21;

    public required PdfDictionary Dict { get; init; }
    public required string FullName { get; init; }
    public PdfDictionary? Parent { get; init; }
    public List<PdfDictionary> Widgets { get; } = new();

    /// <summary>Wartość dziedziczona (pole lub przodkowie).</summary>
    public PdfItem? Inherited(string key)
    {
        var d = Dict;
        for (int depth = 0; d is not null && depth < 32; depth++)
        {
            if (d.Elements.ContainsKey(key)) return Get(d, key);
            d = PdfLow.Dict(d, "/Parent");
        }
        return null;
    }

    public string FieldType => (Inherited("/FT") as PdfName)?.Value ?? string.Empty;
    public int Flags => (int)Number(Inherited("/Ff"));

    public string DateFormat => PdfLow.Dict(Dict, AnnotModel.PrivateKey) is { } p ? Text(p, "/DateFormat") : string.Empty;

    public FormFieldKind Kind
    {
        get
        {
            int ff = Flags;
            return FieldType switch
            {
                "/Btn" => (ff & FfPushButton) != 0 ? FormFieldKind.PushButton : (ff & FfRadio) != 0 ? FormFieldKind.RadioButton : FormFieldKind.CheckBox,
                "/Ch" => (ff & FfCombo) != 0 ? FormFieldKind.ComboBox : FormFieldKind.ListBox,
                "/Sig" => FormFieldKind.Signature,
                _ => DateFormat.Length > 0 ? FormFieldKind.Date : FormFieldKind.Text,
            };
        }
    }

    public string Value
    {
        get
        {
            var v = Inherited("/V");
            if (v is PdfArray a) return a.Elements.Count > 0 ? TextOf(a.Elements[0]) : string.Empty;
            return TextOf(v);
        }
    }

    public string DefaultValue => TextOf(Inherited("/DV"));

    /// <summary>Opcje listy: (eksport, wyświetlana).</summary>
    public List<(string Export, string Display)> Options
    {
        get
        {
            var list = new List<(string, string)>();
            if (Deref(Inherited("/Opt")) is PdfArray opt)
                foreach (var o in opt.Elements)
                {
                    if (Deref(o) is PdfArray pair && pair.Elements.Count >= 2) list.Add((TextOf(pair.Elements[0]), TextOf(pair.Elements[1])));
                    else { var s = TextOf(o); list.Add((s, s)); }
                }
            return list;
        }
    }

    /// <summary>Nazwy stanów „włączony” widżetów (pole wyboru / opcje).</summary>
    public static string? OnState(PdfDictionary widget)
    {
        if (PdfLow.Dict(widget, "/AP") is { } ap && PdfLow.Dict(ap, "/N") is { } n)
            foreach (var k in n.Elements.Keys)
                if (k != "/Off") return k.TrimStart('/');
        if (PdfLow.Dict(widget, AnnotModel.PrivateKey) is { } p && NameOf(p, "/On") is { } on) return on.TrimStart('/');
        return null;
    }

    public string DaText => TextOf(Inherited("/DA"));
}

/// <summary>Drzewo pól AcroForm: odczyt pól końcowych, indeks widżet → strona.</summary>
internal static class FieldTree
{
    public static PdfDictionary? AcroForm(PdfDocument doc) => Dict(doc.Internals.Catalog, "/AcroForm");

    public static List<FieldNode> Terminals(PdfDocument doc)
    {
        var result = new List<FieldNode>();
        var acro = AcroForm(doc);
        if (acro is null || ArrayOf(acro, "/Fields") is not { } fields) return result;
        foreach (var f in fields.Elements)
            if (Deref(f) is PdfDictionary d) Walk(d, null, string.Empty, result, 0);
        return result;
    }

    private static void Walk(PdfDictionary d, PdfDictionary? parent, string prefix, List<FieldNode> result, int depth)
    {
        if (depth > 32) return;
        var t = Text(d, "/T");
        string name = t.Length == 0 ? prefix : prefix.Length == 0 ? t : prefix + "." + t;
        var kids = ArrayOf(d, "/Kids");
        var kidDicts = kids?.Elements.Select(Deref).OfType<PdfDictionary>().ToList() ?? new List<PdfDictionary>();
        var fieldKids = kidDicts.Where(k => k.Elements.ContainsKey("/T")).ToList();
        if (fieldKids.Count > 0)
        {
            foreach (var k in fieldKids) Walk(k, d, name, result, depth + 1);
            var widgetKids = kidDicts.Where(k => !k.Elements.ContainsKey("/T")).ToList();
            if (widgetKids.Count == 0) return;
            var node = new FieldNode { Dict = d, FullName = name, Parent = parent };
            node.Widgets.AddRange(widgetKids);
            result.Add(node);
            return;
        }
        var n = new FieldNode { Dict = d, FullName = name, Parent = parent };
        if (kidDicts.Count > 0) n.Widgets.AddRange(kidDicts);
        else n.Widgets.Add(d);
        result.Add(n);
    }

    /// <summary>Słownik widżet → (strona, indeks w /Annots).</summary>
    public static Dictionary<PdfDictionary, (int Page, int Index)> WidgetPages(PdfDocument doc)
    {
        var map = new Dictionary<PdfDictionary, (int, int)>(ReferenceEqualityComparer.Instance);
        for (int p = 0; p < doc.PageCount; p++)
        {
            int widx = 0;
            foreach (var (d, _) in AnnotDicts(doc.Pages[p]))
                if (NameOf(d, "/Subtype") == "/Widget") map.TryAdd(d, (p, widx++));
        }
        return map;
    }

    public static FieldNode? Find(PdfDocument doc, string fullName) =>
        Terminals(doc).FirstOrDefault(f => string.Equals(f.FullName, fullName, StringComparison.Ordinal));
}

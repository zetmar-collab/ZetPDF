using System.IO.Compression;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;
using ZetPDF.Core.Localization;

namespace ZetPDF.Annotations.Pdf;

/// <summary>Pliki osadzone: drzewo nazw /Names /EmbeddedFiles i specyfikacje plików (/Filespec z /EF).</summary>
internal static class EmbeddedFiles
{
    public sealed record Entry(string Name, PdfDictionary FileSpec);

    public static PdfDictionary CreateFileSpec(PdfDocument doc, string path, string displayName, string description)
    {
        var data = File.ReadAllBytes(path);
        var info = new FileInfo(path);
        return CreateFileSpec(doc, data, displayName, description, info.LastWriteTime);
    }

    public static PdfDictionary CreateFileSpec(PdfDocument doc, byte[] data, string displayName, string description, DateTimeOffset modified)
    {
        var ef = NewDict(doc, "/EmbeddedFile");
        var param = NewDict(doc);
        param.Elements["/Size"] = new PdfInteger(data.Length);
        param.Elements["/ModDate"] = new PdfString(FormatDate(modified));
        ef.Elements["/Params"] = param;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            ef.CreateStream(ms.ToArray());
        }
        ef.Elements["/Filter"] = Name("/FlateDecode");
        doc.Internals.AddObject(ef);

        var efd = NewDict(doc);
        efd.Elements["/F"] = ef.Reference!;
        efd.Elements["/UF"] = ef.Reference!;
        var spec = NewDict(doc, "/Filespec");
        spec.Elements["/F"] = new PdfString(AsciiName(displayName));
        spec.Elements["/UF"] = Str(displayName);
        spec.Elements["/EF"] = efd;
        if (!string.IsNullOrEmpty(description)) spec.Elements["/Desc"] = Str(description);
        doc.Internals.AddObject(spec);
        return spec;
    }

    private static string AsciiName(string s)
    {
        var chars = s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => c switch { 'ł' => 'l', 'Ł' => 'L', _ => c < 128 && c >= 32 ? c : '_' });
        return new string(chars.ToArray());
    }

    public static string FileName(PdfDictionary spec)
    {
        var uf = Text(spec, "/UF");
        return uf.Length > 0 ? uf : Text(spec, "/F");
    }

    public static PdfDictionary? EmbeddedStream(PdfDictionary spec)
    {
        var ef = Dict(spec, "/EF");
        if (ef is null) return null;
        return Dict(ef, "/UF") ?? Dict(ef, "/F");
    }

    public static byte[] Data(PdfDictionary spec)
    {
        var s = EmbeddedStream(spec) ?? throw new InvalidDataException("File specification has no embedded data.").WithMessageKey("Annot.Error.AttachmentNoData");
        return s.Stream?.UnfilteredValue ?? Array.Empty<byte>();
    }

    public static long Size(PdfDictionary spec)
    {
        var s = EmbeddedStream(spec);
        if (s is null) return 0;
        if (Dict(s, "/Params") is { } p && p.Elements.ContainsKey("/Size")) return (long)Number(p, "/Size");
        return s.Stream?.UnfilteredValue?.LongLength ?? 0;
    }

    public static DateTimeOffset? Modified(PdfDictionary spec)
    {
        var s = EmbeddedStream(spec);
        return s is not null && Dict(s, "/Params") is { } p ? ParseDate(Text(p, "/ModDate")) : null;
    }

    /// <summary>Wpisy drzewa nazw (rekurencyjnie przez /Kids).</summary>
    public static List<Entry> List(PdfDocument doc)
    {
        var result = new List<Entry>();
        var tree = Tree(doc, create: false);
        if (tree is not null) Walk(tree, result, 0);
        return result;
    }

    private static void Walk(PdfDictionary node, List<Entry> result, int depth)
    {
        if (depth > 32) return;
        if (ArrayOf(node, "/Names") is { } names)
            for (int i = 0; i + 1 < names.Elements.Count; i += 2)
                if (Deref(names.Elements[i + 1]) is PdfDictionary spec)
                    result.Add(new Entry(TextOf(names.Elements[i]), spec));
        if (ArrayOf(node, "/Kids") is { } kids)
            foreach (var k in kids.Elements)
                if (Deref(k) is PdfDictionary kd) Walk(kd, result, depth + 1);
    }

    private static PdfDictionary? Tree(PdfDocument doc, bool create)
    {
        var catalog = doc.Internals.Catalog;
        var names = Dict(catalog, "/Names");
        if (names is null)
        {
            if (!create) return null;
            names = NewDict(doc);
            catalog.Elements["/Names"] = names;
        }
        var tree = Dict(names, "/EmbeddedFiles");
        if (tree is null && create)
        {
            tree = NewDict(doc);
            names.Elements["/EmbeddedFiles"] = tree;
        }
        return tree;
    }

    /// <summary>Zapisuje drzewo jako płaską, posortowaną tablicę /Names.</summary>
    public static void Write(PdfDocument doc, IEnumerable<Entry> entries)
    {
        var tree = Tree(doc, create: true)!;
        tree.Elements.Remove("/Kids");
        tree.Elements.Remove("/Limits");
        var arr = new PdfArray(doc);
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            arr.Elements.Add(Str(e.Name));
            arr.Elements.Add(e.FileSpec.Reference is { } r ? r : e.FileSpec);
        }
        tree.Elements["/Names"] = arr;
    }

    public static string UniqueName(IEnumerable<string> existing, string name)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!set.Contains(candidate)) return candidate;
        }
    }
}

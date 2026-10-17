using System.Text;
using System.Xml;
using System.Xml.Linq;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.Annotations.Forms;

/// <summary>Dane formularza: FDF (Adobe FDF 1.2), XFDF (Adobe XFDF 3.0) i CSV (nagłówek = pełne nazwy pól, UTF-8 z BOM).</summary>
internal static class FormDataIO
{
    // ------------------------------------------------------------------ eksport

    public static byte[] Export(IReadOnlyList<FormFieldInfo> fields, FormDataFormat format, string? fileName = null)
    {
        var data = fields.Where(f => f.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature))
            .GroupBy(f => f.FullName).Select(g => (Name: g.Key, g.First().Value, g.First().Kind)).ToList();
        return format switch
        {
            FormDataFormat.Fdf => Fdf(data, fileName),
            FormDataFormat.Xfdf => Xfdf(data, fileName),
            _ => Csv(data),
        };
    }

    private sealed class Node
    {
        public string Name = string.Empty;
        public string? Value;
        public bool IsName;
        public List<Node> Kids { get; } = new();
    }

    private static List<Node> Tree(IEnumerable<(string Name, string Value, FormFieldKind Kind)> data)
    {
        var roots = new List<Node>();
        foreach (var (name, value, kind) in data)
        {
            var parts = name.Split('.');
            var level = roots;
            Node? node = null;
            foreach (var part in parts)
            {
                node = level.FirstOrDefault(n => n.Name == part);
                if (node is null) { node = new Node { Name = part }; level.Add(node); }
                level = node.Kids;
            }
            node!.Value = value;
            node.IsName = kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton;
        }
        return roots;
    }

    private static byte[] Fdf(List<(string Name, string Value, FormFieldKind Kind)> data, string? fileName)
    {
        var sb = new StringBuilder();
        void Write(Node n)
        {
            sb.Append("<< /T ").Append(FdfWriter.String(n.Name));
            if (n.Value is not null)
                sb.Append(" /V ").Append(n.IsName ? FdfWriter.Name(string.IsNullOrEmpty(n.Value) ? "Off" : n.Value) : FdfWriter.String(n.Value));
            if (n.Kids.Count > 0)
            {
                sb.Append(" /Kids [");
                foreach (var k in n.Kids) Write(k);
                sb.Append(']');
            }
            sb.Append(" >>\n");
        }
        var body = new StringBuilder("<< /Fields [\n");
        foreach (var r in Tree(data)) Write(r);
        body.Append(sb).Append(']');
        if (!string.IsNullOrEmpty(fileName)) body.Append(" /F ").Append(FdfWriter.String(fileName));
        body.Append(" >>");
        return new FdfWriter().Build(body.ToString());
    }

    private static byte[] Xfdf(List<(string Name, string Value, FormFieldKind Kind)> data, string? fileName)
    {
        var ns = XfdfComments.Ns;
        XElement Make(Node n)
        {
            var el = new XElement(ns + "field", new XAttribute("name", n.Name));
            if (n.Value is not null) el.Add(new XElement(ns + "value", n.Value));
            foreach (var k in n.Kids) el.Add(Make(k));
            return el;
        }
        var fieldsEl = new XElement(ns + "fields", Tree(data).Select(Make));
        var root = new XElement(ns + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"), fieldsEl);
        if (!string.IsNullOrEmpty(fileName)) root.Add(new XElement(ns + "f", new XAttribute("href", fileName)));
        using var ms = new MemoryStream();
        new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(ms);
        return ms.ToArray();
    }

    private static byte[] Csv(List<(string Name, string Value, FormFieldKind Kind)> data)
    {
        static string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.Append(string.Join(",", data.Select(d => Q(d.Name)))).Append("\r\n");
        sb.Append(string.Join(",", data.Select(d => Q(d.Value)))).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    // ------------------------------------------------------------------ import

    public static Dictionary<string, string> Import(byte[] data, FormDataFormat format) => format switch
    {
        FormDataFormat.Fdf => ImportFdf(data),
        FormDataFormat.Xfdf => ImportXfdf(data),
        _ => ImportCsv(data),
    };

    private static Dictionary<string, string> ImportFdf(byte[] data)
    {
        var fdf = FdfParser.Parse(data);
        var root = fdf.FdfRoot ?? throw new InvalidDataException("Not an FDF file.").WithMessageKey("Annot.Error.NotFdf");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(FdfValue? item, string prefix, int depth)
        {
            if (depth > 32 || fdf.ResolveDict(item) is not { } d) return;
            var t = fdf.Text(d["/T"]);
            var name = t.Length == 0 ? prefix : prefix.Length == 0 ? t : prefix + "." + t;
            if (d.Items.ContainsKey("/V"))
            {
                var v = fdf.Resolve(d["/V"]);
                result[name] = v switch
                {
                    FdfName n => n.Value.TrimStart('/'),
                    FdfArray a => a.Items.Count > 0 ? fdf.Text(a.Items[0]) : string.Empty,
                    _ => fdf.Text(v),
                };
            }
            if (fdf.ResolveArray(d["/Kids"]) is { } kids)
                foreach (var k in kids.Items) Walk(k, name, depth + 1);
        }
        if (fdf.ResolveArray(root["/Fields"]) is { } fields)
            foreach (var f in fields.Items) Walk(f, string.Empty, 0);
        return result;
    }

    private static Dictionary<string, string> ImportXfdf(byte[] data)
    {
        XDocument x;
        using (var ms = new MemoryStream(data, writable: false))
        using (var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            x = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = x.Root ?? throw new InvalidDataException("Empty XFDF.").WithMessageKey("Annot.Error.EmptyXfdf");
        if (!string.Equals(root.Name.LocalName, "xfdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Not an XFDF file (root element <" + root.Name.LocalName + ">).").WithMessageKey("Annot.Error.EmptyXfdf");
        var ns = root.Name.Namespace;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(XElement el, string prefix)
        {
            var n = (string?)el.Attribute("name") ?? string.Empty;
            var name = prefix.Length == 0 ? n : n.Length == 0 ? prefix : prefix + "." + n;
            var value = el.Elements(ns + "value").FirstOrDefault() ?? el.Elements(ns + "value-richtext").FirstOrDefault();
            if (value is not null) result[name] = value.Value;
            foreach (var k in el.Elements(ns + "field")) Walk(k, name);
        }
        foreach (var f in root.Element(ns + "fields")?.Elements(ns + "field") ?? Enumerable.Empty<XElement>()) Walk(f, string.Empty);
        return result;
    }

    private static Dictionary<string, string> ImportCsv(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data).TrimStart('﻿');
        var rows = ParseCsv(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rows.Count < 2) return result;
        for (int i = 0; i < rows[0].Count; i++)
            if (rows[0][i].Length > 0) result[rows[0][i]] = i < rows[1].Count ? rows[1][i] : string.Empty;
        return result;
    }

    /// <summary>CSV wg RFC 4180 (separator przecinek albo średnik – wykrywany z nagłówka).</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        char sep = ',';
        int firstLineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        var header = firstLineEnd < 0 ? text : text[..firstLineEnd];
        if (header.Count(c => c == ';') > header.Count(c => c == ',')) sep = ';';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}

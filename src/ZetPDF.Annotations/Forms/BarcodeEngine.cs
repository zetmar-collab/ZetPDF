using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZXing;
using ZXing.Common;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Forms;

public enum BarcodeSymbology { QrCode, Pdf417, DataMatrix }

public enum BarcodeDataFormat { TabDelimited, Json }

/// <summary>Ustawienia pola kodu kreskowego: symbolika, format danych, pola źródłowe (puste = wszystkie pola danych).</summary>
public sealed record BarcodeOptions
{
    public BarcodeSymbology Symbology { get; init; } = BarcodeSymbology.QrCode;
    public BarcodeDataFormat Format { get; init; } = BarcodeDataFormat.TabDelimited;
    /// <summary>Format tabulacyjny: pierwszy wiersz z nazwami pól.</summary>
    public bool IncludeNames { get; init; } = true;
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pole kodu kreskowego = pole tekstowe tylko do odczytu z prywatnym słownikiem /ZetPDF /Barcode. Wartość (/V) to dane
/// zakodowane z wybranych pól (tabulatory albo JSON), wygląd /AP – symbol (QR, PDF417, DataMatrix z ZXing.Net) narysowany
/// wektorowo (prostokąty modułów, bez wygładzania), więc dekoduje się z renderu w każdym czytniku. Dane i wygląd są
/// odtwarzane przy każdej zmianie wartości pól przez <see cref="FormService"/> (wypełnianie, import, reset, obliczenia).
/// </summary>
internal static class BarcodeEngine
{
    private const string Key = "/Barcode";

    public static BarcodeOptions? Options(FieldNode f)
    {
        if (Dict(f.Dict, AnnotModel.PrivateKey) is not { } priv || Dict(priv, Key) is not { } b) return null;
        var fields = ArrayOf(b, "/Fields")?.Elements.Select(TextOf).Where(s => s.Length > 0).ToList() ?? new List<string>();
        return new BarcodeOptions
        {
            Symbology = NameOf(b, "/Sym") switch { "/PDF417" => BarcodeSymbology.Pdf417, "/DataMatrix" => BarcodeSymbology.DataMatrix, _ => BarcodeSymbology.QrCode },
            Format = NameOf(b, "/Fmt") == "/JSON" ? BarcodeDataFormat.Json : BarcodeDataFormat.TabDelimited,
            IncludeNames = !b.Elements.ContainsKey("/Names") || b.Elements.GetBoolean("/Names"),
            Fields = fields,
        };
    }

    public static void Write(PdfDocument doc, FieldNode f, BarcodeOptions o)
    {
        var priv = Dict(f.Dict, AnnotModel.PrivateKey);
        if (priv is null) { priv = NewDict(doc); f.Dict.Elements[AnnotModel.PrivateKey] = priv; }
        var b = NewDict(doc);
        b.Elements["/Sym"] = Name(o.Symbology switch { BarcodeSymbology.Pdf417 => "/PDF417", BarcodeSymbology.DataMatrix => "/DataMatrix", _ => "/QR" });
        b.Elements["/Fmt"] = Name(o.Format == BarcodeDataFormat.Json ? "/JSON" : "/Tab");
        b.Elements["/Names"] = new PdfBoolean(o.IncludeNames);
        var arr = new PdfArray(doc);
        foreach (var n in o.Fields) arr.Elements.Add(Str(n));
        b.Elements["/Fields"] = arr;
        priv.Elements[Key] = b;
        f.Dict.Elements["/Ff"] = new PdfInteger(f.Flags | FieldNode.FfReadOnly);
    }

    /// <summary>Pola danych do zakodowania (bez przycisków, podpisów i innych pól kodu).</summary>
    public static List<FieldNode> Sources(List<FieldNode> all, BarcodeOptions o)
    {
        var data = all.Where(f => f.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature) && Options(f) is null).ToList();
        if (o.Fields.Count == 0) return data;
        var byName = data.GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return o.Fields.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
    }

    public static string Encode(IReadOnlyList<(string Name, string Value)> values, BarcodeOptions o)
    {
        if (o.Format == BarcodeDataFormat.Json)
        {
            var obj = new JsonObject();
            foreach (var (n, v) in values) obj[n] = v;
            return obj.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        static string Clean(string s) => s.Replace('\t', ' ').Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        var sb = new StringBuilder();
        if (o.IncludeNames) sb.Append(string.Join('\t', values.Select(v => Clean(v.Name)))).Append('\n');
        sb.Append(string.Join('\t', values.Select(v => Clean(v.Value))));
        return sb.ToString();
    }

    /// <summary>Przelicza dane wszystkich pól kodu (/V) – woła się przed odtworzeniem wyglądu.</summary>
    public static void UpdateValues(PdfDocument doc)
    {
        var all = FieldTree.Terminals(doc);
        foreach (var f in all)
        {
            if (Options(f) is not { } o) continue;
            var data = Encode(Sources(all, o).Select(s => (s.FullName, s.Value)).ToList(), o);
            f.Dict.Elements["/V"] = Str(data);
        }
    }

    /// <summary>Przelicza dane i wygląd wszystkich pól kodu.</summary>
    public static void UpdateAll(PdfDocument doc)
    {
        UpdateValues(doc);
        foreach (var f in FieldTree.Terminals(doc))
            if (Options(f) is not null) FieldAppearance.Build(doc, f);
    }

    public static BitMatrix? Matrix(string data, BarcodeSymbology sym)
    {
        if (string.IsNullOrEmpty(data)) data = " ";
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = 0,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
        };
        var format = sym switch { BarcodeSymbology.Pdf417 => BarcodeFormat.PDF_417, BarcodeSymbology.DataMatrix => BarcodeFormat.DATA_MATRIX, _ => BarcodeFormat.QR_CODE };
        if (sym == BarcodeSymbology.QrCode) hints[EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M;
        if (sym == BarcodeSymbology.DataMatrix) hints[EncodeHintType.DATA_MATRIX_SHAPE] = ZXing.Datamatrix.Encoder.SymbolShapeHint.FORCE_SQUARE;
        try { return new MultiFormatWriter().encode(data, format, 0, 0, hints); }
        catch (Exception ex) when (ex is WriterException or ArgumentException or InvalidOperationException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>Wygląd widżetu: białe tło i moduły symbolu (prostokąty łączone w wierszach), wyśrodkowane ze strefą ciszy.</summary>
    public static PdfDictionary Appearance(PdfDocument doc, PdfDictionary widget, string data, BarcodeSymbology sym)
    {
        var r = PdfLow.Rect(widget) ?? new PdfRect(0, 0, 50, 50);
        double w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);
        var c = new ContentBuilder();
        c.Fill(PdfColor.White).Rect(new PdfRect(0, 0, w, h)).Op("f");
        var m = Matrix(data, sym);
        if (m is null)
        {
            // dane nie mieszczą się w symbolu – przekreślona ramka
            c.Width(1).Stroke(PdfColor.Red).Rect(new PdfRect(0.5, 0.5, w - 0.5, h - 0.5)).Move(new(0, 0)).Line(new(w, h)).Move(new(0, h)).Line(new(w, 0)).Op("S");
            return FormXObject(doc, new PdfRect(0, 0, w, h), c.ToString());
        }
        int quiet = 2;
        int cols = m.Width + 2 * quiet, rows = m.Height + 2 * quiet;
        double s = Math.Min(w / cols, h / rows);
        double ox = (w - cols * s) / 2 + quiet * s, oy = (h - rows * s) / 2 + quiet * s;
        c.Fill(PdfColor.Black);
        for (int y = 0; y < m.Height; y++)
        {
            double py = oy + (m.Height - 1 - y) * s;
            int x = 0;
            while (x < m.Width)
            {
                if (!m[x, y]) { x++; continue; }
                int start = x;
                while (x < m.Width && m[x, y]) x++;
                c.Rect(new PdfRect(ox + start * s, py, ox + x * s, py + s));
            }
        }
        c.Op("f");
        return FormXObject(doc, new PdfRect(0, 0, w, h), c.ToString());
    }
}

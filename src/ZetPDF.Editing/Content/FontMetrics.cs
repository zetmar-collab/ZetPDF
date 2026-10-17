using PdfSharp.Pdf;
using ZetPDF.Editing.Pdf;

namespace ZetPDF.Editing.Content;

/// <summary>
/// Szerokości glifów czcionki z zasobów strony: czcionki proste (/Widths, /FirstChar, /MissingWidth) i Type0
/// z kodowaniem Identity-H/V (/W, /DW). Inne kodowania CID i Type3 – nieobsługiwane (IsSupported = false).
/// </summary>
internal sealed class FontMetrics
{
    private readonly Dictionary<int, double> _widths = new();
    private readonly double _default;

    private FontMetrics(bool twoByte, double defaultWidth, bool supported)
    {
        TwoByte = twoByte;
        _default = defaultWidth;
        IsSupported = supported;
    }

    public bool TwoByte { get; }
    public bool IsSupported { get; }

    /// <summary>Szerokość w jednostkach 1/1000 em.</summary>
    public double Width(int code) => _widths.TryGetValue(code, out var w) ? w : _default;

    public static FontMetrics Unsupported { get; } = new(false, 0, false);

    public static FontMetrics From(PdfDictionary? font)
    {
        if (font is null) return Unsupported;
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type0")
        {
            var enc = PdfObjects.Resolve(font.Elements["/Encoding"]);
            if (enc is not PdfName name || (name.Value != "/Identity-H" && name.Value != "/Identity-V")) return Unsupported;
            if (PdfObjects.Resolve(font.Elements["/DescendantFonts"]) is not PdfArray desc || desc.Elements.Count == 0) return Unsupported;
            if (PdfObjects.Resolve(desc.Elements[0]) is not PdfDictionary cid) return Unsupported;
            double dw = cid.Elements["/DW"] is null ? 1000 : PdfObjects.Number(cid.Elements["/DW"]);
            var m = new FontMetrics(true, dw, true);
            if (PdfObjects.Resolve(cid.Elements["/W"]) is PdfArray w)
            {
                int i = 0;
                while (i < w.Elements.Count)
                {
                    int first = (int)PdfObjects.Number(w.Elements[i]);
                    if (i + 1 >= w.Elements.Count) break;
                    var next = PdfObjects.Resolve(w.Elements[i + 1]);
                    if (next is PdfArray list)
                    {
                        for (int k = 0; k < list.Elements.Count; k++) m._widths[first + k] = PdfObjects.Number(list.Elements[k]);
                        i += 2;
                    }
                    else
                    {
                        if (i + 2 >= w.Elements.Count) break;
                        int last = (int)PdfObjects.Number(next);
                        double width = PdfObjects.Number(w.Elements[i + 2]);
                        for (int c = first; c <= last && c - first < 65536; c++) m._widths[c] = width;
                        i += 3;
                    }
                }
            }
            return m;
        }
        if (subtype is "/Type1" or "/TrueType" or "/MMType1")
        {
            if (PdfObjects.Resolve(font.Elements["/Widths"]) is not PdfArray widths) return Unsupported; // standardowe 14 bez /Widths
            int firstChar = (int)PdfObjects.Number(font.Elements["/FirstChar"]);
            double missing = 0;
            if (PdfObjects.Resolve(font.Elements["/FontDescriptor"]) is PdfDictionary fd) missing = PdfObjects.Number(fd.Elements["/MissingWidth"]);
            var m = new FontMetrics(false, missing, true);
            for (int k = 0; k < widths.Elements.Count; k++) m._widths[firstChar + k] = PdfObjects.Number(widths.Elements[k]);
            return m;
        }
        return Unsupported;
    }
}

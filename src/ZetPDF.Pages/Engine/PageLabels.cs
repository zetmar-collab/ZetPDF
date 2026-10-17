using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.Pages.Engine;

/// <summary>Etykiety stron – drzewo liczbowe /PageLabels w katalogu (ISO 32000-1, 12.4.2).</summary>
internal static class PageLabels
{
    public static byte[] Set(byte[] pdf, IReadOnlyList<PageLabelRange> ranges)
    {
        using var doc = PdfIo.OpenModify(pdf);
        var catalog = doc.Internals.Catalog;
        if (ranges.Count == 0)
        {
            catalog.Elements.Remove("/PageLabels");
            return PdfIo.Save(doc);
        }
        var sorted = ranges.Where(r => r.StartPageIndex >= 0 && r.StartPageIndex < doc.PageCount)
            .GroupBy(r => r.StartPageIndex).Select(g => g.Last()).OrderBy(r => r.StartPageIndex).ToList();
        if (sorted.Count == 0) throw new ArgumentException("No valid page label ranges.", nameof(ranges)).WithMessageKey("Pages.Error.NoLabelRanges");
        if (sorted[0].StartPageIndex != 0) sorted.Insert(0, new PageLabelRange(0, PageLabelStyle.Decimal));
        var nums = new PdfArray(doc);
        foreach (var r in sorted)
        {
            if (r.StartNumber < 1) throw new ArgumentOutOfRangeException(nameof(ranges), "Start number must be at least 1.");
            nums.Elements.Add(new PdfInteger(r.StartPageIndex));
            var d = new PdfDictionary(doc);
            d.Elements["/Type"] = new PdfName("/PageLabel");
            var s = StyleName(r.Style);
            if (s is not null) d.Elements["/S"] = new PdfName(s);
            if (!string.IsNullOrEmpty(r.Prefix)) d.Elements["/P"] = new PdfString(r.Prefix, PdfStringEncoding.Unicode);
            if (r.StartNumber != 1) d.Elements["/St"] = new PdfInteger(r.StartNumber);
            nums.Elements.Add(d);
        }
        var tree = new PdfDictionary(doc);
        tree.Elements["/Nums"] = nums;
        catalog.Elements["/PageLabels"] = tree;
        return PdfIo.Save(doc);
    }

    public static IReadOnlyList<PageLabelRange> Get(byte[] pdf)
    {
        using var doc = PdfIo.OpenImport(pdf);
        var list = new List<PageLabelRange>();
        if (PdfIo.GetDict(doc.Internals.Catalog, "/PageLabels") is { } root) Collect(root, list, 0);
        return list.OrderBy(r => r.StartPageIndex).ToList();
    }

    private static void Collect(PdfDictionary node, List<PageLabelRange> list, int depth)
    {
        if (depth > 32) return;
        if (PdfIo.GetArray(node, "/Nums") is { } nums)
        {
            for (int i = 0; i + 1 < nums.Elements.Count; i += 2)
            {
                var idx = PdfIo.GetNumber(nums.Elements[i]);
                if (idx is null || PdfIo.Resolve(nums.Elements[i + 1]) is not PdfDictionary d) continue;
                var style = ParseStyle(PdfIo.GetName(d, "/S"));
                var prefix = PdfIo.GetText(d.Elements["/P"]) ?? string.Empty;
                var st = (int)(PdfIo.GetNumber(d.Elements["/St"]) ?? 1);
                list.Add(new PageLabelRange((int)idx.Value, style, prefix, Math.Max(1, st)));
            }
        }
        if (PdfIo.GetArray(node, "/Kids") is { } kids)
            foreach (var k in kids.Elements)
                if (PdfIo.Resolve(k) is PdfDictionary kd) Collect(kd, list, depth + 1);
    }

    private static string? StyleName(PageLabelStyle s) => s switch
    {
        PageLabelStyle.Decimal => "/D",
        PageLabelStyle.UpperRoman => "/R",
        PageLabelStyle.LowerRoman => "/r",
        PageLabelStyle.UpperLetters => "/A",
        PageLabelStyle.LowerLetters => "/a",
        _ => null,
    };

    private static PageLabelStyle ParseStyle(string? s) => s switch
    {
        "/D" => PageLabelStyle.Decimal,
        "/R" => PageLabelStyle.UpperRoman,
        "/r" => PageLabelStyle.LowerRoman,
        "/A" => PageLabelStyle.UpperLetters,
        "/a" => PageLabelStyle.LowerLetters,
        _ => PageLabelStyle.None,
    };

    /// <summary>Etykieta strony wg zakresów (do podglądu w oknie i testów).</summary>
    public static string Label(IReadOnlyList<PageLabelRange> ranges, int pageIndex)
    {
        var r = ranges.Where(x => x.StartPageIndex <= pageIndex).OrderBy(x => x.StartPageIndex).LastOrDefault();
        if (r is null) return (pageIndex + 1).ToString(CultureInfo.InvariantCulture);
        int n = r.StartNumber + pageIndex - r.StartPageIndex;
        return r.Prefix + r.Style switch
        {
            PageLabelStyle.Decimal => n.ToString(CultureInfo.InvariantCulture),
            PageLabelStyle.UpperRoman => Roman(n),
            PageLabelStyle.LowerRoman => Roman(n).ToLowerInvariant(),
            PageLabelStyle.UpperLetters => Letters(n),
            PageLabelStyle.LowerLetters => Letters(n).ToLowerInvariant(),
            _ => string.Empty,
        };
    }

    private static string Roman(int n)
    {
        if (n <= 0) return n.ToString(CultureInfo.InvariantCulture);
        var map = new (int V, string S)[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new StringBuilder();
        foreach (var (v, s) in map) while (n >= v) { sb.Append(s); n -= v; }
        return sb.ToString();
    }

    /// <summary>A..Z, AA..ZZ, AAA… (konwencja PDF).</summary>
    private static string Letters(int n)
    {
        if (n <= 0) return string.Empty;
        char c = (char)('A' + (n - 1) % 26);
        return new string(c, (n - 1) / 26 + 1);
    }
}

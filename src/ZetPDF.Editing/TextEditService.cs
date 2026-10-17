using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Content;
using ZetPDF.Editing.Text;
using ZetPDF.Ocr.Pdf;
using ZetPDF.Core.Localization;

namespace ZetPDF.Editing;

/// <summary>
/// Edycja tekstu. Blok = linia tekstu (PdfPig). Zastąpienie: glify bloku są USUWANE ze strumienia treści
/// (interpreter <see cref="ContentWalker"/>: usunięty glif → przesunięcie w TJ, reszta linii zostaje na miejscu),
/// a nowy tekst jest dopisywany czcionką systemową osadzoną jako TrueType/Identity-H (polskie znaki).
/// Gdy usunięcie jest niewykonalne (czcionka Type3, kodowanie CID inne niż Identity, standardowa czcionka bez /Widths,
/// tekst wewnątrz formularza XObject) – rezerwa: stary tekst zakrywany białym prostokątem (patrz ograniczenia).
/// </summary>
public sealed class TextEditService : ITextEditService
{
    private const string LayerMarker = "edit-text";

    public Task<IReadOnlyList<PdfTextBlock>> GetTextBlocksAsync(byte[] pdf, int pageIndex, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PdfTextBlock>>(() => Lines(pdf, pageIndex).Select(ToBlock).ToList(), ct);

    internal static List<TextLine> Lines(byte[] pdf, int pageIndex)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return TextLines.Build(doc.GetPage(pageIndex + 1), pageIndex);
    }

    internal static string BlockId(TextLine l) =>
        string.Create(CultureInfo.InvariantCulture, $"p{l.PageIndex}-l{l.Index}-{(uint)StableHash(l.Text):x8}");

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }

    private static PdfTextBlock ToBlock(TextLine l) =>
        new(BlockId(l), l.PageIndex, l.Bounds, l.Text, l.FontName, l.FontSize, l.Color, l.Bold, l.Italic);

    public Task<byte[]> ReplaceTextBlockAsync(byte[] pdf, int pageIndex, string blockId, string newText, TextStyle style, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(style);
        return Task.Run(() =>
        {
            var line = Lines(pdf, pageIndex).FirstOrDefault(l => BlockId(l) == blockId)
                ?? throw new InvalidOperationException("Text block not found: " + blockId).WithMessageKey("Edit.Error.TextBlockNotFound");
            return PdfSharpEdit.Modify(pdf, doc =>
            {
                var page = doc.Pages[pageIndex];
                RemoveOrCover(page, new[] { line.Bounds });
                WriteText(page, line.Baseline, newText, style, null);
            });
        }, ct);
    }

    public Task<byte[]> AddTextAsync(byte[] pdf, int pageIndex, PdfPoint position, string text, TextStyle style, double? maxWidth = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(style);
        return PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            WriteText(doc.Pages[pageIndex], position, text, style, maxWidth);
        }, ct);
    }

    public Task<FindReplaceResult> FindReplaceAsync(byte[] pdf, string find, string replace, TextSearchOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(find)) throw new ArgumentException("Empty search text.", nameof(find)).WithMessageKey("Edit.Error.EmptySearch");
        replace ??= string.Empty;
        return Task.Run(() =>
        {
            var pattern = Regex.Escape(find);
            if (options.HasFlag(TextSearchOptions.WholeWord)) pattern = $@"(?<![\p{{L}}\p{{N}}]){pattern}(?![\p{{L}}\p{{N}}])";
            var regex = new Regex(pattern, options.HasFlag(TextSearchOptions.MatchCase) ? RegexOptions.CultureInvariant : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var plan = new Dictionary<int, List<(TextLine Line, string NewText)>>();
            int replaced = 0, skipped = 0;
            using (var doc = UglyToad.PdfPig.PdfDocument.Open(pdf))
            {
                for (int p = 0; p < doc.NumberOfPages; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(OperationProgress.Of(p, doc.NumberOfPages));
                    var page = doc.GetPage(p + 1);
                    foreach (var line in TextLines.Build(page, p))
                    {
                        var n = regex.Matches(line.Text).Count;
                        if (n == 0) continue;
                        if (!plan.TryGetValue(p, out var l)) plan[p] = l = new();
                        l.Add((line, regex.Replace(line.Text, replace.Replace("$", "$$"))));
                        replaced += n;
                    }
                    // Trafienia rozdzielone między linie (nie da się ich zastąpić w jednej linii).
                    var pageText = string.Join(" ", TextLines.Build(page, p).Select(x => x.Text));
                    skipped += Math.Max(0, regex.Matches(pageText).Count - (plan.TryGetValue(p, out var pl) ? pl.Sum(x => regex.Matches(x.Line.Text).Count) : 0));
                }
            }
            if (plan.Count == 0) return new FindReplaceResult(pdf, 0, skipped);
            var output = PdfSharpEdit.Modify(pdf, doc =>
            {
                foreach (var (p, lines) in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = doc.Pages[p];
                    RemoveOrCover(page, lines.Select(l => l.Line.Bounds).ToList());
                    foreach (var (line, text) in lines)
                    {
                        var style = new TextStyle { FontFamily = FontNames.ToFamily(line.FontName), FontSize = line.FontSize, Bold = line.Bold, Italic = line.Italic, Color = line.Color };
                        WriteText(page, line.Baseline, text, style, null);
                    }
                }
            });
            return new FindReplaceResult(output, replaced, skipped);
        }, ct);
    }

    /// <summary>Usuwa glify z obszarów; gdy się nie da – zakrywa obszary białym prostokątem. Zwraca true, gdy usunięto.</summary>
    internal static bool RemoveOrCover(PdfPage page, IReadOnlyList<PdfRect> areas)
    {
        var inflated = areas.Select(a => a.Inflate(0.75)).ToList();
        try
        {
            var walker = new ContentWalker(page);
            int removed = walker.RemoveGlyphs(g =>
            {
                var c = new PdfPoint((g.Box.Left + g.Box.Right) / 2, (g.Box.Bottom + g.Box.Top) / 2);
                return inflated.Any(a => a.Contains(c));
            }, out var content);
            if (removed > 0)
            {
                PageContent.Replace(page, content);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // treść nieczytelna dla parsera – rezerwa niżej
        }
        var sb = new StringBuilder("q 1 1 1 rg\n");
        foreach (var a in inflated) sb.Append($"{PageContent.Num(a.Left)} {PageContent.Num(a.Bottom)} {PageContent.Num(a.Width)} {PageContent.Num(a.Height)} re f\n");
        sb.Append("Q\n");
        PageContent.Append(page, Encoding.ASCII.GetBytes(sb.ToString()), "edit-cover");
        return false;
    }

    /// <summary>Dopisuje tekst (wiele linii, opcjonalne zawijanie do maxWidth) od punktu linii bazowej.</summary>
    internal static void WriteText(PdfPage page, PdfPoint baseline, string text, TextStyle style, double? maxWidth)
    {
        var layer = new LayerStyle { FontFamily = style.FontFamily, Bold = style.Bold, Italic = style.Italic, Color = style.Color, Marker = LayerMarker };
        double fs = style.FontSize > 0 ? style.FontSize : 12;
        var lines = Wrap(text ?? string.Empty, layer, fs, maxWidth);
        var words = new List<LayerWord>();
        for (int i = 0; i < lines.Count; i++)
            if (!string.IsNullOrWhiteSpace(lines[i])) words.Add(new LayerWord(lines[i], new PdfPoint(baseline.X, baseline.Y - i * fs * 1.2), fs));
        TextLayerWriter.Write(page, words, layer);
    }

    internal static List<string> Wrap(string text, LayerStyle style, double fontSize, double? maxWidth)
    {
        var result = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (maxWidth is not { } mw || mw <= 0)
            {
                result.Add(para);
                continue;
            }
            var line = new StringBuilder();
            foreach (var word in para.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && TextLayerWriter.MeasureUnitWidth(candidate, style) * fontSize > mw)
                {
                    result.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }
            result.Add(line.ToString());
        }
        return result;
    }
}

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Content;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Utilities;
using ZetPDF.Editing.Imaging;
using ZetPDF.Editing.Text;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace ZetPDF.Editing.Conversion;

/// <summary>
/// PDF → DOCX (podstawowy): akapity z linii tekstu (PdfPig) z rozmiarem i pogrubieniem/kursywą, obrazy osadzone strony
/// (PNG lub JPEG – w kolejności pionowej względem akapitów), strony bez tekstu jako obraz strony (PDFium),
/// podział stron i rozmiar strony jak w PDF. Bez tabel, kolumn, pływających obiektów i stylów nagłówków.
/// </summary>
internal static class DocxWriter
{
    private const long EmuPerPoint = 12700;

    public static async Task WriteAsync(byte[] pdf, string path, DocxExportOptions options, IPdfRenderer renderer, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var pages = string.IsNullOrWhiteSpace(options.PageRange) ? Enumerable.Range(0, pig.NumberOfPages).ToList() : PageRange.Parse(options.PageRange, pig.NumberOfPages).ToList();
        using var rdoc = await renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false);
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        uint imageId = 1;
        double firstW = 595, firstH = 842;

        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(i, pages.Count));
            var page = pig.GetPage(pages[i] + 1);
            if (i == 0) { firstW = page.Width; firstH = page.Height; }
            double contentWidth = page.Width - 72 * 2;
            var lines = TextLines.Build(page, pages[i]).OrderByDescending(l => l.Baseline.Y).ThenBy(l => l.Bounds.Left).ToList();
            var items = new List<(double Top, OpenXmlElement Element)>();

            if (lines.Count == 0 && options.RasterizePagesWithoutText)
            {
                var bmp = await rdoc.RenderPageAsync(pages[i], new RenderRequest(150 / 72.0), ct).ConfigureAwait(false);
                var png = PngWriter.FromBgra(bmp);
                double scale = Math.Min(1, contentWidth / page.Width);
                items.Add((page.Height, ImageParagraph(main, png, ImagePartType.Png, page.Width * scale, page.Height * scale, imageId++)));
            }
            else
            {
                foreach (var para in Paragraphs(lines)) items.Add((para[0].Bounds.Top, TextParagraph(para)));
                if (options.IncludeImages)
                {
                    foreach (var img in SafeImages(page))
                    {
                        var data = ImageBytes(img, out var type);
                        if (data is null) continue;
                        double w = Math.Max(4, img.BoundingBox.Width), h = Math.Max(4, img.BoundingBox.Height);
                        if (w > contentWidth) { h *= contentWidth / w; w = contentWidth; }
                        items.Add((img.BoundingBox.Top, ImageParagraph(main, data, type, w, h, imageId++)));
                    }
                }
            }
            foreach (var (_, el) in items.OrderByDescending(x => x.Top)) body.Append(el);
            if (i < pages.Count - 1) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        }
        body.Append(new SectionProperties(
            new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = (UInt32Value)(uint)Math.Round(firstW * 20), Height = (UInt32Value)(uint)Math.Round(firstH * 20) },
            new PageMargin { Top = 1440, Bottom = 1440, Left = 1440U, Right = 1440U, Header = 720U, Footer = 720U, Gutter = 0U }));
        main.Document.Save();
    }

    internal static IEnumerable<IPdfImage> SafeImages(Page page)
    {
        try { return page.GetImages().ToList(); }
        catch (Exception) { return Array.Empty<IPdfImage>(); }
    }

    /// <summary>Linie → akapity (ten sam rozmiar, mały odstęp pionowy, zbliżony lewy margines).</summary>
    internal static List<List<TextLine>> Paragraphs(List<TextLine> lines)
    {
        var result = new List<List<TextLine>>();
        foreach (var l in lines)
        {
            var cur = result.LastOrDefault();
            if (cur is not null)
            {
                var prev = cur[^1];
                double gap = prev.Baseline.Y - l.Baseline.Y;
                bool same = Math.Abs(prev.FontSize - l.FontSize) < 0.6 && prev.Bold == l.Bold && gap > 0 && gap < l.FontSize * 1.6
                    && Math.Abs(prev.Bounds.Left - l.Bounds.Left) < l.FontSize * 3;
                if (same) { cur.Add(l); continue; }
            }
            result.Add(new List<TextLine> { l });
        }
        return result;
    }

    private static Paragraph TextParagraph(List<TextLine> lines)
    {
        var first = lines[0];
        var props = new RunProperties();
        props.Append(new RunFonts { Ascii = FontNames.ToFamily(first.FontName), HighAnsi = FontNames.ToFamily(first.FontName), ComplexScript = FontNames.ToFamily(first.FontName) });
        if (first.Bold) props.Append(new Bold());
        if (first.Italic) props.Append(new Italic());
        var c = first.Color;
        if (c.R != 0 || c.G != 0 || c.B != 0) props.Append(new Color { Val = $"{c.R:X2}{c.G:X2}{c.B:X2}" });
        props.Append(new FontSize { Val = ((int)Math.Round(first.FontSize * 2)).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var text = string.Join(" ", lines.Select(l => l.Text.Trim()));
        var run = new Run(props, new DocumentFormat.OpenXml.Wordprocessing.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        var pp = new ParagraphProperties(new SpacingBetweenLines { After = "120" });
        return new Paragraph(pp, run);
    }

    internal static byte[]? ImageBytes(IPdfImage img, out PartTypeInfo type)
    {
        type = ImagePartType.Png;
        try
        {
            if (img.TryGetPng(out var png) && png is { Length: > 0 }) return png;
            var filter = img.ImageDictionary.Data.TryGetValue("Filter", out var f) ? f.ToString() : string.Empty;
            if (filter is not null && filter.Contains("DCTDecode", StringComparison.Ordinal))
            {
                type = ImagePartType.Jpeg;
                return img.RawBytes.ToArray();
            }
        }
        catch (Exception) { }
        return null;
    }

    private static Paragraph ImageParagraph(MainDocumentPart main, byte[] data, PartTypeInfo type, double widthPt, double heightPt, uint id)
    {
        var part = main.AddImagePart(type);
        using (var ms = new MemoryStream(data)) part.FeedData(ms);
        var relId = main.GetIdOfPart(part);
        long cx = (long)(widthPt * EmuPerPoint), cy = (long)(heightPt * EmuPerPoint);
        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = "Picture " + id },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image" + id }, new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
        return new Paragraph(new Run(drawing));
    }
}

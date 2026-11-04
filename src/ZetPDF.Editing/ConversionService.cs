using System.Text;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Utilities;
using ZetPDF.Editing.Conversion;
using ZetPDF.Editing.Imaging;
using ZetPDF.Editing.Text;
using ZetPDF.Ocr.Pdf;
using ZetPDF.Core.Localization;

namespace ZetPDF.Editing;

/// <summary>Konwersje: obrazy/TXT → PDF, PDF → obrazy/TXT/DOCX, scalanie plików różnych typów, PDF ze schowka.</summary>
public sealed class ConversionService : IConversionService
{
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif" };
    public static readonly string[] TextExtensions = { ".txt", ".text", ".log", ".csv", ".md" };

    private readonly IPdfRenderer _renderer;
    private readonly ILocalizationService? _loc;
    private readonly IOfficeConverter? _office;

    public ConversionService(IPdfRenderer renderer, ILocalizationService? loc = null, IOfficeConverter? office = null)
    {
        _renderer = renderer;
        _loc = loc;
        _office = office;
    }

    // ------------------------------------------------------------------ obrazy → PDF

    public Task<byte[]> ImagesToPdfAsync(IReadOnlyList<string> imagePaths, ImageToPdfOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        return Task.Run(() =>
        {
            PdfSharpEdit.EnsureInitialized();
            using var doc = new PdfDocument();
            for (int i = 0; i < imagePaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(i, imagePaths.Count, Path.GetFileName(imagePaths[i])));
                foreach (var frame in ImageCodec.PrepareFrames(File.ReadAllBytes(imagePaths[i]))) AddImagePage(doc, frame, options);
            }
            if (doc.PageCount == 0) throw new InvalidOperationException("No images.").WithMessageKey("Edit.Error.NoImages");
            return PdfSharpEdit.Save(doc);
        }, ct);
    }

    public Task<byte[]> ImageDataToPdfAsync(byte[] imageData, ImageToPdfOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            PdfSharpEdit.EnsureInitialized();
            using var doc = new PdfDocument();
            foreach (var frame in ImageCodec.PrepareFrames(imageData)) AddImagePage(doc, frame, options);
            return PdfSharpEdit.Save(doc);
        }, ct);

    internal static void AddImagePage(PdfDocument doc, PreparedImage img, ImageToPdfOptions options)
    {
        double w = img.Width / img.DpiX * 72, h = img.Height / img.DpiY * 72;
        double margin = Math.Max(0, options.Margin);
        double pw, ph;
        switch (options.PageSize)
        {
            case ImagePageSize.A4:
            case ImagePageSize.Letter:
                var size = options.PageSize == ImagePageSize.A4 ? PdfSize.A4 : PdfSize.Letter;
                (pw, ph) = w > h ? (size.Height, size.Width) : (size.Width, size.Height);
                double scale = Math.Min((pw - 2 * margin) / w, (ph - 2 * margin) / h);
                if (scale <= 0) scale = 1;
                w *= scale; h *= scale;
                break;
            default:
                pw = w + 2 * margin; ph = h + 2 * margin;
                break;
        }
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(pw);
        page.Height = XUnit.FromPoint(ph);
        var image = new PdfImage(doc, XImage.FromStream(new MemoryStream(img.Data)));
        var name = page.Resources.AddImage(image);
        double x = (pw - w) / 2, y = (ph - h) / 2;
        PageContent.Append(page, Encoding.ASCII.GetBytes($"q {PageContent.Matrix(w, 0, 0, h, x, y)} cm {name} Do Q\n"));
    }

    // ------------------------------------------------------------------ tekst → PDF

    public Task<byte[]> TextToPdfAsync(string text, TextStyle? style = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            PdfSharpEdit.EnsureInitialized();
            using var doc = new PdfDocument();
            WriteTextPages(doc, text ?? string.Empty, style ?? new TextStyle { FontFamily = "Arial", FontSize = 11 }, ct);
            return PdfSharpEdit.Save(doc);
        }, ct);

    internal static void WriteTextPages(PdfDocument doc, string text, TextStyle style, CancellationToken ct)
    {
        const double margin = 56;
        var size = PdfSize.A4;
        double fs = style.FontSize > 0 ? style.FontSize : 11, lineHeight = fs * 1.25;
        var fontStyle = style.Bold ? (style.Italic ? XFontStyleEx.BoldItalic : XFontStyleEx.Bold) : style.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular;
        var font = new XFont(string.IsNullOrWhiteSpace(style.FontFamily) ? "Arial" : style.FontFamily, fs, fontStyle, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var brush = new XSolidBrush(XColor.FromArgb(255, style.Color.R, style.Color.G, style.Color.B));
        double maxWidth = size.Width - 2 * margin;
        PdfPage? page = null;
        XGraphics? g = null;
        double y = 0;
        try
        {
            void NewPage()
            {
                g?.Dispose();
                page = doc.AddPage();
                page.Width = XUnit.FromPoint(size.Width);
                page.Height = XUnit.FromPoint(size.Height);
                g = XGraphics.FromPdfPage(page);
                y = margin + fs;
            }
            NewPage();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                ct.ThrowIfCancellationRequested();
                var para = raw.Replace("\t", "    ");
                if (para == "\f") { NewPage(); continue; }
                foreach (var line in WrapLine(para, s => g!.MeasureString(s, font).Width, maxWidth))
                {
                    if (y > size.Height - margin) NewPage();
                    if (line.Length > 0) g!.DrawString(line, font, brush, margin, y);
                    y += lineHeight;
                }
            }
        }
        finally { g?.Dispose(); }
    }

    internal static IEnumerable<string> WrapLine(string para, Func<string, double> measure, double maxWidth)
    {
        if (para.Length == 0 || measure(para) <= maxWidth) { yield return para; yield break; }
        var line = new StringBuilder();
        foreach (var word in para.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (measure(candidate) <= maxWidth) { line.Clear().Append(candidate); continue; }
            if (line.Length > 0) { yield return line.ToString(); line.Clear(); }
            // Słowo dłuższe niż linia – łamanie po znakach.
            var w = word;
            while (measure(w) > maxWidth && w.Length > 1)
            {
                int n = w.Length - 1;
                while (n > 1 && measure(w[..n]) > maxWidth) n--;
                yield return w[..n];
                w = w[n..];
            }
            line.Append(w);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    // ------------------------------------------------------------------ scalanie

    public Task<byte[]> CombineFilesAsync(IReadOnlyList<string> paths, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() =>
        {
            PdfSharpEdit.EnsureInitialized();
            using var output = new PdfDocument();
            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = paths[i];
                progress?.Report(OperationProgress.Of(i, paths.Count, Path.GetFileName(path)));
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    using var src = PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), PdfDocumentOpenMode.Import);
                    foreach (var p in src.Pages) output.AddPage(p);
                }
                else if (ImageExtensions.Contains(ext))
                {
                    foreach (var frame in ImageCodec.PrepareFrames(File.ReadAllBytes(path))) AddImagePage(output, frame, new ImageToPdfOptions { PageSize = ImagePageSize.A4, Margin = 28 });
                }
                else if (OfficeFormats.IsOffice(ext))
                {
                    // Bez Office czytelny błąd; wcześniej plik binarny trafiał do PDF jako tekst.
                    if (_office is null || !_office.IsAvailableFor(ext)) throw new InvalidOperationException("Office is not available.").WithMessageKey("Edit.Office.Required", Path.GetFileName(path));
                    var tmp = Path.Combine(Path.GetTempPath(), "zetpdf-office-" + Guid.NewGuid().ToString("N") + ".pdf");
                    try
                    {
                        _office.ConvertToPdfAsync(path, tmp, ct).GetAwaiter().GetResult();
                        using var src = PdfReader.Open(new MemoryStream(File.ReadAllBytes(tmp)), PdfDocumentOpenMode.Import);
                        foreach (var p in src.Pages) output.AddPage(p);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                }
                else
                {
                    WriteTextPages(output, ReadText(path), new TextStyle { FontFamily = "Arial", FontSize = 11 }, ct);
                }
            }
            if (output.PageCount == 0) throw new InvalidOperationException("No pages.").WithMessageKey("Edit.Error.NoPages");
            return PdfSharpEdit.Save(output);
        }, ct);
    }

    /// <summary>Tekst z pliku: UTF-8 (z BOM lub bez), w razie błędów dekodowania – Windows-1250.</summary>
    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1250).GetString(bytes);
        }
    }

    // ------------------------------------------------------------------ PDF → obrazy

    public Task<IReadOnlyList<string>> PdfToImagesAsync(byte[] pdf, ImageExportOptions options, string outputDirectory, string baseName, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Obiekty WIC/WPF mają powinowactwo wątku – cała pętla na jednym wątku (PDFium i tak działa na własnym wątku).
        return Task.Factory.StartNew<IReadOnlyList<string>>(() =>
        {
            Directory.CreateDirectory(outputDirectory);
            using var doc = _renderer.OpenAsync(pdf, null, ct).GetAwaiter().GetResult();
            var pages = string.IsNullOrWhiteSpace(options.PageRange) ? Enumerable.Range(0, doc.PageCount).ToList() : PageRange.Parse(options.PageRange, doc.PageCount).ToList();
            int dpi = Math.Clamp(options.Dpi, 36, 1200);
            var files = new List<string>();
            var safeBase = SafeName(baseName);
            BitmapEncoder? multi = options.Format == ImageFormat.Tiff && options.MultiPageTiff ? ImageCodec.CreateEncoder(ImageFormat.Tiff, options.JpegQuality) : null;
            int digits = Math.Max(1, doc.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(i, pages.Count, _loc?.Format("Edit.Convert.Progress.Page", pages[i] + 1)));
                var bmp = doc.RenderPageAsync(pages[i], new RenderRequest(dpi / 72.0), ct).GetAwaiter().GetResult();
                var frame = ImageCodec.Frame(bmp, dpi, options.Format);
                if (multi is not null) { multi.Frames.Add(frame); continue; }
                var enc = ImageCodec.CreateEncoder(options.Format, options.JpegQuality);
                enc.Frames.Add(frame);
                var name = (string.IsNullOrWhiteSpace(options.FileNamePattern) ? "{name}_{page}" : options.FileNamePattern)
                    .Replace("{name}", safeBase).Replace("{page}", (pages[i] + 1).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture));
                files.Add(Save(enc, Path.Combine(outputDirectory, SafeName(name) + ImageCodec.Extension(options.Format))));
            }
            if (multi is not null && multi.Frames.Count > 0) files.Add(Save(multi, Path.Combine(outputDirectory, safeBase + ".tif")));
            progress?.Report(OperationProgress.Of(pages.Count, pages.Count));
            return files;
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static string Save(BitmapEncoder enc, string path)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "document").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "document" : s;
    }

    // ------------------------------------------------------------------ PDF → TXT

    public Task<string> PdfToTextAsync(byte[] pdf, TextExportOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() =>
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
            var pages = string.IsNullOrWhiteSpace(options.PageRange) ? Enumerable.Range(0, doc.NumberOfPages).ToList() : PageRange.Parse(options.PageRange, doc.NumberOfPages).ToList();
            var sb = new StringBuilder();
            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(i, pages.Count));
                if (i > 0 && options.PageSeparator) sb.Append('\f').Append(Environment.NewLine);
                var lines = TextLines.Build(doc.GetPage(pages[i] + 1), pages[i]).OrderByDescending(l => l.Baseline.Y).ThenBy(l => l.Bounds.Left).ToList();
                if (options.PreserveLayout) AppendLayout(sb, lines);
                else foreach (var l in lines) sb.Append(l.Text).Append(Environment.NewLine);
            }
            return sb.ToString();
        }, ct);
    }

    /// <summary>Układ: siatka znaków (kolumna = x / średnia szerokość znaku, puste linie wg odstępów pionowych).</summary>
    private static void AppendLayout(StringBuilder sb, List<TextLine> lines)
    {
        if (lines.Count == 0) return;
        var letters = lines.SelectMany(l => l.Words.SelectMany(w => w.Letters)).ToList();
        double charW = Math.Max(1, letters.Average(l => Math.Max(0.1, l.Width)));
        double minX = lines.Min(l => l.Bounds.Left);
        double lineH = lines.Average(l => l.FontSize) * 1.2;
        // Linie z tą samą linią bazową (np. kolumny) łączymy w jeden wiersz.
        var rows = new List<List<TextLine>>();
        foreach (var l in lines)
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row[0].Baseline.Y - l.Baseline.Y) < l.FontSize * 0.3) row.Add(l);
            else rows.Add(new List<TextLine> { l });
        }
        double? prevY = null;
        foreach (var row in rows)
        {
            if (prevY is { } py)
            {
                int blank = (int)Math.Round((py - row[0].Baseline.Y) / lineH) - 1;
                for (int k = 0; k < Math.Clamp(blank, 0, 5); k++) sb.Append(Environment.NewLine);
            }
            var text = new StringBuilder();
            foreach (var w in row.SelectMany(l => l.Words).OrderBy(w => w.BoundingBox.Left))
            {
                int col = (int)Math.Round((w.BoundingBox.Left - minX) / charW);
                if (text.Length < col) text.Append(' ', col - text.Length);
                else if (text.Length > 0) text.Append(' ');
                text.Append(w.Text);
            }
            sb.Append(text.ToString().TrimEnd()).Append(Environment.NewLine);
            prevY = row[0].Baseline.Y;
        }
    }

    // ------------------------------------------------------------------ PDF → DOCX

    public Task PdfToDocxAsync(byte[] pdf, string outputPath, DocxExportOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(async () =>
        {
            var tmp = outputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                await DocxWriter.WriteAsync(pdf, tmp, options, _renderer, progress, ct).ConfigureAwait(false);
                File.Move(tmp, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }, ct);
    }

    // ------------------------------------------------------------------ PDF → HTML

    public Task<string> PdfToHtmlAsync(byte[] pdf, HtmlExportOptions options, string fallbackTitle, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => HtmlWriter.Write(pdf, options, fallbackTitle, progress, ct), ct);
    }

    // ------------------------------------------------------------------ PDF → XLSX

    public Task<XlsxExportResult> PdfToXlsxAsync(byte[] pdf, string outputPath, XlsxExportOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() =>
        {
            var tmp = outputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                var result = XlsxWriter.Write(pdf, tmp, options, XlsxSheetLabels(), progress, ct);
                File.Move(tmp, outputPath, overwrite: true);
                return result;
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }, ct);
    }

    private XlsxLabels XlsxSheetLabels() => _loc is null
        ? new XlsxLabels(p => $"Page {p}", (t, p) => $"Table {t} (p. {p})", "Text", p => $"(page {p} has no text)")
        : new XlsxLabels(p => _loc.Format("Edit.Xlsx.Sheet.Page", p), (t, p) => _loc.Format("Edit.Xlsx.Sheet.Table", t, p), _loc["Edit.Xlsx.Sheet.Text"], p => _loc.Format("Edit.Xlsx.NoTextOnPage", p));
}

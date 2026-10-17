using System.Globalization;
using System.Text.Json;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Utilities;
using ZetPDF.Pages.Engine;
using ZetPDF.Core.Localization;

namespace ZetPDF.Pages.Stamping;

/// <summary>
/// Znak wodny, tło, nagłówek i stopka, numeracja stron, Bates. Tekst rysowany przez PDFsharp z czcionką systemową
/// osadzoną jako TrueType (Unicode, Identity-H + ToUnicode) – polskie znaki są wyodrębnialne. Oznaczenia: <see cref="Marks"/>.
/// </summary>
public sealed class StampingService : IStampingService
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    // ------------------------------------------------------------------ znak wodny

    public Task<byte[]> AddWatermarkAsync(byte[] pdf, WatermarkOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            bool hasText = !string.IsNullOrWhiteSpace(options.Text);
            bool hasImage = !string.IsNullOrWhiteSpace(options.ImagePath);
            if (!hasText && !hasImage) throw new ArgumentException(PagesModule.Text("Pages.Error.WatermarkEmpty")).WithMessageKey("Pages.Error.WatermarkEmpty");
            using var doc = PdfIo.OpenModify(pdf);
            var pages = PageRange.Parse(options.PageRange, doc.PageCount);
            double opacity = Math.Clamp(options.Opacity, 0.01, 1);
            PdfDictionary form;
            double fw, fh;
            bool image = !hasText;
            if (!image)
            {
                var font = FormFactory.Font(options.Style.FontFamily, Math.Max(1, options.Style.FontSize), options.Style.Bold, options.Style.Italic);
                var lines = options.Text!.Replace("\r\n", "\n").Split('\n');
                double lineH;
                using (var mg = FormFactory.MeasureContext())
                {
                    fw = lines.Max(l => mg.MeasureString(l.Length == 0 ? " " : l, font).Width) + 2;
                    lineH = font.GetHeight();
                }
                fh = lineH * lines.Length;
                var brush = new XSolidBrush(ToXColor(options.Style.Color, opacity));
                double w = fw, lh = lineH;
                form = FormFactory.Create(doc, fw, fh, g =>
                {
                    for (int i = 0; i < lines.Length; i++)
                        g.DrawString(lines[i], font, brush, new XRect(0, i * lh, w, lh), XStringFormats.Center);
                });
            }
            else
            {
                if (!File.Exists(options.ImagePath)) throw new FileNotFoundException(PagesModule.Text("Pages.Error.ImageNotFound"), options.ImagePath).WithMessageKey("Pages.Error.ImageNotFound");
                var (img, iw, ih) = FormFactory.Image(doc, options.ImagePath!);
                var res = new PdfDictionary();
                var xo = new PdfDictionary();
                xo.Elements["/ZIm1"] = img.Reference!;
                res.Elements["/XObject"] = xo;
                form = FormFactory.CreateRaw(doc, iw, ih, $"q {PdfIo.Num(iw)} 0 0 {PdfIo.Num(ih)} 0 0 cm /ZIm1 Do Q", res);
                fw = iw;
                fh = ih;
            }
            Marks.Tag(form, Marks.Watermark, JsonSerializer.Serialize(options, Json));

            for (int k = 0; k < pages.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(k, pages.Count));
                var page = doc.Pages[pages[k]];
                var frame = PdfIo.Frame(page);
                double s = 1;
                if (image && options.ImageScale > 0) s = Math.Min(options.ImageScale * frame.Width / fw, options.ImageScale * frame.Height / fh);
                double dw = fw * s, dh = fh * s;
                double th = options.RotationDegrees * Math.PI / 180.0;
                double rw = Math.Abs(dw * Math.Cos(th)) + Math.Abs(dh * Math.Sin(th));
                double rh = Math.Abs(dw * Math.Sin(th)) + Math.Abs(dh * Math.Cos(th));
                double cx = options.Horizontal switch
                {
                    HorizontalPosition.Left => rw / 2,
                    HorizontalPosition.Right => frame.Width - rw / 2,
                    _ => frame.Width / 2,
                } + options.OffsetX;
                double cy = options.Vertical switch
                {
                    VerticalPosition.Top => frame.Height - rh / 2,
                    VerticalPosition.Bottom => rh / 2,
                    _ => frame.Height / 2,
                } + options.OffsetY;
                var local = PdfMatrix.Translation(-fw / 2, -fh / 2)
                    .Then(PdfMatrix.Scaling(s, s))
                    .Then(PdfMatrix.Rotation(options.RotationDegrees))
                    .Then(PdfMatrix.Translation(cx, cy));
                // tekst: krycie w kolorze pędzla (PDFsharp ustawia własny /ca w formularzu); obraz: krycie przez ExtGState
                Marks.Place(doc, page, form, Marks.Watermark, local, image ? opacity : 1, options.BehindContent);
            }
            progress?.Report(OperationProgress.Of(pages.Count, pages.Count));
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> RemoveWatermarksAsync(byte[] pdf, CancellationToken ct = default) => RemoveAsync(pdf, ct, Marks.Watermark);

    // ------------------------------------------------------------------ tło

    public Task<byte[]> AddBackgroundAsync(byte[] pdf, BackgroundOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            bool hasImage = !string.IsNullOrWhiteSpace(options.ImagePath);
            if (options.Color is null && !hasImage) throw new ArgumentException(PagesModule.Text("Pages.Error.BackgroundEmpty")).WithMessageKey("Pages.Error.BackgroundEmpty");
            using var doc = PdfIo.OpenModify(pdf);
            var pages = PageRange.Parse(options.PageRange, doc.PageCount);
            double opacity = Math.Clamp(options.Opacity, 0.01, 1);
            var json = JsonSerializer.Serialize(options, Json);
            PdfDictionary? colorForm = null, imageForm = null;
            double iw = 0, ih = 0;
            if (options.Color is { } c)
            {
                colorForm = FormFactory.CreateRaw(doc, 1, 1, $"{PdfIo.Num(c.R / 255.0)} {PdfIo.Num(c.G / 255.0)} {PdfIo.Num(c.B / 255.0)} rg 0 0 1 1 re f", null);
                Marks.Tag(colorForm, Marks.Background, json);
            }
            if (hasImage)
            {
                if (!File.Exists(options.ImagePath)) throw new FileNotFoundException(PagesModule.Text("Pages.Error.ImageNotFound"), options.ImagePath).WithMessageKey("Pages.Error.ImageNotFound");
                var (img, w, h) = FormFactory.Image(doc, options.ImagePath!);
                iw = w; ih = h;
                var res = new PdfDictionary();
                var xo = new PdfDictionary();
                xo.Elements["/ZIm1"] = img.Reference!;
                res.Elements["/XObject"] = xo;
                imageForm = FormFactory.CreateRaw(doc, w, h, $"q {PdfIo.Num(w)} 0 0 {PdfIo.Num(h)} 0 0 cm /ZIm1 Do Q", res);
                Marks.Tag(imageForm, Marks.Background, json);
            }
            for (int k = 0; k < pages.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(k, pages.Count));
                var page = doc.Pages[pages[k]];
                var f = PdfIo.Frame(page);
                // kolejność: obraz wstawiany pierwszy na początek, potem kolor przed nim → kolor pod obrazem
                if (imageForm is not null)
                {
                    double s = Math.Min(f.Width / iw, f.Height / ih);
                    var local = PdfMatrix.Scaling(s, s).Then(PdfMatrix.Translation((f.Width - iw * s) / 2, (f.Height - ih * s) / 2));
                    Marks.Place(doc, page, imageForm, Marks.Background, local, opacity, behind: true);
                }
                if (colorForm is not null)
                    Marks.Place(doc, page, colorForm, Marks.Background, PdfMatrix.Scaling(f.Width, f.Height), opacity, behind: true);
            }
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> RemoveBackgroundsAsync(byte[] pdf, CancellationToken ct = default) => RemoveAsync(pdf, ct, Marks.Background);

    // ------------------------------------------------------------------ nagłówek i stopka

    public Task<byte[]> AddHeaderFooterAsync(byte[] pdf, HeaderFooterOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            var pages = PageRange.Parse(options.PageRange, doc.PageCount);
            var json = JsonSerializer.Serialize(options, Json);
            var font = FormFactory.Font(options.Style.FontFamily, Math.Max(1, options.Style.FontSize), options.Style.Bold, options.Style.Italic);
            var brush = new XSolidBrush(ToXColor(options.Style.Color, 1));
            var date = FormatDate(options.DateFormat);
            int total = options.StartPageNumber - 1 + pages.Count;
            var title = doc.Info.Title ?? string.Empty;
            var fields = new (string Text, XStringFormat Format, bool Top)[]
            {
                (options.HeaderLeft, XStringFormats.TopLeft, true), (options.HeaderCenter, XStringFormats.TopCenter, true), (options.HeaderRight, XStringFormats.TopRight, true),
                (options.FooterLeft, XStringFormats.BottomLeft, false), (options.FooterCenter, XStringFormats.BottomCenter, false), (options.FooterRight, XStringFormats.BottomRight, false),
            };
            if (fields.All(f => string.IsNullOrWhiteSpace(f.Text))) throw new ArgumentException(PagesModule.Text("Pages.Error.HeaderFooterEmpty")).WithMessageKey("Pages.Error.HeaderFooterEmpty");
            for (int k = 0; k < pages.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(k, pages.Count));
                var page = doc.Pages[pages[k]];
                var f = PdfIo.Frame(page);
                int number = options.StartPageNumber + k;
                var texts = fields.Select(x => (Text: Macros.Expand(x.Text, number, total, date, title), x.Format, x.Top)).Where(x => x.Text.Length > 0).ToArray();
                if (texts.Length == 0) continue;
                var rect = new XRect(options.MarginLeft, options.MarginTop, Math.Max(1, f.Width - options.MarginLeft - options.MarginRight), Math.Max(1, f.Height - options.MarginTop - options.MarginBottom));
                var form = FormFactory.Create(doc, f.Width, f.Height, g =>
                {
                    foreach (var t in texts) DrawBlock(g, t.Text, font, brush, rect, t.Format, t.Top);
                });
                Marks.Tag(form, Marks.HeaderFooter, k == 0 ? json : null);
                Marks.Place(doc, page, form, Marks.HeaderFooter, PdfMatrix.Identity, 1, behind: false);
            }
            return PdfIo.Save(doc);
        }, ct);

    public Task<byte[]> RemoveHeaderFooterAsync(byte[] pdf, CancellationToken ct = default) => RemoveAsync(pdf, ct, Marks.HeaderFooter);

    // ------------------------------------------------------------------ numeracja stron i Bates

    public Task<byte[]> AddPageNumbersAsync(byte[] pdf, PageNumberOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            var pages = PageRange.Parse(options.PageRange, doc.PageCount);
            var json = JsonSerializer.Serialize(options, Json);
            var date = FormatDate("d");
            int total = options.StartNumber - 1 + pages.Count;
            var title = doc.Info.Title ?? string.Empty;
            for (int k = 0; k < pages.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                var text = Macros.Expand(options.Format, options.StartNumber + k, total, date, title);
                DrawPositioned(doc, doc.Pages[pages[k]], Marks.PageNumbers, text, options.Style, options.Horizontal, options.Vertical, options.Margin, k == 0 ? json : null);
            }
            return PdfIo.Save(doc);
        }, ct);

    /// <summary>Usuwa numerację stron (polecenie w oknie Numeracja stron).</summary>
    public Task<byte[]> RemovePageNumbersAsync(byte[] pdf, CancellationToken ct = default) => RemoveAsync(pdf, ct, Marks.PageNumbers);

    public Task<BatesResult> AddBatesAsync(byte[] pdf, BatesOptions options, long startNumber, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (startNumber < 0) throw new ArgumentOutOfRangeException(nameof(startNumber));
            using var doc = PdfIo.OpenModify(pdf);
            var json = JsonSerializer.Serialize(options, Json);
            int digits = Math.Clamp(options.Digits, 1, 18);
            long n = startNumber;
            for (int i = 0; i < doc.PageCount; i++, n++)
            {
                ct.ThrowIfCancellationRequested();
                var text = options.Prefix + n.ToString(new string('0', digits), CultureInfo.InvariantCulture) + options.Suffix;
                DrawPositioned(doc, doc.Pages[i], Marks.Bates, text, options.Style, options.Horizontal, options.Vertical, options.Margin, i == 0 ? json : null);
            }
            return new BatesResult(PdfIo.Save(doc), startNumber, n - 1);
        }, ct);

    public Task<byte[]> RemoveBatesAsync(byte[] pdf, CancellationToken ct = default) => RemoveAsync(pdf, ct, Marks.Bates);

    public Task<PageMarkKind> DetectMarksAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenImport(pdf);
            return Marks.Detect(doc);
        }, ct);

    /// <summary>Opcje ostatnio dodanego oznaczenia (do „Aktualizuj”) lub null.</summary>
    public Task<T?> GetStoredOptionsAsync<T>(byte[] pdf, PageMarkKind kind, CancellationToken ct = default) where T : class =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenImport(pdf);
            var json = Marks.ReadOptions(doc, Marks.KindName(kind));
            if (json is null) return null;
            try { return JsonSerializer.Deserialize<T>(json, Json); }
            catch (JsonException) { return null; }
        }, ct);

    /// <summary>Usuwa oznaczenia rodzaju i dodaje nowe w jednym kroku (Aktualizuj).</summary>
    internal Task<byte[]> RemoveAsync(byte[] pdf, CancellationToken ct, params string[] kinds) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            if (Marks.Remove(doc, kinds.ToHashSet()) == 0) return pdf;
            return PdfIo.Save(doc);
        }, ct);

    // ------------------------------------------------------------------ rysowanie

    private static void DrawPositioned(PdfDocument doc, PdfPage page, string kind, string text, TextStyle style, HorizontalPosition h, VerticalPosition v, double margin, string? json)
    {
        if (string.IsNullOrEmpty(text)) return;
        var f = PdfIo.Frame(page);
        var font = FormFactory.Font(style.FontFamily, Math.Max(1, style.FontSize), style.Bold, style.Italic);
        var brush = new XSolidBrush(ToXColor(style.Color, 1));
        var format = (v, h) switch
        {
            (VerticalPosition.Top, HorizontalPosition.Left) => XStringFormats.TopLeft,
            (VerticalPosition.Top, HorizontalPosition.Center) => XStringFormats.TopCenter,
            (VerticalPosition.Top, HorizontalPosition.Right) => XStringFormats.TopRight,
            (VerticalPosition.Middle, HorizontalPosition.Left) => XStringFormats.CenterLeft,
            (VerticalPosition.Middle, HorizontalPosition.Center) => XStringFormats.Center,
            (VerticalPosition.Middle, HorizontalPosition.Right) => XStringFormats.CenterRight,
            (VerticalPosition.Bottom, HorizontalPosition.Left) => XStringFormats.BottomLeft,
            (VerticalPosition.Bottom, HorizontalPosition.Center) => XStringFormats.BottomCenter,
            _ => XStringFormats.BottomRight,
        };
        var rect = new XRect(margin, margin, Math.Max(1, f.Width - 2 * margin), Math.Max(1, f.Height - 2 * margin));
        var form = FormFactory.Create(doc, f.Width, f.Height, g => DrawBlock(g, text, font, brush, rect, format, v != VerticalPosition.Bottom));
        Marks.Tag(form, kind, json);
        Marks.Place(doc, page, form, kind, PdfMatrix.Identity, 1, behind: false);
    }

    /// <summary>Tekst wielowierszowy w prostokącie (wiersze od góry dla nagłówka, od dołu dla stopki).</summary>
    private static void DrawBlock(XGraphics g, string text, XFont font, XBrush brush, XRect rect, XStringFormat format, bool fromTop)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        double lh = font.GetHeight();
        if (format.LineAlignment == XLineAlignment.Center)
        {
            double top = rect.Top + (rect.Height - lh * lines.Length) / 2;
            for (int i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], font, brush, new XRect(rect.Left, top + i * lh, rect.Width, lh), new XStringFormat { Alignment = format.Alignment, LineAlignment = XLineAlignment.Center });
            return;
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = fromTop ? lines[i] : lines[lines.Length - 1 - i];
            var r = fromTop
                ? new XRect(rect.Left, rect.Top + i * lh, rect.Width, rect.Height - i * lh)
                : new XRect(rect.Left, rect.Top, rect.Width, Math.Max(lh, rect.Height - i * lh));
            g.DrawString(line, font, brush, r, format);
        }
    }

    internal static XColor ToXColor(PdfColor c, double opacity) =>
        XColor.FromArgb((int)Math.Round(Math.Clamp(c.A / 255.0 * opacity, 0, 1) * 255), c.R, c.G, c.B);

    private static string FormatDate(string format)
    {
        try { return DateTime.Now.ToString(string.IsNullOrWhiteSpace(format) ? "d" : format, CultureInfo.CurrentCulture); }
        catch (FormatException) { return DateTime.Now.ToString("d", CultureInfo.CurrentCulture); }
    }
}

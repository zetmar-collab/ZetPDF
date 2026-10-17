using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Content;
using ZetPDF.Editing.Imaging;
using ZetPDF.Editing.Pdf;
using ZetPDF.Ocr.Pdf;

namespace ZetPDF.Editing;

/// <summary>
/// Edycja obiektów strony: obrazy i formularze (operator Do z macierzą CTM), ścieżki (kształty) i bloki tekstu (BT…ET)
/// wykrywane interpreterem <see cref="ContentWalker"/>. Przekształcenie = owinięcie zakresu operatorów w "q M cm … Q",
/// gdzie M = CTM·T·CTM⁻¹ (T działa w przestrzeni strony). Identyfikator obiektu jest ważny dla danych bajtów dokumentu.
/// </summary>
public sealed class ObjectEditService : IObjectEditService
{
    public Task<IReadOnlyList<PageObjectInfo>> GetObjectsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PageObjectInfo>>(() => PdfSharpEdit.Read(pdf, doc =>
        {
            var walker = new ContentWalker(Page(doc, pageIndex));
            return walker.Objects.Select(o => new PageObjectInfo(Id(o), pageIndex, o.Kind, o.Bounds, o.Matrix)).ToList();
        }), ct);

    private static string Id(ContentObject o) => string.Create(CultureInfo.InvariantCulture, $"{o.Kind}:{o.Start}:{o.End}");

    private static PdfPage Page(PdfDocument doc, int index) =>
        index >= 0 && index < doc.PageCount ? doc.Pages[index] : throw new ArgumentOutOfRangeException(nameof(index));

    private static (ContentWalker Walker, ContentObject Obj) Find(PdfPage page, string id)
    {
        var walker = new ContentWalker(page);
        var obj = walker.Objects.FirstOrDefault(o => Id(o) == id) ?? throw new InvalidOperationException("Object not found: " + id);
        return (walker, obj);
    }

    public Task<byte[]> TransformObjectAsync(byte[] pdf, int pageIndex, string objectId, PdfMatrix transform, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var page = Page(doc, pageIndex);
            var (walker, obj) = Find(page, objectId);
            // Ścieżki i tekst mają współrzędne w bieżącym CTM; owinięcie z M = C·T·C⁻¹ daje nowe CTM = C·T.
            var c = obj.Matrix;
            var m = c.Then(transform).Then(Invert(c));
            PageContent.Replace(page, walker.WrapWithMatrix(obj.Start, obj.End, m));
        }, ct);

    public Task<byte[]> DeleteObjectAsync(byte[] pdf, int pageIndex, string objectId, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var page = Page(doc, pageIndex);
            var (walker, obj) = Find(page, objectId);
            PageContent.Replace(page, walker.RemoveRange(obj.Start, obj.End));
        }, ct);

    public Task<byte[]> ReplaceImageAsync(byte[] pdf, int pageIndex, string objectId, byte[] imageData, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var page = Page(doc, pageIndex);
            var (walker, obj) = Find(page, objectId);
            if (obj.Kind != PageObjectKind.Image) throw new InvalidOperationException("Not an image object.");
            var (name, w, h) = AddImageResource(doc, page, imageData);
            // Dopasowanie z zachowaniem proporcji wewnątrz dotychczasowego prostokąta obrazu (kwadrat jednostkowy).
            var c = obj.Matrix;
            double oldW = Math.Sqrt(c.A * c.A + c.B * c.B), oldH = Math.Sqrt(c.C * c.C + c.D * c.D);
            double newAspect = (double)w / h, oldAspect = oldH > 0 ? oldW / oldH : 1;
            PdfMatrix unit;
            if (newAspect > oldAspect) { double sy = oldAspect / newAspect; unit = new PdfMatrix(1, 0, 0, sy, 0, (1 - sy) / 2); }
            else { double sx = newAspect / oldAspect; unit = new PdfMatrix(sx, 0, 0, 1, (1 - sx) / 2, 0); }
            PageContent.Replace(page, walker.ReplaceDo(obj.Start, name, unit));
        }, ct);

    public Task<byte[]> AddImageAsync(byte[] pdf, int pageIndex, byte[] imageData, PdfRect rect, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            if (rect.IsEmpty) throw new ArgumentException("Empty rectangle.", nameof(rect));
            var page = Page(doc, pageIndex);
            var (name, _, _) = AddImageResource(doc, page, imageData);
            PageContent.Append(page, Encoding.ASCII.GetBytes($"q {PageContent.Matrix(rect.Width, 0, 0, rect.Height, rect.Left, rect.Bottom)} cm {name} Do Q\n"));
        }, ct);

    public Task<byte[]> AddShapeAsync(byte[] pdf, int pageIndex, ShapeSpec shape, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            ArgumentNullException.ThrowIfNull(shape);
            var page = Page(doc, pageIndex);
            PageContent.Append(page, Encoding.ASCII.GetBytes(ShapeOps(shape)));
        }, ct);

    internal static string ShapeOps(ShapeSpec s)
    {
        var sb = new StringBuilder("q\n");
        string N(double v) => PageContent.Num(v);
        if (s.Fill is { } f) sb.Append($"{N(f.R / 255.0)} {N(f.G / 255.0)} {N(f.B / 255.0)} rg\n");
        if (s.Stroke is { } k) sb.Append($"{N(k.R / 255.0)} {N(k.G / 255.0)} {N(k.B / 255.0)} RG {N(Math.Max(0, s.LineWidth))} w\n");
        var r = s.Rect;
        switch (s.Kind)
        {
            case ShapeKind.Line:
                sb.Append($"{N(r.Left)} {N(r.Bottom)} m {N(r.Right)} {N(r.Top)} l\n");
                sb.Append(s.Stroke is null ? "n\n" : "S\n");
                sb.Append("Q\n");
                return sb.ToString();
            case ShapeKind.Ellipse:
            {
                const double kappa = 0.5522847498;
                double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2, rx = r.Width / 2, ry = r.Height / 2;
                double ox = rx * kappa, oy = ry * kappa;
                sb.Append($"{N(cx + rx)} {N(cy)} m\n");
                sb.Append($"{N(cx + rx)} {N(cy + oy)} {N(cx + ox)} {N(cy + ry)} {N(cx)} {N(cy + ry)} c\n");
                sb.Append($"{N(cx - ox)} {N(cy + ry)} {N(cx - rx)} {N(cy + oy)} {N(cx - rx)} {N(cy)} c\n");
                sb.Append($"{N(cx - rx)} {N(cy - oy)} {N(cx - ox)} {N(cy - ry)} {N(cx)} {N(cy - ry)} c\n");
                sb.Append($"{N(cx + ox)} {N(cy - ry)} {N(cx + rx)} {N(cy - oy)} {N(cx + rx)} {N(cy)} c h\n");
                break;
            }
            default:
                sb.Append($"{N(r.Left)} {N(r.Bottom)} {N(r.Width)} {N(r.Height)} re\n");
                break;
        }
        sb.Append((s.Fill is not null, s.Stroke is not null) switch { (true, true) => "B\n", (true, false) => "f\n", (false, true) => "S\n", _ => "n\n" });
        sb.Append("Q\n");
        return sb.ToString();
    }

    /// <summary>Dodaje obraz (PNG/JPG/BMP/TIFF/GIF; przezroczystość PNG → SMask) do zasobów strony. Zwraca nazwę i rozmiar w pikselach.</summary>
    internal static (string Name, int Width, int Height) AddImageResource(PdfDocument doc, PdfPage page, byte[] imageData)
    {
        var prepared = ImageCodec.PrepareForPdf(imageData);
        var image = new PdfImage(doc, XImage.FromStream(new MemoryStream(prepared.Data)));
        var name = page.Resources.AddImage(image);
        return (name, prepared.Width, prepared.Height);
    }

    internal static PdfMatrix Invert(PdfMatrix m)
    {
        double det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Singular matrix.");
        double a = m.D / det, b = -m.B / det, c = -m.C / det, d = m.A / det;
        return new PdfMatrix(a, b, c, d, -(m.E * a + m.F * c), -(m.E * b + m.F * d));
    }
}

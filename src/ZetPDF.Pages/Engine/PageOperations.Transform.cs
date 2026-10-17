using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Pages.Engine;

public sealed partial class PageOperations
{
    // ------------------------------------------------------------------ kadrowanie

    public async Task<byte[]> CropAsync(byte[] pdf, IReadOnlyList<int> pages, CropOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        Dictionary<int, PdfRect?>? content = null;
        if (options.RemoveWhiteMargins)
        {
            if (_renderer is null) throw new InvalidOperationException(PagesModule.Text("Pages.Error.RendererUnavailable"));
            content = await WhiteMargins.DetectAsync(_renderer, pdf, pages, progress, ct).ConfigureAwait(false);
        }
        return await Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            foreach (var p in Distinct(pages, doc.PageCount))
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.Pages[p];
                var f = PdfIo.Frame(page);
                PdfRect display;
                if (content is not null)
                {
                    if (!content.TryGetValue(p, out var frac) || frac is null) continue; // pusta strona – bez zmian
                    var fr = frac.Value;
                    display = new PdfRect(fr.Left * f.Width, fr.Bottom * f.Height, fr.Right * f.Width, fr.Top * f.Height).Inflate(1);
                    display = new PdfRect(Math.Max(0, display.Left), Math.Max(0, display.Bottom), Math.Min(f.Width, display.Right), Math.Min(f.Height, display.Top));
                }
                else
                {
                    display = new PdfRect(options.Left, options.Bottom, f.Width - options.Right, f.Height - options.Top);
                }
                if (display.Width < 3 || display.Height < 3)
                    throw new ArgumentException(PagesModule.Text("Pages.Error.CropTooLarge"));
                var user = f.DisplayToUser(display);
                page.CropBox = PdfIo.ToPdfRectangle(user);
                if (options.ApplyToMediaBox) page.MediaBox = PdfIo.ToPdfRectangle(user);
            }
            return PdfIo.Save(doc);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ zmiana rozmiaru

    public Task<byte[]> ResizeAsync(byte[] pdf, IReadOnlyList<int> pages, ResizeOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            CheckSize(options.TargetSize);
            using var doc = PdfIo.OpenModify(pdf);
            foreach (var p in Distinct(pages, doc.PageCount))
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.Pages[p];
                int rot = PdfIo.GetRotation(page);
                var box = PdfIo.VisibleBox(page);
                double tw = options.TargetSize.Width, th = options.TargetSize.Height;
                if (rot is 90 or 270) (tw, th) = (th, tw);
                double s = options.ScaleContent ? Math.Min(tw / box.Width, th / box.Height) : 1.0;
                double tx = (tw - box.Width * s) / 2 - box.Left * s;
                double ty = (th - box.Height * s) / 2 - box.Bottom * s;
                var m = new PdfMatrix(s, 0, 0, s, tx, ty);
                var pre = $"q {PdfIo.Matrix(m)} cm {PdfIo.Num(box.Left)} {PdfIo.Num(box.Bottom)} {PdfIo.Num(box.Width)} {PdfIo.Num(box.Height)} re W n\n";
                var c1 = page.Contents.PrependContent();
                c1.CreateStream(PdfIo.Latin1(pre));
                var c2 = page.Contents.AppendContent();
                c2.CreateStream(PdfIo.Latin1("\nQ\n"));
                page.MediaBox = PdfIo.ToPdfRectangle(new PdfRect(0, 0, tw, th));
                foreach (var key in new[] { "/CropBox", "/TrimBox", "/BleedBox", "/ArtBox" }) page.Elements.Remove(key);
                TransformAnnotations(page, m);
            }
            return PdfIo.Save(doc);
        }, ct);

    private static void TransformAnnotations(PdfPage page, PdfMatrix m)
    {
        if (PdfIo.GetArray(page, "/Annots") is not { } annots) return;
        foreach (var a in annots.Elements)
        {
            if (PdfIo.Resolve(a) is not PdfDictionary d) continue;
            if (PdfIo.GetNumbers(d, "/Rect") is { Length: 4 } v)
            {
                var r = PdfRect.FromPoints(m.Transform(new PdfPoint(v[0], v[1])), m.Transform(new PdfPoint(v[2], v[3])));
                d.Elements["/Rect"] = PdfIo.NumberArray(null, r.Left, r.Bottom, r.Right, r.Top);
            }
            foreach (var key in new[] { "/QuadPoints", "/Vertices", "/L", "/CL" })
                if (PdfIo.GetArray(d, key) is { } arr) d.Elements[key] = TransformPoints(arr, m);
            if (PdfIo.GetArray(d, "/InkList") is { } ink)
            {
                var ni = new PdfArray();
                foreach (var stroke in ink.Elements)
                    if (PdfIo.Resolve(stroke) is PdfArray sa) ni.Elements.Add(TransformPoints(sa, m));
                d.Elements["/InkList"] = ni;
            }
        }
    }

    private static PdfArray TransformPoints(PdfArray arr, PdfMatrix m)
    {
        var v = arr.Elements.Select(e => PdfIo.GetNumber(e) ?? 0).ToArray();
        var res = new List<double>(v.Length);
        for (int i = 0; i + 1 < v.Length; i += 2)
        {
            var pt = m.Transform(new PdfPoint(v[i], v[i + 1]));
            res.Add(pt.X);
            res.Add(pt.Y);
        }
        return PdfIo.NumberArray(null, res.ToArray());
    }

    // ------------------------------------------------------------------ spłaszczanie

    public Task<byte[]> FlattenAsync(byte[] pdf, FlattenOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            var flattenedWidgets = new HashSet<PdfObjectID>();
            int remainingWidgets = 0;
            int pageCount = doc.PageCount;
            for (int p = 0; p < pageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(p, pageCount));
                var page = doc.Pages[p];
                if (PdfIo.GetArray(page, "/Annots") is not { } annots) continue;
                var content = new StringBuilder();
                var xobjects = PdfIo.SubResources(page.Resources, "/XObject");
                for (int i = annots.Elements.Count - 1; i >= 0; i--)
                {
                    var item = annots.Elements[i];
                    if (PdfIo.Resolve(item) is not PdfDictionary a) continue;
                    var subtype = PdfIo.GetName(a, "/Subtype");
                    bool widget = subtype == "/Widget";
                    if (widget ? !options.FormFields : !options.Annotations) { if (widget) remainingWidgets++; continue; }
                    if (subtype == "/Link" && options.KeepLinks) continue;
                    int flags = (int)(PdfIo.GetNumber(a.Elements["/F"]) ?? 0);
                    bool invisible = (flags & 2) != 0 || (flags & 32) != 0 || subtype == "/Popup";
                    if (!invisible)
                    {
                        var ap = Appearance(a) ?? (widget ? GenerateFieldAppearance(doc, a) : null);
                        if (ap is null && !widget) continue; // komentarz bez wyglądu – zostaje jako adnotacja (pole bez wyglądu jest usuwane)
                        if (ap is not null && PlaceAppearance(a, ap) is { } m)
                        {
                            ap.Elements["/Type"] = new PdfName("/XObject");
                            ap.Elements["/Subtype"] = new PdfName("/Form");
                            var name = PdfIo.UniqueName(xobjects, "ZFl");
                            if (ap.Reference is null) doc.Internals.AddObject(ap);
                            xobjects.Elements[name] = ap.Reference!;
                            content.Insert(0, $"q {PdfIo.Matrix(m)} cm {name} Do Q\n");
                        }
                    }
                    if (widget && item is PdfReference wr) flattenedWidgets.Add(wr.ObjectID);
                    annots.Elements.RemoveAt(i);
                }
                if (content.Length > 0) PdfIo.AppendContent(page, PdfIo.Latin1("q\n" + content + "Q\n"));
                if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
            }
            if (options.FormFields && PdfIo.GetDict(doc.Internals.Catalog, "/AcroForm") is { } acro)
            {
                References.RemoveFields(doc, flattenedWidgets, new HashSet<PdfObjectID>());
                if (remainingWidgets == 0 || (PdfIo.GetArray(acro, "/Fields")?.Elements.Count ?? 0) == 0)
                    doc.Internals.Catalog.Elements.Remove("/AcroForm");
            }
            progress?.Report(OperationProgress.Of(pageCount, pageCount));
            return PdfIo.Save(doc);
        }, ct);

    /// <summary>
    /// Normalny wygląd adnotacji (/AP /N, ze stanem /AS dla pól wyboru/opcji) lub null. Brak /AS (niezgodne z normą,
    /// ale spotykane) – stan z wartości pola /V (dziedziczonej z rodzica); stan bez wyglądu (np. /Off) = nic nie rysujemy.
    /// </summary>
    internal static PdfDictionary? Appearance(PdfDictionary annot)
    {
        if (PdfIo.GetDict(annot, "/AP") is not { } ap) return null;
        var n = PdfIo.Resolve(ap.Elements["/N"]);
        if (n is PdfDictionary nd && nd.Stream is not null) return nd;
        if (n is PdfDictionary states)
        {
            var state = PdfIo.GetName(annot, "/AS") ?? (Inherited(annot, "/V") as PdfName)?.Value;
            if (state is not null && PdfIo.Resolve(states.Elements[state]) is PdfDictionary sd && sd.Stream is not null) return sd;
        }
        return null;
    }

    /// <summary>Macierz umieszczająca wygląd w /Rect (ISO 32000-1, 12.5.5, algorytm 8.1) lub null.</summary>
    internal static PdfMatrix? PlaceAppearance(PdfDictionary annot, PdfDictionary ap)
    {
        if (PdfIo.GetNumbers(annot, "/Rect") is not { Length: 4 } rv) return null;
        var rect = PdfRect.FromPoints(new PdfPoint(rv[0], rv[1]), new PdfPoint(rv[2], rv[3]));
        var bb = PdfIo.GetNumbers(ap, "/BBox");
        if (bb is not { Length: 4 }) return null;
        var mm = PdfIo.GetNumbers(ap, "/Matrix");
        var fm = mm is { Length: 6 } ? new PdfMatrix(mm[0], mm[1], mm[2], mm[3], mm[4], mm[5]) : PdfMatrix.Identity;
        var corners = new[] { new PdfPoint(bb[0], bb[1]), new PdfPoint(bb[2], bb[1]), new PdfPoint(bb[0], bb[3]), new PdfPoint(bb[2], bb[3]) }.Select(fm.Transform).ToArray();
        double l = corners.Min(c => c.X), r = corners.Max(c => c.X), b = corners.Min(c => c.Y), t = corners.Max(c => c.Y);
        if (r - l < 1e-6 || t - b < 1e-6 || rect.IsEmpty) return null;
        double sx = rect.Width / (r - l), sy = rect.Height / (t - b);
        return new PdfMatrix(sx, 0, 0, sy, rect.Left - l * sx, rect.Bottom - b * sy);
    }

    /// <summary>Wygląd pola tekstowego/listy bez /AP (np. NeedAppearances) – wartość /V czcionką z /DA.</summary>
    private static PdfDictionary? GenerateFieldAppearance(PdfDocument doc, PdfDictionary widget)
    {
        var ft = Inherited(widget, "/FT") is PdfName n ? n.Value : null;
        if (ft is not ("/Tx" or "/Ch")) return null;
        var v = Inherited(widget, "/V");
        string? value = v switch
        {
            PdfString s => s.Value,
            PdfArray arr => string.Join(", ", arr.Elements.Select(PdfIo.GetText).Where(x => x is not null)),
            PdfName nm => nm.Value.TrimStart('/'),
            _ => null,
        };
        if (string.IsNullOrEmpty(value)) return null;
        if (PdfIo.GetNumbers(widget, "/Rect") is not { Length: 4 } rv) return null;
        var rect = PdfRect.FromPoints(new PdfPoint(rv[0], rv[1]), new PdfPoint(rv[2], rv[3]));
        var da = (PdfIo.GetText(Inherited(widget, "/DA")) ?? PdfIo.GetText(PdfIo.GetDict(doc.Internals.Catalog, "/AcroForm")?.Elements["/DA"])) ?? string.Empty;
        double size = ParseFontSize(da);
        if (size <= 0) size = Math.Clamp((rect.Height - 4) * 0.7, 4, 12);
        var color = ParseColor(da);
        var form = FormFactory.Create(doc, rect.Width, rect.Height, g =>
        {
            var font = FormFactory.Font("Arial", size);
            var line = value.Replace("\r", " ").Replace("\n", " ");
            g.DrawString(line, font, new XSolidBrush(color), new XRect(2, 0, Math.Max(1, rect.Width - 4), rect.Height), XStringFormats.CenterLeft);
        });
        return form;
    }

    private static PdfItem? Inherited(PdfDictionary field, string key)
    {
        var d = field;
        for (int i = 0; i < 32 && d is not null; i++)
        {
            if (d.Elements.ContainsKey(key)) return PdfIo.Resolve(d.Elements[key]);
            d = PdfIo.GetDict(d, "/Parent")!;
        }
        return null;
    }

    private static double ParseFontSize(string da)
    {
        var parts = da.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
            if (parts[i] == "Tf" && double.TryParse(parts[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static XColor ParseColor(string da)
    {
        var parts = da.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        double N(int i) => double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 1) : 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "rg" && i >= 3) return XColor.FromArgb(255, (int)(N(i - 3) * 255), (int)(N(i - 2) * 255), (int)(N(i - 1) * 255));
            if (parts[i] == "g" && i >= 1) { int gr = (int)(N(i - 1) * 255); return XColor.FromArgb(255, gr, gr, gr); }
        }
        return XColors.Black;
    }

    // ------------------------------------------------------------------ etykiety stron

    public Task<byte[]> SetPageLabelsAsync(byte[] pdf, IReadOnlyList<PageLabelRange> ranges, CancellationToken ct = default) =>
        Task.Run(() => PageLabels.Set(pdf, ranges), ct);

    public Task<IReadOnlyList<PageLabelRange>> GetPageLabelsAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run(() => PageLabels.Get(pdf), ct);
}

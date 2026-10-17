using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Pages.Content;
using ZetPDF.Pages.Engine;
using ZetPDF.Core.Localization;

namespace ZetPDF.Pages.Optimizer;

/// <summary>Optymalizator rozmiaru i konwersja do skali szarości.</summary>
public sealed class OptimizerService : IOptimizerService
{
    private readonly IPdfRenderer? _renderer;

    public OptimizerService() : this(null) { }

    public OptimizerService(IPdfRenderer? renderer) => _renderer = renderer;

    // ------------------------------------------------------------------ optymalizacja

    public Task<OptimizeResult> OptimizeAsync(byte[] pdf, OptimizeOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            long before = pdf.LongLength;
            using var doc = PdfIo.OpenModify(pdf);
            int recompressed = 0;
            if (options.RecompressImages) recompressed = RecompressImages(doc, options, progress, ct);
            if (options.RemoveEmbeddedThumbnails)
                foreach (var page in doc.Pages) page.Elements.Remove("/Thumb");
            if (options.RemoveMetadata) RemoveMetadata(doc);
            if (options.RemoveUnusedObjects) RemoveUnusedResources(doc, ct);
            if (options.CompressStreams) CompressStreams(doc, ct);
            var after = PdfIo.Save(doc);
            // ponowny zapis może być większy (np. oryginał miał strumienie obiektów) – wtedy zostawiamy oryginał
            if (after.LongLength >= before && recompressed == 0 && !options.RemoveMetadata) after = pdf;
            progress?.Report(new OperationProgress(1));
            return new OptimizeResult(after, before, after.LongLength, recompressed);
        }, ct);

    private static IEnumerable<(PdfDictionary Dict, PdfReference? Ref)> Images(PdfDocument doc)
    {
        foreach (var obj in doc.Internals.GetAllObjects())
            if (obj is PdfDictionary d && d.Stream is not null && PdfIo.GetName(d, "/Subtype") == "/Image")
                yield return (d, d.Reference);
    }

    private static HashSet<PdfObjectID> SoftMasks(PdfDocument doc)
    {
        var set = new HashSet<PdfObjectID>();
        foreach (var (d, _) in Images(doc))
            if (d.Elements["/SMask"] is PdfReference r) set.Add(r.ObjectID);
        return set;
    }

    private static int RecompressImages(PdfDocument doc, OptimizeOptions options, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var usage = ImageUsage.Collect(doc, ct);
        var masks = SoftMasks(doc);
        var images = Images(doc).ToList();
        int count = 0;
        for (int i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(i, images.Count, PagesModule.Text("Pages.Optimize.Progress.Images")));
            var (img, r) = images[i];
            if (r is not null && masks.Contains(r.ObjectID)) continue;
            var raw = ImageCodec.Decode(img);
            if (raw is null || raw.Width * raw.Height < 32 * 32) continue;
            var scaled = raw;
            if (options.MaxImageDpi > 0 && r is not null && usage.TryGetValue(r.ObjectID, out var u) && u.W > 0 && u.H > 0)
            {
                double dpi = Math.Min(raw.Width / u.W, raw.Height / u.H);
                if (dpi > options.MaxImageDpi * 1.05)
                {
                    double f = options.MaxImageDpi / dpi;
                    scaled = ImageCodec.Downsample(raw, (int)Math.Round(raw.Width * f), (int)Math.Round(raw.Height * f));
                }
            }
            byte[] jpeg;
            try { jpeg = ImageCodec.EncodeJpeg(scaled, options.JpegQuality); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            long old = img.Stream.Value.LongLength;
            bool downsampled = !ReferenceEquals(scaled, raw);
            if (jpeg.LongLength >= old * 0.95 && !(downsampled && jpeg.LongLength < old)) continue;
            img.Stream.Value = jpeg;
            img.Elements["/Filter"] = new PdfName("/DCTDecode");
            img.Elements.Remove("/DecodeParms");
            img.Elements.SetInteger("/Width", scaled.Width);
            img.Elements.SetInteger("/Height", scaled.Height);
            img.Elements.SetInteger("/BitsPerComponent", 8);
            img.Elements.SetInteger("/Length", jpeg.Length);
            count++;
        }
        return count;
    }

    internal static int RemoveMetadata(PdfDocument doc)
    {
        int removed = doc.Info.Elements.Count;
        doc.Info.Elements.Clear();
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.ContainsKey("/Metadata")) { catalog.Elements.Remove("/Metadata"); removed++; }
        foreach (var obj in doc.Internals.GetAllObjects().OfType<PdfDictionary>())
        {
            if (ReferenceEquals(obj, catalog)) continue;
            if (obj.Elements.ContainsKey("/Metadata")) { obj.Elements.Remove("/Metadata"); removed++; }
            if (obj.Elements.ContainsKey("/PieceInfo")) { obj.Elements.Remove("/PieceInfo"); removed++; }
        }
        foreach (var page in doc.Pages)
        {
            if (page.Elements.ContainsKey("/Metadata")) { page.Elements.Remove("/Metadata"); removed++; }
            if (page.Elements.ContainsKey("/PieceInfo")) { page.Elements.Remove("/PieceInfo"); removed++; }
        }
        if (catalog.Elements.ContainsKey("/PieceInfo")) { catalog.Elements.Remove("/PieceInfo"); removed++; }
        return removed;
    }

    /// <summary>Usuwa z zasobów stron XObjecty, czcionki i ExtGState, do których treść się nie odwołuje.</summary>
    private static void RemoveUnusedResources(PdfDocument doc, CancellationToken ct)
    {
        var used = new Dictionary<PdfDictionary, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (PdfIo.GetDict(page, "/Resources") is not { } res) continue;
            if (!used.TryGetValue(res, out var names)) used[res] = names = new HashSet<string>(StringComparer.Ordinal);
            var content = PdfIo.ReadContent(page);
            var lexer = new ContentLexer(content);
            foreach (var t in lexer.Tokens())
                if (t.Kind == TokenKind.Name) names.Add(lexer.Text(t));
        }
        foreach (var (res, names) in used)
            foreach (var key in new[] { "/XObject", "/Font", "/ExtGState" })
                if (PdfIo.GetDict(res, key) is { } sub)
                    foreach (var name in sub.Elements.Keys.ToArray())
                        if (!names.Contains(name)) sub.Elements.Remove(name);
    }

    private static void CompressStreams(PdfDocument doc, CancellationToken ct)
    {
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfSharp.Pdf.PdfFlateEncodeMode.BestCompression;
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            ct.ThrowIfCancellationRequested();
            if (obj is not PdfDictionary d || d.Stream is null) continue;
            var filters = ImageCodec.Filters(d);
            try
            {
                if (filters.Length == 0)
                {
                    var data = d.Stream.Value;
                    if (data.Length < 64) continue;
                    var z = ImageCodec.Deflate(data);
                    if (z.Length >= data.Length) continue;
                    d.Stream.Value = z;
                    d.Elements["/Filter"] = new PdfName("/FlateDecode");
                    d.Elements.SetInteger("/Length", z.Length);
                }
                else if (filters.Length == 1 && filters[0] == "/FlateDecode" && !d.Elements.ContainsKey("/DecodeParms"))
                {
                    var data = d.Stream.Value;
                    var z = ImageCodec.Deflate(ImageCodec.Inflate(data));
                    if (z.Length >= data.Length) continue;
                    d.Stream.Value = z;
                    d.Elements.SetInteger("/Length", z.Length);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { /* uszkodzony strumień – zostaje */ }
        }
    }

    // ------------------------------------------------------------------ skala szarości

    public async Task<byte[]> ConvertToGrayscaleAsync(byte[] pdf, bool rasterize, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        if (rasterize) return await RasterizeGrayAsync(pdf, progress, ct).ConfigureAwait(false);
        return await Task.Run(() => VectorGray(pdf, progress, ct), ct).ConfigureAwait(false);
    }

    private static byte[] VectorGray(byte[] pdf, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var doc = PdfIo.OpenModify(pdf);
        // 1. obrazy
        var images = Images(doc).ToList();
        for (int i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(i, images.Count * 2));
            GrayImage(images[i].Dict);
        }
        // 2. treść stron (stan przestrzeni barw przechodzi między strumieniami jednej strony)
        int pc = doc.PageCount;
        for (int p = 0; p < pc; p++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(OperationProgress.Of(images.Count + p * images.Count / Math.Max(1, pc), images.Count * 2));
            var page = doc.Pages[p];
            var res = PdfIo.GetDict(page, "/Resources");
            var conv = new GrayscaleContent();
            foreach (var c in PdfIo.ContentDicts(page).ToList())
            {
                var data = PdfIo.StreamData(c);
                var nd = conv.Convert(data, res);
                if (!ReferenceEquals(nd, data)) PdfIo.SetStreamData(c, nd);
            }
            if (PdfIo.GetArray(page, "/Annots") is { } annots)
                foreach (var a in annots.Elements)
                    if (PdfIo.Resolve(a) is PdfDictionary ad)
                        foreach (var key in new[] { "/C", "/IC" }) GrayColorArray(ad, key);
        }
        // 3. formularze (XObject, wyglądy adnotacji), wzorce kafelkowe, cieniowania
        foreach (var obj in doc.Internals.GetAllObjects().OfType<PdfDictionary>().ToList())
        {
            ct.ThrowIfCancellationRequested();
            bool form = obj.Stream is not null && (PdfIo.GetName(obj, "/Subtype") == "/Form" || (obj.Elements.ContainsKey("/BBox") && PdfIo.GetName(obj, "/Subtype") != "/Image"));
            bool tiling = obj.Stream is not null && PdfIo.GetNumber(obj.Elements["/PatternType"]) == 1;
            if (form || tiling)
            {
                var data = PdfIo.StreamData(obj);
                if (data.Length == 0) continue;
                var nd = new GrayscaleContent().Convert(data, PdfIo.GetDict(obj, "/Resources"));
                if (!ReferenceEquals(nd, data)) PdfIo.SetStreamData(obj, nd);
            }
            if (obj.Elements.ContainsKey("/ShadingType")) GrayShading(obj);
            if (PdfIo.GetNumber(obj.Elements["/PatternType"]) == 2 && PdfIo.GetDict(obj, "/Shading") is { } sh) GrayShading(sh);
        }
        progress?.Report(new OperationProgress(1));
        return PdfIo.Save(doc);
    }

    private static void GrayImage(PdfDictionary img)
    {
        var cs = PdfIo.Resolve(img.Elements["/ColorSpace"]);
        // Indexed: przeliczamy paletę
        if (cs is PdfArray a && a.Elements.Count == 4 && PdfIo.Resolve(a.Elements[0]) is PdfName { Value: "/Indexed" or "/I" })
        {
            var baseCh = ImageCodec.Channels(a.Elements[1]);
            bool cmyk = PdfIo.Resolve(a.Elements[1]) is PdfName { Value: "/DeviceCMYK" };
            if (baseCh != 3 && !cmyk) return;
            int hival = (int)(PdfIo.GetNumber(a.Elements[2]) ?? 0);
            byte[]? lookup = PdfIo.Resolve(a.Elements[3]) switch
            {
                PdfString s => Encoding.Latin1.GetBytes(s.Value),
                PdfDictionary sd => PdfIo.StreamData(sd),
                _ => null,
            };
            int n = cmyk ? 4 : 3;
            if (lookup is null || lookup.Length < (hival + 1) * n) return;
            var gray = new byte[hival + 1];
            for (int i = 0; i <= hival; i++)
            {
                int o = i * n;
                gray[i] = cmyk
                    ? (byte)Math.Round(GrayscaleContent.GrayCmyk(lookup[o] / 255.0, lookup[o + 1] / 255.0, lookup[o + 2] / 255.0, lookup[o + 3] / 255.0) * 255)
                    : ImageCodec.Luma(lookup[o], lookup[o + 1], lookup[o + 2]);
            }
            var na = new PdfArray();
            na.Elements.Add(new PdfName("/Indexed"));
            na.Elements.Add(new PdfName("/DeviceGray"));
            na.Elements.Add(new PdfInteger(hival));
            na.Elements.Add(new PdfString(Encoding.Latin1.GetString(gray), PdfStringEncoding.RawEncoding));
            img.Elements["/ColorSpace"] = na;
            return;
        }
        var ch = ImageCodec.Channels(cs);
        if (ch != 3) return;
        var raw = ImageCodec.Decode(img);
        if (raw is null) return;
        ImageCodec.Write(img, ImageCodec.ToGray(raw), raw.WasJpeg, 90, setGray: true);
    }

    private static void GrayColorArray(PdfDictionary d, string key)
    {
        if (PdfIo.GetArray(d, key) is not { } arr) return;
        var v = arr.Elements.Select(e => PdfIo.GetNumber(e) ?? 0).ToArray();
        double? g = v.Length switch { 3 => GrayscaleContent.Gray(v[0], v[1], v[2]), 4 => GrayscaleContent.GrayCmyk(v[0], v[1], v[2], v[3]), _ => null };
        if (g is not null) d.Elements[key] = PdfIo.NumberArray(null, g.Value);
    }

    /// <summary>Cieniowania z funkcjami wykładniczymi (typ 2) i składanymi (typ 3) – przeliczenie C0/C1.</summary>
    private static void GrayShading(PdfDictionary sh)
    {
        var ch = ImageCodec.Channels(sh.Elements["/ColorSpace"]);
        if (ch != 3) return;
        var fn = PdfIo.Resolve(sh.Elements["/Function"]);
        if (fn is not PdfDictionary fd || !GrayFunction(fd, 0)) return;
        sh.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
    }

    private static bool GrayFunction(PdfDictionary fd, int depth)
    {
        if (depth > 8) return false;
        var type = PdfIo.GetNumber(fd.Elements["/FunctionType"]);
        if (type == 2)
        {
            foreach (var key in new[] { "/C0", "/C1" })
            {
                var arr = PdfIo.GetArray(fd, key);
                double[] v = arr?.Elements.Select(e => PdfIo.GetNumber(e) ?? 0).ToArray() ?? (key == "/C0" ? new[] { 0.0, 0, 0 } : new[] { 1.0, 1, 1 });
                if (v.Length != 3) return false;
                fd.Elements[key] = PdfIo.NumberArray(null, GrayscaleContent.Gray(v[0], v[1], v[2]));
            }
            return true;
        }
        if (type == 3 && PdfIo.GetArray(fd, "/Functions") is { } fns)
            return fns.Elements.All(f => PdfIo.Resolve(f) is PdfDictionary sub && GrayFunction(sub, depth + 1));
        return false;
    }

    /// <summary>Rasteryzacja: każda strona → obraz w skali szarości 150 DPI (tekst przestaje być tekstem).</summary>
    private async Task<byte[]> RasterizeGrayAsync(byte[] pdf, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (_renderer is null) throw new InvalidOperationException(PagesModule.Text("Pages.Error.RendererUnavailable")).WithMessageKey("Pages.Error.RendererUnavailable");
        var bitmaps = new List<RawImage>();
        using (var rdoc = await _renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false))
        {
            for (int p = 0; p < rdoc.PageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(p, rdoc.PageCount));
                var size = rdoc.Pages[p].DisplaySize;
                double scale = Math.Min(150.0 / 72.0, 4000.0 / Math.Max(1, Math.Max(size.Width, size.Height)));
                var bmp = await rdoc.RenderPageAsync(p, new RenderRequest(scale, Flags: RenderFlags.Annotations | RenderFlags.FormFields | RenderFlags.Printing), ct).ConfigureAwait(false);
                var gray = new byte[bmp.Width * bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int o = y * bmp.Stride + x * 4;
                        gray[y * bmp.Width + x] = ImageCodec.Luma(bmp.Bgra[o + 2], bmp.Bgra[o + 1], bmp.Bgra[o]);
                    }
                bitmaps.Add(new RawImage(bmp.Width, bmp.Height, 1, gray, false));
            }
        }
        return await Task.Run(() =>
        {
            using var doc = PdfIo.OpenModify(pdf);
            for (int p = 0; p < doc.PageCount && p < bitmaps.Count; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.Pages[p];
                var f = PdfIo.Frame(page);
                var img = PdfIo.NewStream(doc, ImageCodec.EncodeJpeg(bitmaps[p], 85),
                    ("/Type", new PdfName("/XObject")), ("/Subtype", new PdfName("/Image")),
                    ("/Width", new PdfInteger(bitmaps[p].Width)), ("/Height", new PdfInteger(bitmaps[p].Height)),
                    ("/ColorSpace", new PdfName("/DeviceGray")), ("/BitsPerComponent", new PdfInteger(8)), ("/Filter", new PdfName("/DCTDecode")));
                var res = new PdfDictionary(doc);
                var xo = new PdfDictionary();
                xo.Elements["/ZRas"] = img.Reference!;
                res.Elements["/XObject"] = xo;
                page.Elements["/Resources"] = res;
                var m = PdfMatrix.Scaling(f.Width, f.Height).Then(f.ToUser);
                var content = PdfIo.NewStream(doc, PdfIo.Latin1($"q {PdfIo.Matrix(m)} cm /ZRas Do Q\n"));
                page.Elements["/Contents"] = content.Reference!;
                // adnotacje i pola są już w obrazie – zostają tylko łącza
                if (PdfIo.GetArray(page, "/Annots") is { } annots)
                {
                    for (int i = annots.Elements.Count - 1; i >= 0; i--)
                        if (PdfIo.Resolve(annots.Elements[i]) is not PdfDictionary ad || PdfIo.GetName(ad, "/Subtype") != "/Link") annots.Elements.RemoveAt(i);
                    if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
                }
            }
            doc.Internals.Catalog.Elements.Remove("/AcroForm");
            return PdfIo.Save(doc);
        }, ct).ConfigureAwait(false);
    }
}

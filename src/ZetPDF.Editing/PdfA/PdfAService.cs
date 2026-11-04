using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Editing.Imaging;
using ZetPDF.Editing.Pdf;
using ZetPDF.Editing.Text;
using ZetPDF.Ocr.Pdf;

namespace ZetPDF.Editing.PdfA;

/// <summary>
/// Sprawdzanie i konwersja do PDF/A-2b na bibliotekach permisywnych (PDFsharp, PDFium, PdfPig). Własne sprawdzenie
/// wybranych reguł, a nie certyfikowana walidacja. Plik z pdfaid powstaje tylko wtedy, gdy to sprawdzenie nie zgłasza błędów.
/// </summary>
public sealed class PdfAService : IPdfAService
{
    private readonly IPdfRenderer _renderer;
    private readonly ILocalizationService? _loc;
    private readonly Func<byte[]?> _profile;

    public PdfAService(IPdfRenderer renderer, ILocalizationService? loc = null) : this(renderer, loc, IccProfile.ReadSystemSrgb) { }

    internal PdfAService(IPdfRenderer renderer, ILocalizationService? loc, Func<byte[]?> profile)
    {
        _renderer = renderer;
        _loc = loc;
        _profile = profile;
    }

    public Task<PdfACheckReport> CheckAsync(byte[] pdf, bool savedEncrypted = false, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => PdfAChecker.Check(pdf, savedEncrypted, ct), ct);

    public Task<PdfAConversionPlan> PlanAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var facts = PdfAInspector.ReadFileLevel(pdf);
            if (facts.Encrypted) throw EncryptedInput();
            using var doc = PdfSharpEdit.OpenForImport(pdf);
            PdfAInspector.InspectDocument(doc, facts, ct);
            return BuildPlan(facts, out _);
        }, ct);

    private static Exception EncryptedInput() => new InvalidOperationException("Encrypted input.").WithMessageKey("Edit.PdfA.Error.Encrypted");

    /// <summary>Plan z faktów: czcionki da się osadzić albo ich strony idą do rasteryzacji; CMYK i treść nieczytelna też.</summary>
    private static PdfAConversionPlan BuildPlan(PdfAFacts f, out Dictionary<PdfDictionary, PdfAFontEmbedder.Candidate> embed)
    {
        embed = new(ReferenceEqualityComparer.Instance);
        var raster = new SortedSet<int>(f.CmykPages);
        raster.UnionWith(f.PostScriptPages);
        raster.UnionWith(f.UnreadablePages);
        raster.UnionWith(f.WidgetsWithoutAppearance);
        var names = new List<string>();
        foreach (var use in f.Fonts.Values.Where(u => !PdfAFontEmbedder.IsEmbedded(u.Font)))
        {
            if (PdfAFontEmbedder.TryPrepare(use.Font) is { } c)
            {
                embed[use.Font] = c;
                var line = use.Name + " → " + c.Family;
                if (!names.Contains(line)) names.Add(line);
            }
            else raster.UnionWith(use.Pages);
        }
        return new PdfAConversionPlan
        {
            PagesToRasterize = raster.ToList(),
            FontsToEmbed = names,
            Attachments = f.Attachments,
            AnnotationsToRemove = f.MultimediaAnnotations + f.AttachmentAnnotations + f.AnnotationsNoAppearance + f.AnnotationsHidden,
            ActionsToRemove = f.JavaScript + f.ForbiddenActions + f.CatalogAdditionalActions,
            HasXfa = f.HasXfa,
            IsSigned = f.Signed,
        };
    }

    public Task<PdfAConvertResult> ConvertAsync(byte[] pdf, PdfAConvertOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(async () =>
        {
            var facts = PdfAInspector.ReadFileLevel(pdf);
            if (facts.Encrypted) throw EncryptedInput();
            // Fakty i poprawki na tym samym obiekcie dokumentu: słowniki czcionek z planu są tymi, które się zmienia.
            using var doc = PdfSharpEdit.OpenForModify(pdf);
            PdfAInspector.InspectDocument(doc, facts, ct);
            var plan = BuildPlan(facts, out var embed);
            if (plan.PagesToRasterize.Count > 0 && !options.AllowRasterize) return new PdfAConvertResult(null, PdfANotConverted.NeedsRasterize, plan, null);
            if (plan.RemovesContent && !options.AllowContentRemoval) return new PdfAConvertResult(null, PdfANotConverted.RemovesContent, plan, null);
            var profile = _profile() ?? throw new InvalidOperationException("No sRGB profile.").WithMessageKey("Edit.PdfA.Error.NoProfile", IccProfile.SystemProfilePath);

            var rendered = await RenderPagesAsync(pdf, plan.PagesToRasterize, options.RasterDpi, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var descriptors = new Dictionary<string, PdfDictionary>();
            foreach (var (font, candidate) in embed) PdfAFontEmbedder.Embed(doc, font, candidate, descriptors);
            foreach (var (index, page) in rendered) ReplaceWithImage(doc, doc.Pages[index], page);
            PdfAFixer.RemoveForbiddenStructure(doc);
            PdfAFixer.WriteArchiveStructure(doc, profile);
            var bytes = PdfAMetadata.WriteToSavedFile(PdfSharpEdit.Save(doc));
            var after = PdfAChecker.Check(bytes, false, ct);
            progress?.Report(OperationProgress.Of(1, 1));
            return after.NoFailures
                ? new PdfAConvertResult(bytes, PdfANotConverted.None, plan, after)
                : new PdfAConvertResult(null, PdfANotConverted.RulesStillFailing, plan, after);
        }, ct);
    }

    private sealed record RenderedPage(byte[] Png, PdfRect Crop, List<PageTextTools.Word> Words);

    private async Task<Dictionary<int, RenderedPage>> RenderPagesAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var result = new Dictionary<int, RenderedPage>();
        if (pages.Count == 0) return result;
        using var rdoc = await _renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false);
        double scale = Math.Clamp(dpi, 72, 600) / 72.0;
        for (int n = 0; n < pages.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            int index = pages[n];
            progress?.Report(OperationProgress.Of(n, pages.Count, _loc?.Format("Edit.Convert.Progress.Page", index + 1)));
            var info = rdoc.Pages[index];
            // Obraz w układzie strony przed /Rotate, jak w redakcji: obrót strony zostaje bez zmian.
            var bmp = await rdoc.RenderPageAsync(index, new RenderRequest(scale, (360 - info.Rotation) % 360), ct).ConfigureAwait(false);
            var text = await rdoc.GetPageTextAsync(index, ct).ConfigureAwait(false);
            var crop = new PdfRect(info.CropOrigin.X, info.CropOrigin.Y, info.CropOrigin.X + info.Size.Width, info.CropOrigin.Y + info.Size.Height);
            result[index] = new RenderedPage(OpaquePng(bmp, dpi), crop, PageTextTools.Words(text));
        }
        return result;
    }

    /// <summary>PNG bez kanału alfa: transparencja w obrazie strony jest zbędna (render na białym tle).</summary>
    private static byte[] OpaquePng(RenderedBitmap bmp, int dpi)
    {
        var converted = new FormatConvertedBitmap(ImageCodec.ToBitmapSource(bmp, dpi), PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return ImageCodec.EncodePng(converted);
    }

    private static void ReplaceWithImage(PdfDocument doc, PdfPage page, RenderedPage data)
    {
        var resources = new PdfDictionary(doc);
        page.Elements["/Resources"] = resources;
        page.Elements.Remove("/Thumb");
        page.Elements.Remove("/PieceInfo");
        page.Elements.Remove("/Group");
        // Wygląd adnotacji i pól jest już na obrazie; pola znikają też z formularza dokumentu.
        PdfObjects.RemoveAnnotations(page, _ => true);
        page.Elements.Remove("/Annots");
        var image = new PdfImage(doc, XImage.FromStream(new MemoryStream(data.Png)));
        if (image.Reference is null) doc.Internals.AddObject(image);
        var xobjects = new PdfDictionary(doc);
        xobjects.Elements["/ImPage"] = image.Reference!;
        resources.Elements["/XObject"] = xobjects;
        var c = data.Crop;
        PageContent.Replace(page, Encoding.ASCII.GetBytes($"q {PageContent.Matrix(c.Width, 0, 0, c.Height, c.Left, c.Bottom)} cm /ImPage Do Q\n"));
        var words = data.Words.Select(w => new LayerWord(w.Text, new PdfPoint(w.Box.Left, w.Box.Bottom + w.Box.Height * 0.02), Math.Max(1, w.FontSize > 0 ? w.FontSize : w.Box.Height), null, w.Box.Width)).ToList();
        TextLayerWriter.Write(page, words, LayerStyle.HiddenText("pdfa-text"));
    }
}

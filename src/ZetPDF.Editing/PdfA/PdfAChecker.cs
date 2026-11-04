using System.Text;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.PdfA;

/// <summary>Reguły PDF/A-2b na podstawie faktów z <see cref="PdfAInspector"/>. Część reguł normy jest poza zasięgiem i ma status „nie sprawdzono”.</summary>
internal static class PdfAChecker
{
    public static readonly string[] NotCheckedRules = { "FontPrograms", "ContentLimits", "XmpSchemas", "FileStructure" };

    public static PdfACheckReport Check(byte[] pdf, bool savedEncrypted, CancellationToken ct = default)
    {
        var f = PdfAInspector.ReadFileLevel(pdf);
        // Plik zaszyfrowany na dysku: bez hasła nie da się go przejrzeć.
        if (f.Encrypted)
            return new PdfACheckReport(new[] { new PdfARuleResult("Encryption", PdfARuleStatus.Failed, 1) }
                .Concat(NotCheckedRules.Select(r => new PdfARuleResult(r, PdfARuleStatus.NotChecked))).ToList());
        using (var doc = Core.Documents.PdfSharpEdit.OpenForImport(pdf)) PdfAInspector.InspectDocument(doc, f, ct);
        f.Encrypted = savedEncrypted;
        return Report(f);
    }

    public static PdfACheckReport Report(PdfAFacts f)
    {
        var r = new List<PdfARuleResult>();
        void Rule(string id, int count, IReadOnlyList<string>? examples = null) =>
            r.Add(new PdfARuleResult(id, count == 0 ? PdfARuleStatus.Passed : PdfARuleStatus.Failed, count, examples));

        Rule("Header", (f.HeaderOk ? 0 : 1) + (f.BinaryCommentOk ? 0 : 1));
        Rule("Encryption", f.Encrypted ? 1 : 0);
        Rule("TrailerId", f.HasId ? 0 : 1);
        Rule("EndOfFile", f.DataAfterEof ? 1 : 0);
        Rule("Xmp", !f.HasXmp || f.XmpFiltered || f.XmpUnreadable ? 1 : 0);
        Rule("PdfaId", f.PdfaPart == "2" && string.Equals(f.PdfaConformance, "B", StringComparison.OrdinalIgnoreCase) ? 0 : 1,
            f.PdfaPart is null ? null : new[] { "part=" + f.PdfaPart + ", conformance=" + f.PdfaConformance });
        Rule("OutputIntent", f.HasOutputIntent && f.IntentProfileValid ? 0 : 1);

        var colorPages = new SortedSet<int>();
        if (f.IntentComponents != 4) colorPages.UnionWith(f.CmykPages);
        if (f.IntentComponents == 4) colorPages.UnionWith(f.RgbPages);
        Rule("DeviceColors", colorPages.Count, colorPages.Count > 0 ? new[] { Pages(colorPages) } : null);

        var unembedded = f.Fonts.Values.Where(u => !PdfAFontEmbedder.IsEmbedded(u.Font)).ToList();
        Rule("Fonts", unembedded.Count, unembedded.Select(u => u.Name + " (" + Pages(u.Pages) + ")").Take(10).ToList());

        Rule("JavaScript", f.JavaScript);
        Rule("Actions", f.ForbiddenActions + f.CatalogAdditionalActions, f.ActionNames);
        Rule("Multimedia", f.MultimediaAnnotations + f.RenditionNames + f.AlternatePresentations);
        Rule("Attachments", f.Attachments + f.AttachmentAnnotations);
        Rule("Annotations", f.AnnotationsNoAppearance + f.WidgetsWithoutAppearance.Count + f.AnnotationsHidden + f.AnnotationsBadFlags + f.AnnotationsExtraAppearance);
        Rule("Forms", (f.HasXfa ? 1 : 0) + (f.NeedAppearances ? 1 : 0));
        Rule("XObjects", f.ImagesInterpolate + f.XObjectsForbiddenKeys + f.PostScriptPages.Count);
        Rule("Filters", f.LzwStreams + f.ExternalStreams);
        Rule("GraphicsState", f.GraphicsStateProblems);
        Rule("OptionalContent", f.OptionalContentProblems);
        Rule("CatalogKeys", f.CatalogForbiddenKeys);
        Rule("PageSize", f.BadPageSizes.Count);
        Rule("OtherMetadata", f.OtherMetadata);
        if (f.UnreadablePages.Count > 0) r.Add(new PdfARuleResult("UnreadableContent", PdfARuleStatus.Failed, f.UnreadablePages.Count, new[] { Pages(f.UnreadablePages) }));
        if (f.Transparency) r.Add(new PdfARuleResult("Transparency", PdfARuleStatus.Info));
        if (f.Signed) r.Add(new PdfARuleResult("Signed", PdfARuleStatus.Info));
        r.AddRange(NotCheckedRules.Select(n => new PdfARuleResult(n, PdfARuleStatus.NotChecked)));
        return new PdfACheckReport(r);
    }

    /// <summary>Strony 1-based: „1, 2, 3…” (pierwsze dziesięć).</summary>
    public static string Pages(IEnumerable<int> pages)
    {
        var list = pages.OrderBy(p => p).ToList();
        return string.Join(", ", list.Take(10).Select(p => (p + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) + (list.Count > 10 ? ", ..." : string.Empty);
    }

    /// <summary>Raport TXT lub HTML w bieżącym języku. Przykłady i nazwa pliku pochodzą z dokumentu, więc w HTML są escapowane.</summary>
    public static string BuildReport(PdfACheckReport report, ILocalizationService loc, string fileName, bool html)
    {
        static string E(string s) => Conversion.HtmlWriter.Escape(s);
        var sb = new StringBuilder();
        string summary = loc.Format("Edit.PdfA.Summary", report.Passed, report.Failed, report.NotChecked);
        if (html)
        {
            sb.Append("<!doctype html>\n<html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
            sb.Append("<title>").Append(E(loc["Edit.PdfA.Report.Title"])).Append("</title><style>body{font-family:Arial,sans-serif;margin:2em;max-width:60em}th,td{text-align:left;padding:.3em .6em;border-bottom:1px solid #ccc;vertical-align:top}</style></head><body>\n");
            sb.Append("<h1>").Append(E(loc["Edit.PdfA.Report.Title"])).Append("</h1>\n<p>").Append(E(fileName)).Append("</p>\n<p><strong>").Append(E(loc["Edit.PdfA.Disclaimer"])).Append("</strong></p>\n<p>").Append(E(summary)).Append("</p>\n<table>\n");
            foreach (var x in report.Results)
                sb.Append("<tr><td>").Append(E(loc["Edit.PdfA.Status." + x.Status])).Append("</td><td>").Append(E(loc["Edit.PdfA.Rule." + x.Rule])).Append("</td><td>").Append(E(Detail(x, loc))).Append("</td></tr>\n");
            sb.Append("</table>\n</body></html>\n");
            return sb.ToString();
        }
        sb.AppendLine(loc["Edit.PdfA.Report.Title"]).AppendLine(fileName).AppendLine().AppendLine(loc["Edit.PdfA.Disclaimer"]).AppendLine().AppendLine(summary).AppendLine();
        foreach (var x in report.Results)
            sb.Append('[').Append(loc["Edit.PdfA.Status." + x.Status]).Append("] ").Append(loc["Edit.PdfA.Rule." + x.Rule]).Append(": ").AppendLine(Detail(x, loc));
        return sb.ToString();
    }

    public static string Detail(PdfARuleResult x, ILocalizationService loc)
    {
        var text = loc["Edit.PdfA.Rule." + x.Rule + ".Desc"];
        if (x.Examples is { Count: > 0 }) text += " " + loc.Format("Edit.PdfA.Examples", string.Join("; ", x.Examples));
        return text;
    }
}

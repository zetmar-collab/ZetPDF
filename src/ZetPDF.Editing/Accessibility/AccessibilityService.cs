using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Pdf;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ZetPDF.Editing.Accessibility;

/// <summary>
/// Podstawowe sprawdzanie ułatwień dostępu (Access.FullCheck) i naprawy (bajty → bajty, wykonywane jako polecenia
/// z Cofnij/Ponów): tytuł (Info/XMP) + /DisplayDocTitle, /Lang, /MarkInfo /Marked, /StructTreeRoot, /Alt figur,
/// /Tabs /S, /TU pól, obecność tekstu (skany → OCR), zakładki (&gt; 20 stron), kolejność tagów (ręcznie).
/// </summary>
public sealed class AccessibilityService
{
    /// <summary>Próg stron, powyżej którego dokument powinien mieć zakładki.</summary>
    public const int BookmarksPageThreshold = 20;

    private static readonly Regex XmpTitle = new(@"<dc:title>\s*<rdf:Alt>\s*<rdf:li[^>]*>(?<t>.*?)</rdf:li>", RegexOptions.Singleline | RegexOptions.Compiled);

    public Task<AccessReport> CheckAsync(byte[] pdf, CancellationToken ct = default) => Task.Run(() => Check(pdf, ct), ct);

    private static AccessReport Check(byte[] pdf, CancellationToken ct)
    {
        var results = new List<AccessCheckResult>();
        int pageCount;
        bool tagged;
        using (var doc = PdfSharpEdit.OpenForImport(pdf))
        {
            var catalog = doc.Internals.Catalog;
            pageCount = doc.PageCount;

            // tytuł i jego wyświetlanie
            string? title = NonEmpty(doc.Info.Title) ?? NonEmpty(ReadXmpTitle(doc));
            results.Add(title is null
                ? Result(AccessRule.DocTitle, AccessCheckStatus.Failed, "Edit.Access.Detail.NoTitle", canFix: true)
                : Result(AccessRule.DocTitle, AccessCheckStatus.Passed, "Edit.Access.Detail.Title", args: new object[] { title }));
            bool display = PdfObjects.Resolve(catalog.Elements["/ViewerPreferences"]) is PdfDictionary vp && vp.Elements.GetBoolean("/DisplayDocTitle");
            results.Add(display
                ? Result(AccessRule.DisplayDocTitle, AccessCheckStatus.Passed, "Edit.Access.Detail.DisplayDocTitle")
                : Result(AccessRule.DisplayDocTitle, AccessCheckStatus.Failed, "Edit.Access.Detail.NoDisplayDocTitle", canFix: true));

            // język
            string? lang = NonEmpty(catalog.Elements.GetString("/Lang"));
            results.Add(lang is null
                ? Result(AccessRule.Language, AccessCheckStatus.Failed, "Edit.Access.Detail.NoLanguage", canFix: true)
                : Result(AccessRule.Language, AccessCheckStatus.Passed, "Edit.Access.Detail.Language", args: new object[] { lang }));

            // oznakowanie i struktura
            tagged = PdfObjects.Resolve(catalog.Elements["/MarkInfo"]) is PdfDictionary mi && mi.Elements.GetBoolean("/Marked");
            results.Add(tagged
                ? Result(AccessRule.Tagged, AccessCheckStatus.Passed, "Edit.Access.Detail.Tagged")
                : Result(AccessRule.Tagged, AccessCheckStatus.Failed, "Edit.Access.Detail.NotTagged"));
            var root = PdfObjects.Resolve(catalog.Elements["/StructTreeRoot"]) as PdfDictionary;
            results.Add(root is not null
                ? Result(AccessRule.StructureTree, AccessCheckStatus.Passed, "Edit.Access.Detail.StructTree")
                : Result(AccessRule.StructureTree, AccessCheckStatus.Failed, "Edit.Access.Detail.NoStructTree"));

            // tekst alternatywny figur
            if (root is null)
                results.Add(Result(AccessRule.FigureAltText, AccessCheckStatus.Manual, "Edit.Access.Detail.FiguresUntagged"));
            else
            {
                var figures = Figures(doc).Select(f => f.Figure).ToList();
                var missing = figures.Where(f => f.Alt is null && f.ActualText is null).ToList();
                results.Add(figures.Count == 0
                    ? Result(AccessRule.FigureAltText, AccessCheckStatus.Passed, "Edit.Access.Detail.NoFigures")
                    : missing.Count == 0
                        ? Result(AccessRule.FigureAltText, AccessCheckStatus.Passed, "Edit.Access.Detail.FiguresOk", args: new object[] { figures.Count })
                        : Result(AccessRule.FigureAltText, AccessCheckStatus.Failed, "Edit.Access.Detail.FiguresMissing", args: new object[] { missing.Count, figures.Count },
                            pages: missing.Select(f => f.PageIndex).Where(p => p >= 0).Distinct().Order().ToList()));
            }

            // kolejność tabulacji
            var tabPages = new List<int>();
            int annotated = 0;
            for (int i = 0; i < doc.PageCount; i++)
            {
                if (PdfObjects.Annotations(doc.Pages[i]).Count == 0) continue;
                annotated++;
                if (doc.Pages[i].Elements.GetName("/Tabs") != "/S") tabPages.Add(i);
            }
            results.Add(annotated == 0
                ? Result(AccessRule.TabOrder, AccessCheckStatus.Passed, "Edit.Access.Detail.NoAnnotations")
                : tabPages.Count == 0
                    ? Result(AccessRule.TabOrder, AccessCheckStatus.Passed, "Edit.Access.Detail.TabOrderOk", args: new object[] { annotated })
                    : Result(AccessRule.TabOrder, AccessCheckStatus.Failed, "Edit.Access.Detail.TabOrderMissing", canFix: true, args: new object[] { tabPages.Count }, pages: tabPages));

            // opisy pól
            var fields = TerminalFields(doc).ToList();
            var noTu = fields.Where(f => NonEmpty(f.Elements.GetString("/TU")) is null).ToList();
            results.Add(fields.Count == 0
                ? Result(AccessRule.FieldDescriptions, AccessCheckStatus.Passed, "Edit.Access.Detail.NoFields")
                : noTu.Count == 0
                    ? Result(AccessRule.FieldDescriptions, AccessCheckStatus.Passed, "Edit.Access.Detail.FieldsOk", args: new object[] { fields.Count })
                    : Result(AccessRule.FieldDescriptions, AccessCheckStatus.Failed, "Edit.Access.Detail.FieldsMissing", canFix: noTu.Any(f => NonEmpty(f.Elements.GetString("/T")) is not null),
                        args: new object[] { noTu.Count, fields.Count }));

            // zakładki
            bool hasOutlines = PdfObjects.Resolve(catalog.Elements["/Outlines"]) is PdfDictionary ol && ol.Elements.ContainsKey("/First");
            results.Add(pageCount <= BookmarksPageThreshold
                ? Result(AccessRule.Bookmarks, AccessCheckStatus.Passed, "Edit.Access.Detail.BookmarksShort", args: new object[] { pageCount })
                : hasOutlines
                    ? Result(AccessRule.Bookmarks, AccessCheckStatus.Passed, "Edit.Access.Detail.BookmarksOk")
                    : Result(AccessRule.Bookmarks, AccessCheckStatus.Failed, "Edit.Access.Detail.BookmarksMissing", args: new object[] { pageCount }));
        }
        ct.ThrowIfCancellationRequested();

        // obecność tekstu (PdfPig): strona z obrazem, a bez liter = prawdopodobnie skan
        var scanned = new List<int>();
        try
        {
            using var pig = PigDocument.Open(pdf);
            foreach (var page in pig.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                int letters = page.Letters.Count(l => l.Value.Any(char.IsLetterOrDigit));
                if (letters < 3 && page.GetImages().Any()) scanned.Add(page.Number - 1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // PdfPig nie czyta pliku – reguła do sprawdzenia ręcznie
            scanned = null!;
        }
        results.Insert(results.FindIndex(r => r.Rule == AccessRule.Bookmarks), scanned is null
            ? Result(AccessRule.TextPresence, AccessCheckStatus.Manual, "Edit.Access.Detail.TextUnknown")
            : scanned.Count == 0
                ? Result(AccessRule.TextPresence, AccessCheckStatus.Passed, "Edit.Access.Detail.TextOk")
                : Result(AccessRule.TextPresence, AccessCheckStatus.Failed, "Edit.Access.Detail.TextMissing", args: new object[] { scanned.Count, PageList(scanned) }, pages: scanned));

        // kolejność tagów/odczytu – nie da się zweryfikować automatycznie
        results.Add(Result(AccessRule.ReadingOrder, AccessCheckStatus.Manual, tagged ? "Edit.Access.Detail.ReadingOrderTagged" : "Edit.Access.Detail.ReadingOrder"));
        return new AccessReport(results, pageCount, tagged);
    }

    private static AccessCheckResult Result(AccessRule rule, AccessCheckStatus status, string key, bool canFix = false, object[]? args = null, IReadOnlyList<int>? pages = null) =>
        new(rule, status, key, args ?? Array.Empty<object>(), canFix && status == AccessCheckStatus.Failed, pages ?? Array.Empty<int>());

    /// <summary>„1, 2, 5–7” (numery stron od 1).</summary>
    public static string PageList(IReadOnlyList<int> pages, int max = 12)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < pages.Count && parts.Count < max)
        {
            int start = pages[i], end = start;
            while (i + 1 < pages.Count && pages[i + 1] == end + 1) end = pages[++i];
            parts.Add(start == end ? (start + 1).ToString(CultureInfo.InvariantCulture) : $"{start + 1}–{end + 1}");
            i++;
        }
        return string.Join(", ", parts) + (i < pages.Count ? ", …" : string.Empty);
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? ReadXmpTitle(PdfDocument doc)
    {
        if (PdfObjects.Resolve(doc.Internals.Catalog.Elements["/Metadata"]) is not PdfDictionary meta || meta.Stream is null) return null;
        try
        {
            var xml = Encoding.UTF8.GetString(ZetPDF.Core.Documents.PdfStreamLimits.Decode(meta, ZetPDF.Core.Documents.PdfStreamLimits.SmallDataBytes));
            var m = XmpTitle.Match(xml);
            return m.Success ? WebUtility.HtmlDecode(m.Groups["t"].Value) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    // ------------------------------------------------------------------ struktura

    /// <summary>Elementy /Figure drzewa struktury (z RoleMap) w kolejności przejścia w głąb.</summary>
    public Task<IReadOnlyList<AccessFigure>> GetFiguresAsync(byte[] pdf, CancellationToken ct = default) => Task.Run<IReadOnlyList<AccessFigure>>(() =>
    {
        using var doc = PdfSharpEdit.OpenForImport(pdf);
        return Figures(doc).Select(f => f.Figure).ToList();
    }, ct);

    private static List<(AccessFigure Figure, PdfDictionary Element)> Figures(PdfDocument doc)
    {
        var list = new List<(AccessFigure, PdfDictionary)>();
        if (PdfObjects.Resolve(doc.Internals.Catalog.Elements["/StructTreeRoot"]) is not PdfDictionary root) return list;
        var roleMap = PdfObjects.Resolve(root.Elements["/RoleMap"]) as PdfDictionary;
        var pages = new Dictionary<PdfObjectID, int>();
        for (int i = 0; i < doc.PageCount; i++)
            if (doc.Pages[i].Reference is { } r) pages[r.ObjectID] = i;
        var visited = new HashSet<PdfObjectID>();

        string Role(string s)
        {
            for (int guard = 0; guard < 8 && roleMap is not null && roleMap.Elements.ContainsKey(s); guard++)
            {
                var mapped = roleMap.Elements.GetName(s);
                if (string.IsNullOrEmpty(mapped) || mapped == s) break;
                s = mapped;
            }
            return s;
        }

        void Walk(PdfItem? kids, int inheritedPage, int depth)
        {
            if (depth > 200) return;
            if (kids is PdfReference kr && !visited.Add(kr.ObjectID)) return;
            var item = PdfObjects.Resolve(kids);
            if (item is PdfArray arr)
            {
                foreach (var k in arr.Elements) Walk(k, inheritedPage, depth + 1);
                return;
            }
            if (item is not PdfDictionary d) return;
            var type = d.Elements.GetName("/Type");
            if (type is "/MCR" or "/OBJR") return;
            int page = inheritedPage;
            if (d.Elements["/Pg"] is PdfReference pg && pages.TryGetValue(pg.ObjectID, out int pi)) page = pi;
            var s = d.Elements.GetName("/S");
            if (!string.IsNullOrEmpty(s) && Role(s) == "/Figure")
                list.Add((new AccessFigure(list.Count, page, NonEmpty(d.Elements.GetString("/Alt")), NonEmpty(d.Elements.GetString("/ActualText"))), d));
            if (d.Elements.ContainsKey("/K")) Walk(d.Elements["/K"], page, depth + 1);
        }

        Walk(root.Elements["/K"], -1, 0);
        return list;
    }

    /// <summary>Pola końcowe AcroForm (z nazwą lub widżetem), z pominięciem samych widżetów potomnych.</summary>
    private static IEnumerable<PdfDictionary> TerminalFields(PdfDocument doc)
    {
        if (PdfObjects.Resolve(doc.Internals.Catalog.Elements["/AcroForm"]) is not PdfDictionary acro) yield break;
        if (PdfObjects.Resolve(acro.Elements["/Fields"]) is not PdfArray fields) yield break;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Field, int Depth)>();
        foreach (var f in fields.Elements.Reverse()) if (PdfObjects.Resolve(f) is PdfDictionary d) stack.Push((d, 0));
        while (stack.Count > 0)
        {
            var (field, depth) = stack.Pop();
            if (!visited.Add(field) || depth > 50) continue;
            var kids = (PdfObjects.Resolve(field.Elements["/Kids"]) as PdfArray)?.Elements.Select(PdfObjects.Resolve).OfType<PdfDictionary>().ToList();
            var childFields = kids?.Where(k => k.Elements.ContainsKey("/T")).ToList();
            if (childFields is { Count: > 0 })
            {
                for (int i = childFields.Count - 1; i >= 0; i--) stack.Push((childFields[i], depth + 1));
                continue;
            }
            yield return field;
        }
    }

    // ------------------------------------------------------------------ naprawy

    /// <summary>Naprawia wskazane reguły (tylko te, które da się naprawić automatycznie).</summary>
    public Task<byte[]> FixAsync(byte[] pdf, IReadOnlyCollection<AccessRule> rules, AccessFixOptions options, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var catalog = doc.Internals.Catalog;
            if (rules.Contains(AccessRule.DocTitle))
            {
                var title = NonEmpty(options.Title) ?? NonEmpty(doc.Info.Title) ?? NonEmpty(ReadXmpTitle(doc));
                if (title is not null) SetTitle(doc, title);
            }
            if (rules.Contains(AccessRule.DisplayDocTitle)) doc.ViewerPreferences.DisplayDocTitle = true;
            if (rules.Contains(AccessRule.Language))
            {
                var lang = NonEmpty(options.Language);
                if (lang is not null) catalog.Elements["/Lang"] = new PdfString(lang);
            }
            if (rules.Contains(AccessRule.TabOrder))
                for (int i = 0; i < doc.PageCount; i++)
                    if (PdfObjects.Annotations(doc.Pages[i]).Count > 0) doc.Pages[i].Elements.SetName("/Tabs", "/S");
            if (rules.Contains(AccessRule.FieldDescriptions))
                foreach (var f in TerminalFields(doc))
                    if (NonEmpty(f.Elements.GetString("/TU")) is null && NonEmpty(f.Elements.GetString("/T")) is { } name)
                        f.Elements["/TU"] = new PdfString(Readable(name), PdfStringEncoding.Unicode);
        }, ct);

    /// <summary>Nazwa pola jako opis: „data_urodzenia” → „data urodzenia”.</summary>
    internal static string Readable(string name) => Regex.Replace(name.Replace('_', ' ').Replace('.', ' '), @"\s+", " ").Trim();

    /// <summary>Ustawia tytuł, język, /DisplayDocTitle i teksty alternatywne figur.</summary>
    public Task<byte[]> SetPropertiesAsync(byte[] pdf, AccessProperties properties, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var catalog = doc.Internals.Catalog;
            if (properties.Title is { } t) SetTitle(doc, t.Trim());
            if (properties.Language is { } l)
            {
                if (string.IsNullOrWhiteSpace(l)) catalog.Elements.Remove("/Lang");
                else catalog.Elements["/Lang"] = new PdfString(l.Trim());
            }
            if (properties.DisplayDocTitle is { } dd) doc.ViewerPreferences.DisplayDocTitle = dd;
            if (properties.AltTexts is { Count: > 0 } alts)
                foreach (var (figure, element) in Figures(doc))
                {
                    if (!alts.TryGetValue(figure.Index, out var alt)) continue;
                    if (string.IsNullOrWhiteSpace(alt)) element.Elements.Remove("/Alt");
                    else element.Elements["/Alt"] = new PdfString(alt.Trim(), PdfStringEncoding.Unicode);
                }
        }, ct);

    private static void SetTitle(PdfDocument doc, string title)
    {
        doc.Info.Title = title;
        if (PdfObjects.Resolve(doc.Internals.Catalog.Elements["/Metadata"]) is not PdfDictionary meta || meta.Stream is null) return;
        try
        {
            var xml = Encoding.UTF8.GetString(ZetPDF.Core.Documents.PdfStreamLimits.Decode(meta, ZetPDF.Core.Documents.PdfStreamLimits.SmallDataBytes));
            var m = XmpTitle.Match(xml);
            if (!m.Success) return;
            var g = m.Groups["t"];
            xml = xml[..g.Index] + WebUtility.HtmlEncode(title) + xml[(g.Index + g.Length)..];
            meta.Elements.Remove("/Filter");
            meta.Elements.Remove("/DecodeParms");
            meta.Stream.Value = Encoding.UTF8.GetBytes(xml);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    // ------------------------------------------------------------------ raport

    /// <summary>Raport HTML lub TXT (teksty w bieżącym języku interfejsu).</summary>
    public static string BuildReport(AccessReport report, ILocalizationService loc, string documentName, bool html, DateTime? when = null)
    {
        string Status(AccessCheckStatus s) => loc["Edit.Access.Status." + s];
        string Detail(AccessCheckResult r) => r.DetailArgs.Count == 0 ? loc[r.DetailKey] : loc.Format(r.DetailKey, r.DetailArgs.ToArray());
        var date = (when ?? DateTime.Now).ToString("g", loc.CurrentCulture);
        var sb = new StringBuilder();
        if (!html)
        {
            sb.AppendLine(loc["Edit.Access.Report.Heading"]);
            sb.AppendLine(loc.Format("Edit.Access.Report.Document", documentName));
            sb.AppendLine(loc.Format("Edit.Access.Report.Date", date));
            sb.AppendLine(loc.Format("Edit.Access.Summary", report.Passed, report.Failed, report.Manual));
            sb.AppendLine();
            foreach (var r in report.Results)
            {
                sb.Append('[').Append(Status(r.Status)).Append("] ").AppendLine(loc[r.NameKey]);
                sb.Append("    ").AppendLine(Detail(r));
                if (r.Status != AccessCheckStatus.Passed) sb.Append("    ").AppendLine(loc[r.ExplainKey]);
            }
            return sb.ToString();
        }
        static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(loc.CurrentCulture.TwoLetterISOLanguageName)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
          .Append(E(loc["Edit.Access.Report.Heading"])).Append(" – ").Append(E(documentName)).Append("</title>\n")
          .Append("<style>body{font-family:'Segoe UI',sans-serif;margin:24px}table{border-collapse:collapse;width:100%}")
          .Append("th,td{border:1px solid;padding:6px 8px;text-align:left;vertical-align:top}.Failed{font-weight:bold}</style>\n</head>\n<body>\n");
        sb.Append("<h1>").Append(E(loc["Edit.Access.Report.Heading"])).Append("</h1>\n");
        sb.Append("<p>").Append(E(loc.Format("Edit.Access.Report.Document", documentName))).Append("<br>")
          .Append(E(loc.Format("Edit.Access.Report.Date", date))).Append("<br>")
          .Append(E(loc.Format("Edit.Access.Summary", report.Passed, report.Failed, report.Manual))).Append("</p>\n");
        sb.Append("<table>\n<thead><tr><th>").Append(E(loc["Edit.Access.Report.Rule"])).Append("</th><th>").Append(E(loc["Edit.Access.Report.Status"]))
          .Append("</th><th>").Append(E(loc["Edit.Access.Report.Detail"])).Append("</th></tr></thead>\n<tbody>\n");
        foreach (var r in report.Results)
        {
            sb.Append("<tr class=\"").Append(r.Status).Append("\"><td>").Append(E(loc[r.NameKey])).Append("</td><td>").Append(E(Status(r.Status))).Append("</td><td>")
              .Append(E(Detail(r)));
            if (r.Status != AccessCheckStatus.Passed) sb.Append("<br><small>").Append(E(loc[r.ExplainKey])).Append("</small>");
            sb.Append("</td></tr>\n");
        }
        sb.Append("</tbody>\n</table>\n</body>\n</html>\n");
        return sb.ToString();
    }
}

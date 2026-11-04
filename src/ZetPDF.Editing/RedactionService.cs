using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Editing.Imaging;
using ZetPDF.Editing.Pdf;
using ZetPDF.Editing.Redaction;
using ZetPDF.Editing.Text;
using ZetPDF.Ocr.Pdf;

namespace ZetPDF.Editing;

/// <summary>
/// Redakcja. Oznaczenia = adnotacje /Redact (podgląd, odwracalne). Zastosowanie (nieodwracalne poza Cofnij) –
/// METODA RASTERYZACJI stron z oznaczeniami:
/// 1) strona renderowana przez PDFium w 300 DPI (bez adnotacji, w orientacji przestrzeni użytkownika),
/// 2) obszary redakcji zamalowane w bitmapie,
/// 3) cała dotychczasowa treść strony (strumienie i zasoby: czcionki, obrazy, formularze XObject) zastąpiona jednym obrazem,
/// 4) ukryta warstwa tekstu (tryb 3) odtworzona tylko ze słów PDFium leżących poza obszarami (tekst nadal można wyszukać),
/// 5) usunięte adnotacje /Redact, adnotacje i pola przecinające obszary, miniatura strony, metadane i tytuły zakładek z frazami.
/// Treść spod obszarów nie istnieje w pliku w żadnej postaci (PDFsharp zapisuje plik od nowa, bez starych rewizji
/// i nieosiągalnych obiektów). Koszt: strona staje się obrazem (wektory i czcionki tracone, rozmiar rośnie).
/// </summary>
public sealed class RedactionService : IRedactionService
{
    public const double RasterDpi = 300;
    private readonly IPdfRenderer _renderer;
    private readonly ILocalizationService? _loc;

    public RedactionService(IPdfRenderer renderer, ILocalizationService? loc = null)
    {
        _renderer = renderer;
        _loc = loc;
    }

    public bool Validate(SensitiveDataPattern pattern, string value) => SensitivePatterns.Validate(pattern, value);

    public Task<IReadOnlyList<RedactionCandidate>> FindAsync(byte[] pdf, RedactionSearch search, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        return Task.Run<IReadOnlyList<RedactionCandidate>>(async () =>
        {
            var list = new List<RedactionCandidate>();
            using var doc = await _renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false);
            var phrases = search.Phrases.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList();
            for (int pi = 0; pi < doc.PageCount; pi++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(OperationProgress.Of(pi, doc.PageCount, _loc?.Format("Edit.Redact.Progress.Search", pi + 1, doc.PageCount)));
                var text = await doc.GetPageTextAsync(pi, ct).ConfigureAwait(false);
                var s = text.Text;
                foreach (var phrase in phrases)
                {
                    foreach (var (index, length) in FindPhrase(s, phrase, search.Options))
                    {
                        if (!search.Options.HasFlag(TextSearchOptions.WholeWord) || IsWholeWord(s, index, length))
                            Add(list, text, pi, index, length, SensitiveDataPattern.None);
                    }
                }
                if (search.Patterns != SensitiveDataPattern.None)
                    foreach (var (index, length, pattern) in SensitivePatterns.Find(s, search.Patterns))
                        Add(list, text, pi, index, length, pattern);
            }
            progress?.Report(OperationProgress.Of(doc.PageCount, doc.PageCount));
            return list;
        }, ct);
    }

    /// <summary>
    /// Trafienia frazy: suma wyszukiwania kulturowego (porównuje też litery złożone inaczej, np. „ś” i „s” + akcent)
    /// i wyrażenia dopuszczającego złamania wiersza. W redakcji pominięte trafienie zostaje w pliku, więc metody się uzupełniają.
    /// </summary>
    internal static IEnumerable<(int Index, int Length)> FindPhrase(string text, string phrase, TextSearchOptions options)
    {
        var found = new HashSet<(int, int)>();
        var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        var compareOptions = options.HasFlag(TextSearchOptions.MatchCase) ? System.Globalization.CompareOptions.None : System.Globalization.CompareOptions.IgnoreCase;
        for (int start = 0; start < text.Length;)
        {
            int index = compare.IndexOf(text.AsSpan(start), phrase.AsSpan(), compareOptions, out int length);
            if (index < 0 || length == 0) break;
            found.Add((start + index, length));
            start += index + length;
        }
        foreach (System.Text.RegularExpressions.Match m in PhraseRegex(phrase, options).Matches(text)) found.Add((m.Index, m.Length));
        return found.OrderBy(f => f.Item1);
    }

    /// <summary>
    /// Fraza jako wyrażenie: dowolny biały znak (także koniec wiersza) między słowami, a wewnątrz słowa dopuszczalne
    /// przeniesienie do następnego wiersza i znaki niewidoczne. Silnik bez nawrotów: czas liniowy także dla spreparowanego tekstu.
    /// </summary>
    internal static System.Text.RegularExpressions.Regex PhraseRegex(string phrase, TextSearchOptions options)
    {
        const string wordBreak = @"(?:[-\u2010\p{Cf}\u0002\uFFFE]?(?:[^\S\r\n]|\p{Cf})*(?:\r\n|\r|\n)(?:\s|\p{Cf})*|[\p{Cf}\u0002\uFFFE])?";
        var sb = new StringBuilder();
        bool afterSpace = true;
        foreach (var ch in phrase.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!afterSpace) sb.Append(@"(?:\s|\p{Cf})+");
                afterSpace = true;
                continue;
            }
            if (!afterSpace) sb.Append(wordBreak);
            sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
            afterSpace = false;
        }
        var regexOptions = System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking;
        if (!options.HasFlag(TextSearchOptions.MatchCase)) regexOptions |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        return new System.Text.RegularExpressions.Regex(sb.ToString(), regexOptions);
    }

    private static void Add(List<RedactionCandidate> list, PageText text, int page, int index, int length, SensitiveDataPattern pattern)
    {
        var rects = PageTextTools.RangeRects(text, index, length);
        if (rects.Count == 0) return;
        var value = System.Text.RegularExpressions.Regex.Replace(text.Text.Substring(index, length), @"\s+", " ");
        if (list.Any(c => c.PageIndex == page && c.Quads.Select(q => q.Bounds).SequenceEqual(rects))) return;
        list.Add(new RedactionCandidate(page, value, pattern, rects.Select(PdfQuad.FromRect).ToList()));
    }

    private static bool IsWholeWord(string s, int idx, int len) =>
        (idx == 0 || !char.IsLetterOrDigit(s[idx - 1])) && (idx + len >= s.Length || !char.IsLetterOrDigit(s[idx + len]));

    public Task<byte[]> MarkAsync(byte[] pdf, IReadOnlyList<RedactionMark> marks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marks);
        return PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            foreach (var mark in marks)
            {
                if (mark.PageIndex < 0 || mark.PageIndex >= doc.PageCount || mark.Quads.Count == 0) continue;
                var page = doc.Pages[mark.PageIndex];
                var bounds = mark.Quads.Select(q => q.Bounds).Aggregate((a, b) => a.Union(b));
                var annot = new PdfDictionary(doc);
                annot.Elements["/Type"] = new PdfName("/Annot");
                annot.Elements["/Subtype"] = new PdfName("/Redact");
                annot.Elements["/Rect"] = PdfObjects.RectArray(doc, bounds);
                annot.Elements["/QuadPoints"] = PdfObjects.Numbers(doc, mark.Quads.SelectMany(q => new[]
                {
                    q.UpperLeft.X, q.UpperLeft.Y, q.UpperRight.X, q.UpperRight.Y, q.LowerLeft.X, q.LowerLeft.Y, q.LowerRight.X, q.LowerRight.Y,
                }));
                annot.Elements["/IC"] = PdfObjects.Numbers(doc, new double[] { 0, 0, 0 });
                annot.Elements["/C"] = PdfObjects.Numbers(doc, new double[] { 0.85, 0.1, 0.1 });
                annot.Elements["/F"] = new PdfInteger(4);
                annot.Elements["/NM"] = new PdfString("zetpdf-redact-" + Guid.NewGuid().ToString("N"));
                annot.Elements["/M"] = new PdfString("D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z");
                if (!string.IsNullOrEmpty(mark.OverlayText)) annot.Elements["/OverlayText"] = new PdfString(mark.OverlayText, PdfStringEncoding.Unicode);
                annot.Elements["/AP"] = OutlineAppearance(doc, bounds, mark.Quads);
                PdfObjects.AddAnnotation(page, annot);
            }
        }, ct);
    }

    /// <summary>Wygląd oznaczenia (czerwony obrys obszarów) – żeby podgląd był widoczny także w innych przeglądarkach.</summary>
    private static PdfDictionary OutlineAppearance(PdfDocument doc, PdfRect bounds, IReadOnlyList<PdfQuad> quads)
    {
        var sb = new StringBuilder("q 0.85 0.1 0.1 RG 1 w\n");
        foreach (var q in quads)
        {
            var b = q.Bounds;
            sb.Append($"{PageContent.Num(b.Left)} {PageContent.Num(b.Bottom)} {PageContent.Num(b.Width)} {PageContent.Num(b.Height)} re S\n");
        }
        sb.Append("Q\n");
        var form = new PdfDictionary(doc);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = PdfObjects.RectArray(doc, bounds.Inflate(1));
        form.CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        doc.Internals.AddObject(form);
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form.Reference!;
        return ap;
    }

    /// <summary>Oznaczenia /Redact w dokumencie (strona → obszary).</summary>
    public static Dictionary<int, List<PdfRect>> ReadMarks(PdfDocument doc)
    {
        var map = new Dictionary<int, List<PdfRect>>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            foreach (var a in PdfObjects.Annotations(doc.Pages[i]).Where(a => PdfObjects.Subtype(a) == "/Redact"))
            {
                var rects = new List<PdfRect>();
                if (PdfObjects.Resolve(a.Elements["/QuadPoints"]) is PdfArray qp && qp.Elements.Count >= 8)
                {
                    for (int k = 0; k + 7 < qp.Elements.Count; k += 8)
                    {
                        var xs = new[] { 0, 2, 4, 6 }.Select(o => PdfObjects.Number(qp.Elements[k + o])).ToArray();
                        var ys = new[] { 1, 3, 5, 7 }.Select(o => PdfObjects.Number(qp.Elements[k + o])).ToArray();
                        rects.Add(new PdfRect(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                    }
                }
                else if (PdfObjects.Rect(a.Elements["/Rect"]) is { } r) rects.Add(r);
                if (rects.Count == 0) continue;
                if (!map.TryGetValue(i, out var l)) map[i] = l = new List<PdfRect>();
                l.AddRange(rects.Where(r => !r.IsEmpty));
            }
        }
        return map;
    }

    /// <summary>Oznaczenia z bajtów (dla nakładki podglądu).</summary>
    public static Dictionary<int, List<PdfRect>> ReadMarks(byte[] pdf) => PdfSharpEdit.Read(pdf, ReadMarks);

    public Task<byte[]> ApplyAsync(byte[] pdf, RedactionApplyOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(async () =>
        {
            var marks = ReadMarks(pdf);
            if (marks.Count == 0) return pdf;

            // 1. Render i słowa poza obszarami (PDFium).
            var rendered = new Dictionary<int, (byte[] Png, PdfRect Crop, List<PageTextTools.Word> Words, List<string> Removed)>();
            using (var rdoc = await _renderer.OpenAsync(pdf, null, ct).ConfigureAwait(false))
            {
                int n = 0;
                foreach (var (pageIndex, rects) in marks.OrderBy(m => m.Key))
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(OperationProgress.Of(n++, marks.Count, _loc?.Format("Edit.Redact.Progress.Apply", pageIndex + 1)));
                    var info = rdoc.Pages[pageIndex];
                    var areas = rects.Select(r => r.Inflate(0.5)).ToList();
                    double scale = RasterDpi / 72.0;
                    var bmp = await rdoc.RenderPageAsync(pageIndex, new RenderRequest(scale, (360 - info.Rotation) % 360, null, RenderFlags.None), ct).ConfigureAwait(false);
                    var crop = new PdfRect(info.CropOrigin.X, info.CropOrigin.Y, info.CropOrigin.X + info.Size.Width, info.CropOrigin.Y + info.Size.Height);
                    PaintAreas(bmp, crop, areas, options.FillColor);
                    var text = await rdoc.GetPageTextAsync(pageIndex, ct).ConfigureAwait(false);
                    bool Hit(TextChar c) => areas.Any(a => a.IntersectsWith(c.Box) || a.Contains(new PdfPoint((c.Box.Left + c.Box.Right) / 2, (c.Box.Bottom + c.Box.Top) / 2)));
                    var removedWords = PageTextTools.Words(text).Where(w => Enumerable.Range(w.Start, w.Count).Any(i => Hit(text.Chars[i]))).Select(w => w.Text).ToList();
                    var keptWords = PageTextTools.Words(text, c => !Hit(c));
                    rendered[pageIndex] = (PngWriter.FromBgra(bmp), crop, keptWords, removedWords);
                }
            }

            // 2. Przebudowa stron (PDFsharp).
            return PdfSharpEdit.Modify(pdf, doc =>
            {
                var removedAnnotations = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                var removedTokens = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                foreach (var (pageIndex, data) in rendered)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = doc.Pages[pageIndex];
                    var areas = marks[pageIndex].Select(r => r.Inflate(0.5)).ToList();
                    foreach (var w in data.Removed) removedTokens.Add(w.Trim('.', ',', ';', ':', '(', ')', '"', '\''));

                    // Nowe zasoby i treść – stare strumienie, czcionki i obrazy przestają być osiągalne.
                    var resources = new PdfDictionary(doc);
                    page.Elements["/Resources"] = resources;
                    page.Elements.Remove("/Thumb");
                    page.Elements.Remove("/PieceInfo");
                    page.Elements.Remove("/Metadata");
                    page.Elements.Remove("/Group");
                    var image = new PdfImage(doc, XImage.FromStream(new MemoryStream(data.Png)));
                    var xobjects = new PdfDictionary(doc);
                    xobjects.Elements["/ImRedacted"] = image.Reference ?? AddIndirect(doc, image);
                    resources.Elements["/XObject"] = xobjects;
                    var c = data.Crop;
                    PageContent.Replace(page, Encoding.ASCII.GetBytes($"q {PageContent.Matrix(c.Width, 0, 0, c.Height, c.Left, c.Bottom)} cm /ImRedacted Do Q\n"));

                    var words = data.Words.Select(w => new Ocr.Pdf.LayerWord(w.Text, new PdfPoint(w.Box.Left, w.Box.Bottom + w.Box.Height * 0.02),
                        Math.Max(1, w.FontSize > 0 ? w.FontSize : w.Box.Height), null, w.Box.Width)).ToList();
                    TextLayerWriter.Write(page, words, LayerStyle.HiddenText("redaction-text"));

                    PdfObjects.RemoveAnnotations(page, a =>
                    {
                        bool remove = PdfObjects.Subtype(a) == "/Redact" ||
                            (options.RemoveIntersectingAnnotations && PdfObjects.Rect(a.Elements["/Rect"]) is { } r && areas.Any(x => x.IntersectsWith(r)));
                        if (remove) removedAnnotations.Add(a);
                        return remove;
                    });
                }
                // Oznaczenia /Redact na stronach bez obszarów (puste) – też usuwamy.
                foreach (var page in doc.Pages) PdfObjects.RemoveAnnotations(page, a => PdfObjects.Subtype(a) == "/Redact");

                RemoveDependentAnnotations(doc, removedAnnotations);
                RemoveHiddenCopiesOfContent(doc);
                if (options.ScrubMetadata) ScrubMetadata(doc, removedTokens.Where(t => t.Length >= 3 && !t.Contains('*')).ToList());
            });
        }, ct);
    }

    /// <summary>
    /// Usunięty komentarz nie może zostać osiągalny w pliku: okienko /Popup trzyma go przez /Parent, a odpowiedź przez /IRT.
    /// Pole formularza z kilkoma widżetami usuwamy w całości, bo wartość /V jest wspólna.
    /// </summary>
    private static void RemoveDependentAnnotations(PdfDocument doc, HashSet<PdfDictionary> removed)
    {
        var removedFields = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var w in removed.Where(a => PdfObjects.Subtype(a) == "/Widget"))
            if (PdfObjects.Resolve(w.Elements["/Parent"]) is PdfDictionary field) removedFields.Add(field);

        bool Depends(PdfDictionary a)
        {
            if (PdfObjects.Subtype(a) == "/Widget") return PdfObjects.Resolve(a.Elements["/Parent"]) is PdfDictionary f && removedFields.Contains(f);
            return (PdfObjects.Resolve(a.Elements["/Parent"]) is PdfDictionary p && removed.Contains(p))
                || (PdfObjects.Resolve(a.Elements["/IRT"]) is PdfDictionary irt && removed.Contains(irt));
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var page in doc.Pages)
            {
                PdfObjects.RemoveAnnotations(page, a =>
                {
                    if (!Depends(a)) return false;
                    removed.Add(a);
                    changed = true;
                    return true;
                });
            }
        }
        while (changed);

        // Pozostałe adnotacje nie mogą wskazywać usuniętych (np. /Popup przecinający obszar, którego komentarz został).
        foreach (var page in doc.Pages)
        {
            foreach (var a in PdfObjects.Annotations(page))
            {
                if (PdfObjects.Resolve(a.Elements["/Popup"]) is PdfDictionary popup && removed.Contains(popup)) a.Elements.Remove("/Popup");
            }
        }
    }

    /// <summary>
    /// Miejsca poza treścią strony, które mogą przechowywać jej tekst: drzewo znaczników (/ActualText, /Alt, /E)
    /// i dane formularza XFA. Drzewo usuwamy w całości – zredagowana strona i tak jest obrazem, a częściowe
    /// czyszczenie łatwo coś pomija. Dokument traci tagi dostępności (można je sprawdzić kartą Ułatwienia dostępu).
    /// </summary>
    private static void RemoveHiddenCopiesOfContent(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog.Elements;
        catalog.Remove("/StructTreeRoot");
        catalog.Remove("/MarkInfo");
        foreach (var page in doc.Pages)
        {
            page.Elements.Remove("/StructParents");
            foreach (var a in PdfObjects.Annotations(page)) a.Elements.Remove("/StructParent");
        }
        if (PdfObjects.Resolve(catalog["/AcroForm"]) is PdfDictionary acro) acro.Elements.Remove("/XFA");
    }

    private static PdfReference AddIndirect(PdfDocument doc, PdfObject obj)
    {
        doc.Internals.AddObject(obj);
        return obj.Reference!;
    }

    private static void PaintAreas(RenderedBitmap bmp, PdfRect crop, IReadOnlyList<PdfRect> areas, PdfColor color)
    {
        double s = bmp.Scale;
        foreach (var a in areas)
        {
            int x1 = (int)Math.Floor((a.Left - crop.Left) * s) - 1, x2 = (int)Math.Ceiling((a.Right - crop.Left) * s) + 1;
            int y1 = (int)Math.Floor((crop.Top - a.Top) * s) - 1, y2 = (int)Math.Ceiling((crop.Top - a.Bottom) * s) + 1;
            x1 = Math.Clamp(x1, 0, bmp.Width); x2 = Math.Clamp(x2, 0, bmp.Width);
            y1 = Math.Clamp(y1, 0, bmp.Height); y2 = Math.Clamp(y2, 0, bmp.Height);
            for (int y = y1; y < y2; y++)
            {
                int o = y * bmp.Stride + x1 * 4;
                for (int x = x1; x < x2; x++, o += 4)
                {
                    bmp.Bgra[o] = color.B; bmp.Bgra[o + 1] = color.G; bmp.Bgra[o + 2] = color.R; bmp.Bgra[o + 3] = 255;
                }
            }
        }
    }

    /// <summary>Usuwa z /Info wpisy, a z XMP i tytułów zakładek frazy, które zostały zredagowane.</summary>
    private static void ScrubMetadata(PdfDocument doc, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return;
        bool Contains(string? s) => s is not null && tokens.Any(t => s.Contains(t, StringComparison.CurrentCultureIgnoreCase));
        var info = doc.Info.Elements;
        foreach (var key in info.Keys.ToList())
        {
            if (PdfObjects.Resolve(info[key]) is PdfString ps && Contains(ps.Value)) info.Remove(key);
        }
        var catalog = doc.Internals.Catalog.Elements;
        if (PdfObjects.Resolve(catalog["/Metadata"]) is PdfDictionary xmp && xmp.Stream is not null)
        {
            string text;
            try { text = Encoding.UTF8.GetString(xmp.Stream.UnfilteredValue); } catch (Exception) { text = string.Empty; }
            if (Contains(text)) catalog.Remove("/Metadata");
        }
        if (PdfObjects.Resolve(catalog["/Outlines"]) is PdfDictionary outlines) ScrubOutlines(outlines, tokens, 0);
    }

    private static void ScrubOutlines(PdfDictionary node, IReadOnlyList<string> tokens, int depth)
    {
        if (depth > 64) return;
        var child = PdfObjects.Resolve(node.Elements["/First"]) as PdfDictionary;
        int guard = 0;
        while (child is not null && guard++ < 100000)
        {
            if (PdfObjects.Resolve(child.Elements["/Title"]) is PdfString t)
            {
                var title = t.Value;
                foreach (var tok in tokens) title = ReplaceIgnoreCase(title, tok, "***");
                if (title != t.Value) child.Elements["/Title"] = new PdfString(title, PdfStringEncoding.Unicode);
            }
            ScrubOutlines(child, tokens, depth + 1);
            child = PdfObjects.Resolve(child.Elements["/Next"]) as PdfDictionary;
        }
    }

    private static string ReplaceIgnoreCase(string s, string find, string repl)
    {
        int i;
        while ((i = s.IndexOf(find, StringComparison.CurrentCultureIgnoreCase)) >= 0) s = s[..i] + repl + s[(i + find.Length)..];
        return s;
    }
}

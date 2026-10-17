using PdfSharp.Pdf;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations;

/// <summary>
/// Komentarze PDF (PDFsharp 6.2.4, słowniki ręcznie): odczyt, dodawanie z wyglądem /AP, zmiany, usuwanie,
/// odpowiedzi (/IRT), stan recenzji (/State, /StateModel), XFDF/FDF. Metody „bajty → bajty” (Task.Run).
/// </summary>
public sealed class AnnotationService : IAnnotationService
{
    private readonly AnnotationTexts _texts;

    public AnnotationService() : this(null, null) { }

    public AnnotationService(ILocalizationService? loc, ISettingsService? settings)
    {
        _texts = new AnnotationTexts(loc, settings);
    }

    internal IAnnotationTexts Texts => _texts;

    /// <summary>Zegar (testy mogą podmienić).</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public Task<IReadOnlyList<AnnotationInfo>> GetAnnotationsAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<AnnotationInfo>>(() => PdfSharpEdit.Read(pdf, doc => AnnotationStore.Read(doc, pageIndex)), ct);

    public Task<(byte[] Pdf, AnnotationId Id)> AddAsync(byte[] pdf, AnnotationSpec spec, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            AnnotationId id = default;
            var result = PdfSharpEdit.Modify(pdf, doc =>
            {
                var d = AnnotationFactory.Create(doc, spec, _texts, Clock());
                id = new AnnotationId(spec.PageIndex, Text(d, "/NM"));
            });
            return (result, id);
        }, ct);

    public Task<byte[]> UpdateAsync(byte[] pdf, AnnotationId id, AnnotationUpdate update, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => Update(doc, id, update), ct);

    /// <summary>Czcionka i kolor tekstu adnotacji FreeText (null dla innych rodzajów) – do okna Właściwości.</summary>
    public Task<TextStyle?> GetTextStyleAsync(byte[] pdf, AnnotationId id, CancellationToken ct = default) =>
        Task.Run(() => PdfSharpEdit.Read(pdf, doc =>
        {
            if (AnnotationStore.Find(doc, id) is not { } found) return null;
            var m = AnnotModel.Read(found.Dict);
            return m.Subtype == "/FreeText" ? new TextStyle { FontFamily = m.FontFamily, FontSize = m.FontSize, Color = m.TextColor } : (TextStyle?)null;
        }), ct);

    private void Update(PdfDocument doc, AnnotationId id, AnnotationUpdate u)
    {
        var found = AnnotationStore.Find(doc, id) ?? throw new KeyNotFoundException("Annotation not found: " + id.Name);
        var d = found.Dict;
        var now = Clock();
        bool geometry = false, style = false;
        var m = AnnotModel.Read(d);

        if (u.Rect is { } r && !r.IsEmpty && r != m.Rect)
        {
            var oldRect = m.Rect;
            m.TransformTo(r);
            m.WriteGeometry(doc, d);
            geometry = true;
            // okienko notatki jedzie razem z ikoną
            if (Dict(d, "/Popup") is { } popup && PdfLow.Rect(popup) is { } pr)
            {
                double dx = r.Left - oldRect.Left, dy = r.Top - oldRect.Top;
                popup.Elements["/Rect"] = RectArray(doc, new PdfRect(pr.Left + dx, pr.Bottom + dy, pr.Right + dx, pr.Top + dy));
            }
        }
        if (u.Contents is not null)
        {
            if (u.Contents.Length == 0) d.Elements.Remove("/Contents"); else d.Elements["/Contents"] = Str(u.Contents);
            m.Contents = u.Contents;
            style |= m.Subtype == "/FreeText";
        }
        if (u.Color is { } c)
        {
            if (m.Subtype == "/FreeText")
            {
                // FreeText: kolor = kolor tekstu i ramki
                m.TextColor = c;
                m.Color = c;
                d.Elements["/DA"] = Str(AnnotModel.BuildDa(m.FontSize, c));
                d.Elements["/DS"] = Str($"font: {Num(m.FontSize)}pt {m.FontFamily}; font-family: {m.FontFamily}; color:{Hex(c)}");
                var priv = Dict(d, AnnotModel.PrivateKey) ?? NewDict(doc);
                priv.Elements["/Border"] = ColorArray(doc, c);
                d.Elements[AnnotModel.PrivateKey] = priv;
            }
            else d.Elements["/C"] = ColorArray(doc, c);
            style = true;
        }
        if (u.TextStyle is { } ts && m.Subtype == "/FreeText")
        {
            if (!string.IsNullOrWhiteSpace(ts.FontFamily)) m.FontFamily = ts.FontFamily.Trim();
            if (ts.FontSize > 0) m.FontSize = Math.Clamp(ts.FontSize, 1, 1000);
            m.TextColor = ts.Color;
            d.Elements["/DA"] = Str(AnnotModel.BuildDa(m.FontSize, m.TextColor));
            d.Elements["/DS"] = Str($"font: {Num(m.FontSize)}pt {m.FontFamily}; font-family: {m.FontFamily}; color:{Hex(m.TextColor)}");
            style = true;
        }
        if (u.Opacity is { } o)
        {
            d.Elements["/CA"] = Real(Math.Clamp(o, 0.05, 1));
            style = true;
        }
        if (u.LineWidth is { } w)
        {
            var bs = Dict(d, "/BS") ?? NewDict(doc, "/Border");
            bs.Elements["/W"] = Real(Math.Max(0, w));
            d.Elements["/BS"] = bs;
            style = true;
        }
        if (u.IsHidden is { } hidden) AnnotationStore.SetFlag(d, AnnotationStore.FlagHidden, hidden);
        if (u.State is { } state) SetState(doc, found, state, now);

        if (geometry || style)
        {
            var fresh = AnnotModel.Read(d);
            if (!AppearanceBuilder.Apply(doc, d, fresh) && geometry)
            {
                // wygląd nieodtwarzalny (np. obraz) – czytnik dopasuje BBox do nowego /Rect
            }
        }
        AnnotationStore.Touch(d, now);
    }

    private void SetState(PdfDocument doc, FoundAnnot parent, AnnotationReviewState state, DateTimeOffset now)
    {
        string author = _texts.DefaultAuthor;
        var page = doc.Pages[parent.PageIndex];
        PdfDictionary? existing = null;
        foreach (var a in AnnotationStore.All(doc, parent.PageIndex))
            if (AnnotationStore.IsStateAnnot(a.Dict) && ReferenceEquals(Dict(a.Dict, "/IRT"), parent.Dict) && Text(a.Dict, "/T") == author)
                existing = a.Dict;
        if (existing is null)
        {
            existing = AnnotationStore.NewAnnot(doc, page, "/Text", NewName(), author, now,
                AnnotationStore.FlagHidden | AnnotationStore.FlagPrint | AnnotationStore.FlagNoZoom | AnnotationStore.FlagNoRotate);
            existing.Elements["/IRT"] = parent.Dict.Reference is { } pr ? pr : throw new InvalidOperationException("Parent annotation must be indirect.");
            existing.Elements["/Rect"] = RectArray(doc, PdfLow.Rect(parent.Dict) ?? new PdfRect(0, 0, 1, 1));
            existing.Elements["/StateModel"] = Str("Review");
            AnnotationStore.SetEmptyAppearance(doc, existing);
            EnsureAnnots(page).Elements.Add(existing.Reference!);
        }
        existing.Elements["/State"] = Str(AnnotationStore.StateName(state));
        existing.Elements["/Contents"] = Str(_texts.Get("Annot.State." + state));
        AnnotationStore.Touch(existing, now);
    }

    public Task<byte[]> DeleteAsync(byte[] pdf, IReadOnlyList<AnnotationId> ids, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var targets = ids.Select(id => AnnotationStore.Find(doc, id)).Where(f => f is not null).Select(f => f!.Dict).ToList();
            AnnotationStore.Remove(doc, targets);
        }, ct);

    public Task<byte[]> DeleteAllAsync(byte[] pdf, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            var targets = AnnotationStore.All(doc)
                .Where(a => AnnotModel.KindOf(a.Dict) is var k && AnnotModel.IsComment(k))
                .Select(a => a.Dict).ToList();
            AnnotationStore.Remove(doc, targets);
        }, ct);

    public Task<(byte[] Pdf, AnnotationId Id)> AddReplyAsync(byte[] pdf, AnnotationId parent, string text, string author, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            AnnotationId id = default;
            var result = PdfSharpEdit.Modify(pdf, doc =>
            {
                var p = AnnotationStore.Find(doc, parent) ?? throw new KeyNotFoundException("Annotation not found: " + parent.Name);
                var page = doc.Pages[p.PageIndex];
                if (p.Dict.Reference is null) doc.Internals.AddObject(p.Dict);
                var d = AnnotationStore.NewAnnot(doc, page, "/Text", NewName(), string.IsNullOrWhiteSpace(author) ? _texts.DefaultAuthor : author, Clock(),
                    AnnotationStore.FlagPrint | AnnotationStore.FlagNoZoom | AnnotationStore.FlagNoRotate);
                d.Elements["/IRT"] = p.Dict.Reference!;
                d.Elements["/RT"] = Name("/R");
                d.Elements["/Rect"] = RectArray(doc, PdfLow.Rect(p.Dict) ?? new PdfRect(0, 0, 1, 1));
                d.Elements["/Contents"] = Str(text);
                d.Elements["/Name"] = Name("/Comment");
                d.Elements["/Subj"] = Str(_texts.Get("Annot.Kind.Reply"));
                AnnotationStore.SetEmptyAppearance(doc, d);
                EnsureAnnots(page).Elements.Add(d.Reference!);
                id = new AnnotationId(p.PageIndex, Text(d, "/NM"));
            });
            return (result, id);
        }, ct);

    public Task ExportXfdfAsync(byte[] pdf, Stream output, string? sourceFileName, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var xml = PdfSharpEdit.Read(pdf, doc => XfdfComments.Export(doc, sourceFileName));
            xml.Save(output);
        }, ct);

    public Task<byte[]> ImportCommentsAsync(byte[] pdf, Stream input, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            var data = ms.ToArray();
            return PdfSharpEdit.Modify(pdf, doc =>
            {
                if (FdfParser.LooksLikeFdf(data)) FdfComments.Import(doc, data, _texts, Clock());
                else XfdfComments.Import(doc, data, _texts, Clock());
            });
        }, ct);

    /// <summary>Eksport komentarzy do FDF (Adobe FDF 1.2: /FDF /Annots).</summary>
    public Task ExportFdfAsync(byte[] pdf, Stream output, string? sourceFileName, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var bytes = PdfSharpEdit.Read(pdf, doc => FdfComments.Export(doc, sourceFileName));
            output.Write(bytes);
        }, ct);

    /// <summary>Ukrywa/pokazuje wszystkie komentarze (flaga Hidden /F) – Pokaż/Ukryj wszystkie.</summary>
    public Task<byte[]> SetAllHiddenAsync(byte[] pdf, bool hidden, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            foreach (var a in AnnotationStore.All(doc))
            {
                var k = AnnotModel.KindOf(a.Dict);
                if (!AnnotModel.IsComment(k) || AnnotationStore.IsStateAnnot(a.Dict)) continue;
                AnnotationStore.SetFlag(a.Dict, AnnotationStore.FlagHidden, hidden);
            }
        }, ct);

    /// <summary>Spłaszcza adnotacje (wygląd /AP wtapiany w treść strony). onlyIds = null → wszystkie komentarze.</summary>
    public Task<byte[]> FlattenAsync(byte[] pdf, bool includeFormFields, IReadOnlyCollection<AnnotationId>? onlyIds = null, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc => Flattener.Flatten(doc, includeFormFields, onlyIds), ct);
}

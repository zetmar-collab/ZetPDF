using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Measure;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations;

/// <summary>
/// Pomiary (odległość, obwód, pole) jako standardowe adnotacje pomiarowe PDF: /Line /IT /LineDimension,
/// /PolyLine /IT /PolyLineDimension, /Polygon /IT /PolygonDimension ze słownikiem /Measure (ISO 32000-1 §12.9)
/// i wyglądem /AP z etykietą wartości. Skala per strona w /VP (§8.8.1). Metody „bajty → bajty”.
/// </summary>
public sealed class MeasureService
{
    private readonly AnnotationService _annotations;

    public MeasureService(AnnotationService annotations) => _annotations = annotations;

    /// <summary>Kultura liczb w etykietach (separator dziesiętny) – język interfejsu.</summary>
    internal CultureInfo Culture => _annotations.TextsCulture;

    public Task<(byte[] Pdf, AnnotationId Id)> AddAsync(byte[] pdf, MeasureSpec spec, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            AnnotationId id = default;
            var result = PdfSharpEdit.Modify(pdf, doc =>
            {
                var d = Create(doc, spec);
                id = new AnnotationId(spec.PageIndex, Text(d, "/NM"));
            });
            return (result, id);
        }, ct);

    private PdfDictionary Create(PdfDocument doc, MeasureSpec spec)
    {
        if (spec.PageIndex < 0 || spec.PageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(spec), "Page index out of range.");
        int min = spec.Type switch { MeasureType.Distance => 2, MeasureType.Perimeter => 2, _ => 3 };
        if (spec.Points.Count < min) throw new ArgumentException("Not enough points for measurement.", nameof(spec)).WithMessageKey("Annot.Error.Geometry");
        var page = doc.Pages[spec.PageIndex];
        var scale = spec.Scale ?? MeasureDict.PageScale(page) ?? MeasureScale.Default;
        var texts = _annotations.Texts;
        var now = _annotations.Clock();
        string author = string.IsNullOrWhiteSpace(spec.Author) ? texts.DefaultAuthor : spec.Author!;
        var d = AnnotationStore.NewAnnot(doc, page, MeasureDict.SubtypeFor(spec.Type), NewName(), author, now);
        d.Elements["/IT"] = Name(MeasureDict.IntentFor(spec.Type));
        d.Elements["/Subj"] = Str(_annotations.TextsGet("Annot.Measure.Kind." + spec.Type));
        d.Elements["/C"] = ColorArray(doc, spec.Color);
        d.Elements["/CA"] = Real(Math.Clamp(spec.Opacity, 0.05, 1));
        var bs = NewDict(doc, "/Border");
        bs.Elements["/W"] = Real(Math.Max(0.1, spec.LineWidth));
        bs.Elements["/S"] = Name("/S");
        d.Elements["/BS"] = bs;
        d.Elements["/Measure"] = MeasureDict.Create(doc, scale, Culture);
        var pts = spec.Points.ToList();
        if (spec.Type == MeasureType.Distance)
        {
            pts = pts.Take(2).ToList();
            var le = new PdfArray(doc);
            le.Elements.Add(Name("/Butt"));
            le.Elements.Add(Name("/Butt"));
            d.Elements["/LE"] = le;
            d.Elements["/Cap"] = new PdfBoolean(true);
            d.Elements["/CP"] = Name("/Top");
        }
        var m = new AnnotModel
        {
            Kind = spec.Type switch { MeasureType.Distance => AnnotationKind.Line, MeasureType.Perimeter => AnnotationKind.Polyline, _ => AnnotationKind.Polygon },
            Subtype = MeasureDict.SubtypeFor(spec.Type),
            Color = spec.Color,
            Opacity = spec.Opacity,
            LineWidth = spec.LineWidth,
        };
        m.Points.AddRange(pts);
        m.Rect = m.GeometryBounds(spec.LineWidth + 6);
        m.WriteGeometry(doc, d);
        AppearanceBuilder.Apply(doc, d, AnnotModel.Read(d));
        EnsureAnnots(page).Elements.Add(d.Reference!);
        return d;
    }

    /// <summary>Skala strony zapisana w /VP (null – brak; narzędzia przyjmują wtedy 1:1 cm).</summary>
    public Task<MeasureScale?> GetScaleAsync(byte[] pdf, int pageIndex, CancellationToken ct = default) =>
        Task.Run(() => PdfSharpEdit.Read(pdf, doc => pageIndex >= 0 && pageIndex < doc.PageCount ? MeasureDict.PageScale(doc.Pages[pageIndex]) : null), ct);

    /// <summary>
    /// Ustawia skalę stron (null = usuń skalę ZetPDF). <paramref name="updateExisting"/>: pomiary na tych stronach
    /// dostają nową skalę i przeliczoną wartość/wygląd.
    /// </summary>
    public Task<byte[]> SetScaleAsync(byte[] pdf, IReadOnlyCollection<int> pages, MeasureScale? scale, bool updateExisting, CancellationToken ct = default) =>
        PdfSharpEdit.ModifyAsync(pdf, doc =>
        {
            if (scale is not null && !scale.IsValid) throw new ArgumentException("Invalid scale.", nameof(scale)).WithMessageKey("Annot.Measure.Error.Scale");
            foreach (var p in pages.Where(p => p >= 0 && p < doc.PageCount).Distinct())
            {
                var page = doc.Pages[p];
                MeasureDict.SetPageScale(doc, page, scale, Culture);
                if (!updateExisting) continue;
                foreach (var (a, _) in AnnotDicts(page))
                {
                    if (MeasureDict.TypeOf(a) is null) continue;
                    a.Elements["/Measure"] = MeasureDict.Create(doc, scale ?? MeasureScale.Default, Culture);
                    AppearanceBuilder.Apply(doc, a, AnnotModel.Read(a));
                    AnnotationStore.Touch(a, _annotations.Clock());
                }
            }
        }, ct);

    /// <summary>Wszystkie pomiary dokumentu (także z innych programów – adnotacje z /IT …Dimension i /Measure).</summary>
    public Task<IReadOnlyList<MeasurementInfo>> GetMeasurementsAsync(byte[] pdf, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<MeasurementInfo>>(() => PdfSharpEdit.Read(pdf, doc =>
        {
            var list = new List<MeasurementInfo>();
            foreach (var a in AnnotationStore.All(doc))
            {
                if (MeasureDict.TypeOf(a.Dict) is not { } type || Dict(a.Dict, "/Measure") is not { } measure) continue;
                var format = MeasureDict.ReadFormat(measure, Culture) ?? MeasureFormat.From(MeasureScale.Default, Culture);
                double value = Math.Round(format.Value(type, MeasureDict.Points(a.Dict)), format.Decimals, MidpointRounding.AwayFromZero);
                list.Add(new MeasurementInfo(a.Id, type, value, format.UnitFor(type), format.Label(type, value), Text(a.Dict, "/T"),
                    ParseDate(Text(a.Dict, "/CreationDate")), Text(measure, "/R")));
            }
            return list;
        }), ct);

    /// <summary>
    /// CSV pomiarów (UTF-8 z BOM, separator „;”): strona, typ, wartość, jednostka, autor, data, skala.
    /// Nagłówki i nazwy typów w języku interfejsu.
    /// </summary>
    public byte[] ExportCsv(IReadOnlyList<MeasurementInfo> items)
    {
        var c = Culture;
        string T(string s) => CsvText.Cell(s, ';');               // tekst (z dokumentu / zasobów) – neutralizowany
        string N(string s) => CsvText.Cell(s, ';', neutralize: false); // liczby generowane przez program
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", new[] { "Page", "Type", "Value", "Unit", "Author", "Date", "Scale" }.Select(h => T(_annotations.TextsGet("Annot.Measure.Csv." + h)))));
        foreach (var m in items.OrderBy(m => m.Id.PageIndex))
            sb.AppendLine(string.Join(";",
                N((m.Id.PageIndex + 1).ToString(c)),
                T(_annotations.TextsGet("Annot.Measure.Kind." + m.Type)),
                N(m.Value.ToString("0.######", c)),
                T(m.Unit),
                T(m.Author),
                N(m.Created?.LocalDateTime.ToString("g", c) ?? string.Empty),
                T(m.Ratio)));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }
}

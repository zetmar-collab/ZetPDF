using System.Globalization;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Measure;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Słownik /Measure (ISO 32000-1 §12.9, podtyp /RL – prostoliniowy) i obszary /VP strony (§8.8.1).
/// Zapis: /R (tekst skali), /X (przelicznik punkt → jednostka), /D (odległość, /C 1), /A (pole, /C 1) – jak Acrobat.
/// Dokładne wartości skali ZetPDF trzyma dodatkowo w prywatnym /ZetPDF (inne czytniki go ignorują).
/// </summary>
internal static class MeasureDict
{
    public const string ViewportName = "ZetPDF";

    public static string IntentFor(MeasureType t) => t switch
    {
        MeasureType.Distance => "/LineDimension",
        MeasureType.Perimeter => "/PolyLineDimension",
        _ => "/PolygonDimension",
    };

    public static string SubtypeFor(MeasureType t) => t switch
    {
        MeasureType.Distance => "/Line",
        MeasureType.Perimeter => "/PolyLine",
        _ => "/Polygon",
    };

    /// <summary>Rodzaj pomiaru adnotacji (po /IT; wymagany /Measure) albo null, gdy to nie pomiar.</summary>
    public static MeasureType? TypeOf(PdfDictionary annot)
    {
        if (Dict(annot, "/Measure") is null) return null;
        return NameOf(annot, "/IT") switch
        {
            "/LineDimension" => MeasureType.Distance,
            "/PolyLineDimension" => MeasureType.Perimeter,
            "/PolygonDimension" => MeasureType.Area,
            _ => null,
        };
    }

    private static PdfDictionary NumberFormat(PdfDocument doc, string unit, double factor, int decimals, string rd, string ss)
    {
        var nf = NewDict(doc, "/NumberFormat");
        nf.Elements["/U"] = Str(unit);
        nf.Elements["/C"] = new PdfReal(factor);
        nf.Elements["/F"] = Name("/D");
        nf.Elements["/D"] = new PdfInteger((int)Math.Pow(10, Math.Clamp(decimals, 0, 6)));
        nf.Elements["/RD"] = Str(rd);
        nf.Elements["/SS"] = Str(ss);
        return nf;
    }

    private static PdfArray One(PdfDocument doc, PdfDictionary d)
    {
        var a = new PdfArray(doc);
        a.Elements.Add(d);
        return a;
    }

    /// <summary>Nowy słownik /Measure dla skali (separatory liczb wg kultury).</summary>
    public static PdfDictionary Create(PdfDocument doc, MeasureScale scale, CultureInfo culture)
    {
        var f = MeasureFormat.From(scale, culture);
        var m = NewDict(doc, "/Measure");
        m.Elements["/Subtype"] = Name("/RL");
        m.Elements["/R"] = Str(scale.Ratio(culture));
        m.Elements["/X"] = One(doc, NumberFormat(doc, f.Unit, f.RealPerPoint, f.Decimals, f.DecimalSeparator, f.ThousandsSeparator));
        m.Elements["/D"] = One(doc, NumberFormat(doc, f.Unit, 1, f.Decimals, f.DecimalSeparator, f.ThousandsSeparator));
        m.Elements["/A"] = One(doc, NumberFormat(doc, f.AreaUnit, 1, f.Decimals, f.DecimalSeparator, f.ThousandsSeparator));
        var priv = NewDict(doc);
        priv.Elements["/PV"] = new PdfReal(scale.PageValue);
        priv.Elements["/PU"] = Str(scale.PageUnit);
        priv.Elements["/RV"] = new PdfReal(scale.RealValue);
        priv.Elements["/RU"] = Str(scale.RealUnit);
        priv.Elements["/Dec"] = new PdfInteger(scale.Decimals);
        m.Elements[AnnotModel.PrivateKey] = priv;
        return m;
    }

    /// <summary>Skala zapisana przez ZetPDF (prywatne wartości) albo odtworzona z /X, /D (pliki innych programów).</summary>
    public static MeasureScale? ReadScale(PdfDictionary measure)
    {
        if (Dict(measure, AnnotModel.PrivateKey) is { } p)
        {
            var s = new MeasureScale(Number(p, "/PV", 1), Text(p, "/PU"), Number(p, "/RV", 1), Text(p, "/RU"), Int(p, "/Dec", 2));
            if (s.IsValid) return s;
        }
        var fmt = ReadFormat(measure, CultureInfo.InvariantCulture);
        if (fmt is null || !MeasureScale.IsKnownUnit(fmt.Unit)) return null;
        // 1 cm na stronie = RealPerPoint * 28,35 jednostek
        return new MeasureScale(1, "cm", Math.Round(fmt.RealPerPoint * MeasureScale.PointsPer("cm"), 10), fmt.Unit.Trim().ToLowerInvariant(), fmt.Decimals);
    }

    /// <summary>Format liczb ze słownika /Measure (pierwsze elementy /X, /D, /A).</summary>
    public static MeasureFormat? ReadFormat(PdfDictionary measure, CultureInfo fallbackCulture)
    {
        if (NameOf(measure, "/Subtype") is { } st && st != "/RL") return null;
        PdfDictionary? First(string key) => ArrayOf(measure, key) is { Elements.Count: > 0 } a ? Deref(a.Elements[0]) as PdfDictionary : null;
        var x = First("/X");
        if (x is null) return null;
        var d = First("/D") ?? x;
        var area = First("/A");
        double cx = Number(x, "/C", 1);
        double cd = ReferenceEquals(d, x) ? 1 : Number(d, "/C", 1);
        string unit = Text(d, "/U") is { Length: > 0 } u ? u : Text(x, "/U");
        string areaUnit = area is not null && Text(area, "/U") is { Length: > 0 } au ? au : unit + "²";
        double areaFactor = area is not null ? Number(area, "/C", 1) : 1;
        int precision = Int(d, "/D", 100);
        int decimals = precision <= 1 ? 0 : (int)Math.Round(Math.Log10(precision));
        string rd = d.Elements.ContainsKey("/RD") ? Text(d, "/RD") : ".";
        string ss = d.Elements.ContainsKey("/SS") ? Text(d, "/SS") : ",";
        if (!d.Elements.ContainsKey("/RD") && !d.Elements.ContainsKey("/SS") && Dict(measure, AnnotModel.PrivateKey) is not null)
        {
            rd = fallbackCulture.NumberFormat.NumberDecimalSeparator;
            ss = fallbackCulture.NumberFormat.NumberGroupSeparator;
        }
        if (cx <= 0 || !double.IsFinite(cx)) return null;
        // /C w /D przelicza jednostki /X na jednostki /D; pole: (jednostki /X)² · /C pola
        return new MeasureFormat(cx * cd, unit, areaUnit, areaFactor, Math.Clamp(decimals, 0, 6), rd, ss);
    }

    /// <summary>Skala strony z /VP (obszar ZetPDF albo pierwszy obszar ze słownikiem /Measure).</summary>
    public static MeasureScale? PageScale(PdfPage page)
    {
        if (Deref(page.Elements["/VP"]) is not PdfArray vp) return null;
        PdfDictionary? found = null;
        foreach (var it in vp.Elements)
        {
            if (Deref(it) is not PdfDictionary v || Dict(v, "/Measure") is not { } m) continue;
            if (Text(v, "/Name") == ViewportName) { found = m; break; }
            found ??= m;
        }
        return found is null ? null : ReadScale(found);
    }

    /// <summary>Ustawia (zastępuje) obszar ZetPDF w /VP strony obejmujący całą stronę.</summary>
    public static void SetPageScale(PdfDocument doc, PdfPage page, MeasureScale? scale, CultureInfo culture)
    {
        var vp = Deref(page.Elements["/VP"]) as PdfArray;
        if (vp is not null)
            for (int i = vp.Elements.Count - 1; i >= 0; i--)
                if (Deref(vp.Elements[i]) is PdfDictionary v && Text(v, "/Name") == ViewportName) vp.Elements.RemoveAt(i);
        if (scale is null)
        {
            if (vp is { Elements.Count: 0 }) page.Elements.Remove("/VP");
            return;
        }
        if (vp is null)
        {
            vp = new PdfArray(doc);
            page.Elements["/VP"] = vp;
        }
        var box = page.MediaBox;
        var view = NewDict(doc, "/Viewport");
        view.Elements["/BBox"] = RectArray(doc, new PdfRect(box.X1, box.Y1, box.X2, box.Y2));
        view.Elements["/Name"] = Str(ViewportName);
        view.Elements["/Measure"] = Create(doc, scale, culture);
        vp.Elements.Add(view);
    }

    /// <summary>Punkty geometrii pomiaru (/L albo /Vertices).</summary>
    public static List<PdfPoint> Points(PdfDictionary annot)
    {
        var n = Numbers(ArrayOf(annot, NameOf(annot, "/Subtype") == "/Line" ? "/L" : "/Vertices"));
        var pts = new List<PdfPoint>();
        for (int i = 0; i + 1 < n.Length; i += 2) pts.Add(new PdfPoint(n[i], n[i + 1]));
        return pts;
    }
}

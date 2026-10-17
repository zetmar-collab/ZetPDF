using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Model adnotacji odczytany ze słownika PDF (geometria + styl). Źródło dla generatora wyglądu (/AP)
/// i dla przekształceń (przesunięcie/zmiana rozmiaru). Zapis z powrotem: <see cref="WriteGeometry"/>.
/// </summary>
internal sealed partial class AnnotModel
{
    /// <summary>Klucz prywatny ZetPDF (rodzaj ponad /Subtype, teksty stempla) – inne czytniki go ignorują.</summary>
    public const string PrivateKey = "/ZetPDF";

    public AnnotationKind Kind { get; set; }
    public string Subtype { get; set; } = string.Empty;
    public PdfRect Rect { get; set; }
    public List<PdfQuad> Quads { get; } = new();
    /// <summary>Line: 2 punkty (/L); Polygon/Polyline: /Vertices; Callout: /CL.</summary>
    public List<PdfPoint> Points { get; } = new();
    public List<List<PdfPoint>> Ink { get; } = new();
    public PdfColor? Color { get; set; }
    public PdfColor? Fill { get; set; }
    public double Opacity { get; set; } = 1;
    public double LineWidth { get; set; } = 1;
    public string Contents { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? StampName { get; set; }
    public List<string> StampLines { get; } = new();
    public bool IsImageStamp { get; set; }
    public double FontSize { get; set; } = 12;
    public string FontFamily { get; set; } = "Arial";
    public PdfColor TextColor { get; set; } = PdfColor.Black;
    public bool HasBorder { get; set; } = true;
    /// <summary>Callout: prostokąt pola tekstowego wewnątrz /Rect.</summary>
    public PdfRect? TextRect { get; set; }
    public string? LineEnd { get; set; }

    public static AnnotationKind KindOf(PdfDictionary d)
    {
        var priv = Dict(d, PrivateKey);
        if (priv is not null && NameOf(priv, "/Kind") is { } k && Enum.TryParse<AnnotationKind>(k.TrimStart('/'), out var pk)) return pk;
        var subtype = NameOf(d, "/Subtype") ?? string.Empty;
        switch (subtype)
        {
            case "/Text": return AnnotationKind.Note;
            case "/FreeText":
                return NameOf(d, "/IT") switch
                {
                    "/FreeTextTypeWriter" => AnnotationKind.Typewriter,
                    "/FreeTextCallout" => AnnotationKind.Callout,
                    _ => AnnotationKind.FreeText,
                };
            case "/Line":
                var le = ArrayOf(d, "/LE");
                return le is { Elements.Count: 2 } && TextOf(le.Elements[1]) is { } end && end != "None" ? AnnotationKind.Arrow : AnnotationKind.Line;
            case "/Square": return AnnotationKind.Square;
            case "/Circle": return AnnotationKind.Circle;
            case "/Polygon": return AnnotationKind.Polygon;
            case "/PolyLine": return AnnotationKind.Polyline;
            case "/Ink": return AnnotationKind.Ink;
            case "/Highlight": return AnnotationKind.Highlight;
            case "/Underline": return AnnotationKind.Underline;
            case "/Squiggly": return AnnotationKind.Squiggly;
            case "/StrikeOut": return AnnotationKind.StrikeOut;
            case "/Stamp": return AnnotationKind.Stamp;
            case "/FileAttachment": return AnnotationKind.FileAttachment;
            case "/Link": return AnnotationKind.Link;
            case "/Redact": return AnnotationKind.Redact;
            case "/Widget": return AnnotationKind.Widget;
            case "/Popup": return AnnotationKind.Popup;
            default: return AnnotationKind.Other;
        }
    }

    public static string SubtypeFor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight or AnnotationKind.AreaHighlight => "/Highlight",
        AnnotationKind.Underline => "/Underline",
        AnnotationKind.Squiggly => "/Squiggly",
        AnnotationKind.StrikeOut => "/StrikeOut",
        AnnotationKind.Note => "/Text",
        AnnotationKind.FreeText or AnnotationKind.Typewriter or AnnotationKind.Callout => "/FreeText",
        AnnotationKind.Square => "/Square",
        AnnotationKind.Circle => "/Circle",
        AnnotationKind.Line or AnnotationKind.Arrow => "/Line",
        AnnotationKind.Polygon => "/Polygon",
        AnnotationKind.Polyline => "/PolyLine",
        AnnotationKind.Ink => "/Ink",
        AnnotationKind.Stamp => "/Stamp",
        AnnotationKind.FileAttachment => "/FileAttachment",
        AnnotationKind.Redact => "/Redact",
        _ => throw new NotSupportedException("Annotation kind not supported: " + kind),
    };

    /// <summary>Czy to komentarz (adiustacja, notatka, rysunek, stempel…), a nie łącze/pole/okienko.</summary>
    public static bool IsComment(AnnotationKind k) => k is not (AnnotationKind.Link or AnnotationKind.Widget or AnnotationKind.Popup or AnnotationKind.Other);

    public static AnnotModel Read(PdfDictionary d)
    {
        var m = new AnnotModel
        {
            Kind = KindOf(d),
            Subtype = NameOf(d, "/Subtype") ?? string.Empty,
            Rect = PdfLow.Rect(d) ?? default,
            Color = PdfLow.Color(d, "/C"),
            Fill = PdfLow.Color(d, "/IC"),
            Opacity = Number(d, "/CA", 1),
            Contents = Text(d, "/Contents"),
            Icon = NameOf(d, "/Name")?.TrimStart('/'),
        };
        var bs = Dict(d, "/BS");
        m.LineWidth = bs is not null ? Number(bs, "/W", 1) : Border(d);

        var qp = Numbers(ArrayOf(d, "/QuadPoints"));
        for (int i = 0; i + 7 < qp.Length; i += 8)
            m.Quads.Add(new PdfQuad(new(qp[i], qp[i + 1]), new(qp[i + 2], qp[i + 3]), new(qp[i + 4], qp[i + 5]), new(qp[i + 6], qp[i + 7])));

        double[] pts = m.Subtype switch
        {
            "/Line" => Numbers(ArrayOf(d, "/L")),
            "/Polygon" or "/PolyLine" => Numbers(ArrayOf(d, "/Vertices")),
            "/FreeText" => Numbers(ArrayOf(d, "/CL")),
            _ => System.Array.Empty<double>(),
        };
        for (int i = 0; i + 1 < pts.Length; i += 2) m.Points.Add(new PdfPoint(pts[i], pts[i + 1]));

        if (ArrayOf(d, "/InkList") is { } ink)
            foreach (var s in ink.Elements)
            {
                var n = Numbers(Deref(s) as PdfArray);
                var stroke = new List<PdfPoint>();
                for (int i = 0; i + 1 < n.Length; i += 2) stroke.Add(new PdfPoint(n[i], n[i + 1]));
                if (stroke.Count > 0) m.Ink.Add(stroke);
            }

        if (ArrayOf(d, "/LE") is { Elements.Count: 2 } le) m.LineEnd = TextOf(le.Elements[1]);

        if (m.Subtype == "/FreeText")
        {
            var da = Text(d, "/DA");
            ParseDa(da, out var size, out var color);
            if (size > 0) m.FontSize = size;
            if (color is { } tc) m.TextColor = tc;
            var ds = Text(d, "/DS");
            var fam = FontFamilyRegex().Match(ds);
            if (fam.Success) m.FontFamily = fam.Groups[1].Value.Trim().Trim('\'', '"');
            m.HasBorder = m.Kind != AnnotationKind.Typewriter && m.LineWidth > 0;
            // /C w FreeText = tło (PDF 2.0)
            m.Fill = m.Color;
            if (Dict(d, PrivateKey) is { } p && PdfLow.Color(p, "/Border") is { } bc) m.Color = bc;
            else m.Color = m.TextColor;
            var rd = Numbers(ArrayOf(d, "/RD"));
            if (m.Kind == AnnotationKind.Callout && rd.Length == 4)
                m.TextRect = new PdfRect(m.Rect.Left + rd[0], m.Rect.Bottom + rd[1], m.Rect.Right - rd[2], m.Rect.Top - rd[3]);
        }

        if (Dict(d, PrivateKey) is { } priv)
        {
            m.StampName = NameOf(priv, "/Stamp")?.TrimStart('/');
            m.IsImageStamp = priv.Elements.GetBoolean("/Image");
            if (ArrayOf(priv, "/Lines") is { } lines)
                foreach (var l in lines.Elements) m.StampLines.Add(TextOf(l));
        }
        if (m.Subtype == "/Stamp" && m.StampName is null) m.StampName = m.Icon;
        return m;
    }

    private static double Border(PdfDictionary d)
    {
        var b = Numbers(ArrayOf(d, "/Border"));
        return b.Length >= 3 ? b[2] : 1;
    }

    [GeneratedRegex(@"font-family\s*:\s*([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FontFamilyRegex();

    [GeneratedRegex(@"font\s*:\s*(?:[\w-]+\s+)*?([\d.]+)pt\s+([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FontShorthandRegex();

    /// <summary>Rozbiór /DA: rozmiar czcionki (Tf) i kolor tekstu (g / rg / k).</summary>
    public static void ParseDa(string da, out double fontSize, out PdfColor? color)
    {
        fontSize = 0;
        color = null;
        var tokens = da.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            double N(int back) => i - back >= 0 && double.TryParse(tokens[i - back], NumberStyles.Float, Inv, out var v) ? v : 0;
            switch (tokens[i])
            {
                case "Tf": fontSize = N(1); break;
                case "g": color = ColorFrom(new[] { N(1) }); break;
                case "rg": color = ColorFrom(new[] { N(3), N(2), N(1) }); break;
                case "k": color = ColorFrom(new[] { N(4), N(3), N(2), N(1) }); break;
            }
        }
    }

    /// <summary>Nazwa czcionki z /DA (np. "Helv").</summary>
    public static string? DaFontName(string da)
    {
        var tokens = da.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 2; i < tokens.Length; i++)
            if (tokens[i] == "Tf" && tokens[i - 2].StartsWith('/')) return tokens[i - 2][1..];
        return null;
    }

    public static string BuildDa(double fontSize, PdfColor color, string fontName = "Helv") =>
        $"/{fontName} {Num(fontSize)} Tf {Rgb(color, false)}";

    // ------------------------------------------------------------------ przekształcenia

    /// <summary>Przekształca całą geometrię tak, by stary /Rect przeszedł w nowy (przesunięcie + skala).</summary>
    public void TransformTo(PdfRect newRect)
    {
        var old = Rect;
        double sx = old.Width > 1e-6 ? newRect.Width / old.Width : 1;
        double sy = old.Height > 1e-6 ? newRect.Height / old.Height : 1;
        PdfPoint T(PdfPoint p) => new(newRect.Left + (p.X - old.Left) * sx, newRect.Bottom + (p.Y - old.Bottom) * sy);
        for (int i = 0; i < Quads.Count; i++)
        {
            var q = Quads[i];
            Quads[i] = new PdfQuad(T(q.UpperLeft), T(q.UpperRight), T(q.LowerLeft), T(q.LowerRight));
        }
        for (int i = 0; i < Points.Count; i++) Points[i] = T(Points[i]);
        foreach (var s in Ink)
            for (int i = 0; i < s.Count; i++) s[i] = T(s[i]);
        if (TextRect is { } tr) TextRect = PdfRect.FromPoints(T(new(tr.Left, tr.Bottom)), T(new(tr.Right, tr.Top)));
        Rect = newRect;
    }

    /// <summary>Zapisuje geometrię (Rect, QuadPoints, L/Vertices/CL, InkList, RD) do słownika.</summary>
    public void WriteGeometry(PdfDocument doc, PdfDictionary d)
    {
        d.Elements["/Rect"] = RectArray(doc, Rect);
        if (Quads.Count > 0)
        {
            var a = new PdfArray(doc);
            foreach (var q in Quads)
                foreach (var p in new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight })
                {
                    a.Elements.Add(Real(p.X));
                    a.Elements.Add(Real(p.Y));
                }
            d.Elements["/QuadPoints"] = a;
        }
        if (Points.Count > 0)
        {
            string key = Subtype switch { "/Line" => "/L", "/FreeText" => "/CL", _ => "/Vertices" };
            var a = new PdfArray(doc);
            foreach (var p in Points) { a.Elements.Add(Real(p.X)); a.Elements.Add(Real(p.Y)); }
            d.Elements[key] = a;
        }
        if (Ink.Count > 0)
        {
            var list = new PdfArray(doc);
            foreach (var s in Ink)
            {
                var a = new PdfArray(doc);
                foreach (var p in s) { a.Elements.Add(Real(p.X)); a.Elements.Add(Real(p.Y)); }
                list.Elements.Add(a);
            }
            d.Elements["/InkList"] = list;
        }
        if (TextRect is { } tr)
            d.Elements["/RD"] = Arr(doc, tr.Left - Rect.Left, tr.Bottom - Rect.Bottom, Rect.Right - tr.Right, Rect.Top - tr.Top);
    }

    /// <summary>Prostokąt obejmujący geometrię (dla adiustacji z czworokątów, linii, kresek) z marginesem.</summary>
    public PdfRect GeometryBounds(double margin)
    {
        var pts = new List<PdfPoint>();
        foreach (var q in Quads) pts.AddRange(new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight });
        pts.AddRange(Points);
        foreach (var s in Ink) pts.AddRange(s);
        if (TextRect is { } tr) { pts.Add(new(tr.Left, tr.Bottom)); pts.Add(new(tr.Right, tr.Top)); }
        if (pts.Count == 0) return Rect;
        double l = pts.Min(p => p.X), b = pts.Min(p => p.Y), r = pts.Max(p => p.X), t = pts.Max(p => p.Y);
        return new PdfRect(l - margin, b - margin, r + margin, t + margin);
    }
}

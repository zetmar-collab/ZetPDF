using System.Globalization;
using System.Text;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.Measure;

/// <summary>Rodzaj pomiaru (ISO 32000-1 12.5.6.7 /IT: LineDimension, PolyLineDimension, PolygonDimension).</summary>
public enum MeasureType { Distance, Perimeter, Area }

/// <summary>
/// Skala strony: <see cref="PageValue"/> <see cref="PageUnit"/> na stronie = <see cref="RealValue"/> <see cref="RealUnit"/>
/// w rzeczywistości (np. 1 cm = 2 m). Zapisywana w słowniku /Measure (/R, /X, /D, /A – ISO 32000-1 12.9) w /VP strony
/// i w każdej adnotacji pomiarowej.
/// </summary>
public sealed record MeasureScale(double PageValue = 1, string PageUnit = "cm", double RealValue = 1, string RealUnit = "cm", int Decimals = 2)
{
    /// <summary>Jednostki obsługiwane w oknie skali (nazwy jak w /U).</summary>
    public static IReadOnlyList<string> Units { get; } = new[] { "mm", "cm", "m", "km", "in", "ft", "yd", "pt" };

    /// <summary>Skala 1:1 w centymetrach (domyślna, gdy strona nie ma /VP).</summary>
    public static MeasureScale Default { get; } = new();

    /// <summary>Punkty PDF (1/72 cala) w jednej jednostce długości.</summary>
    public static double PointsPer(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "mm" => 72 / 25.4,
        "cm" => 72 / 2.54,
        "m" => 72 / 0.0254,
        "km" => 72 / 0.0000254,
        "in" or "\"" => 72,
        "ft" => 72 * 12,
        "yd" => 72 * 36,
        "pt" => 1,
        _ => throw new ArgumentException("Unknown unit: " + unit, nameof(unit)),
    };

    public static bool IsKnownUnit(string unit) => Units.Contains(unit.Trim().ToLowerInvariant());

    /// <summary>Wartość rzeczywista (w <see cref="RealUnit"/>) odpowiadająca jednemu punktowi PDF.</summary>
    public double RealPerPoint => RealValue / (PageValue * PointsPer(PageUnit));

    public bool IsValid => PageValue > 0 && RealValue > 0 && double.IsFinite(PageValue) && double.IsFinite(RealValue) && IsKnownUnit(PageUnit) && IsKnownUnit(RealUnit);

    /// <summary>Tekst /R, np. "1 cm = 2 m" (w kulturze dokumentu).</summary>
    public string Ratio(CultureInfo culture) =>
        $"{PageValue.ToString("0.######", culture)} {PageUnit} = {RealValue.ToString("0.######", culture)} {RealUnit}";

    /// <summary>
    /// Skala z kalibracji: odcinek o długości <paramref name="measuredPoints"/> (punkty PDF) ma w rzeczywistości
    /// <paramref name="realLength"/> <paramref name="realUnit"/>. Wynik w postaci „1 <paramref name="pageUnit"/> = x jednostek”.
    /// </summary>
    public static MeasureScale Calibrate(double measuredPoints, double realLength, string realUnit, string pageUnit = "cm", int decimals = 2)
    {
        if (measuredPoints <= 0 || realLength <= 0) throw new ArgumentOutOfRangeException(nameof(measuredPoints));
        double pageLen = measuredPoints / PointsPer(pageUnit);
        return new MeasureScale(1, pageUnit, Math.Round(realLength / pageLen, 6), realUnit, decimals);
    }
}

/// <summary>
/// Format liczb pomiaru odczytany ze słownika /Measure (także z plików innych programów): przelicznik punkt → jednostka,
/// jednostka długości i pola, dokładność, separatory (/RD, /SS).
/// </summary>
public sealed record MeasureFormat(double RealPerPoint, string Unit, string AreaUnit, double AreaFactor, int Decimals, string DecimalSeparator, string ThousandsSeparator)
{
    public static MeasureFormat From(MeasureScale scale, CultureInfo culture) => new(
        scale.RealPerPoint, scale.RealUnit, scale.RealUnit + "²", 1, Math.Clamp(scale.Decimals, 0, 6),
        culture.NumberFormat.NumberDecimalSeparator, culture.NumberFormat.NumberGroupSeparator);

    /// <summary>Wartość pomiaru w jednostkach rzeczywistych.</summary>
    public double Value(MeasureType type, IReadOnlyList<PdfPoint> points) => type switch
    {
        MeasureType.Distance => points.Count >= 2 ? MeasureGeometry.Length(points[0], points[1]) * RealPerPoint : 0,
        MeasureType.Perimeter => MeasureGeometry.PathLength(points, closed: false) * RealPerPoint,
        _ => MeasureGeometry.PolygonArea(points) * RealPerPoint * RealPerPoint * AreaFactor,
    };

    public string UnitFor(MeasureType type) => type == MeasureType.Area ? AreaUnit : Unit;

    /// <summary>Etykieta, np. „12,5 cm” albo „1 234,56 m²” (zera końcowe usuwane).</summary>
    public string Label(MeasureType type, double value) => FormatNumber(value) + " " + UnitFor(type);

    public string FormatNumber(double value)
    {
        var s = Math.Round(value, Decimals, MidpointRounding.AwayFromZero).ToString("F" + Decimals, CultureInfo.InvariantCulture);
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        var parts = s.Split('.');
        var intPart = parts[0];
        var frac = parts.Length > 1 ? parts[1].TrimEnd('0') : string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < intPart.Length; i++)
        {
            if (i > 0 && (intPart.Length - i) % 3 == 0) sb.Append(ThousandsSeparator);
            sb.Append(intPart[i]);
        }
        if (frac.Length > 0) sb.Append(DecimalSeparator).Append(frac);
        var r = sb.ToString();
        return neg && r.Any(c => c is >= '1' and <= '9') ? "-" + r : r;
    }
}

/// <summary>Geometria pomiarów (punkty PDF).</summary>
public static class MeasureGeometry
{
    public static double Length(PdfPoint a, PdfPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double PathLength(IReadOnlyList<PdfPoint> pts, bool closed)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += Length(pts[i - 1], pts[i]);
        if (closed && pts.Count > 2) s += Length(pts[^1], pts[0]);
        return s;
    }

    /// <summary>Pole wielokąta (wzór Gaussa), wartość bezwzględna.</summary>
    public static double PolygonArea(IReadOnlyList<PdfPoint> pts)
    {
        if (pts.Count < 3) return 0;
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a) / 2;
    }

    /// <summary>Środek ciężkości wielokąta (dla etykiety pola); przy zerowym polu – średnia punktów.</summary>
    public static PdfPoint Centroid(IReadOnlyList<PdfPoint> pts)
    {
        if (pts.Count == 0) return default;
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            double cross = p.X * q.Y - q.X * p.Y;
            a += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        if (Math.Abs(a) < 1e-9) return new PdfPoint(pts.Average(p => p.X), pts.Average(p => p.Y));
        a /= 2;
        return new PdfPoint(cx / (6 * a), cy / (6 * a));
    }
}

/// <summary>Opis nowego pomiaru.</summary>
public sealed record MeasureSpec
{
    public required MeasureType Type { get; init; }
    public required int PageIndex { get; init; }
    public required IReadOnlyList<PdfPoint> Points { get; init; }
    /// <summary>Skala; null = skala strony z /VP (albo domyślna 1:1 cm).</summary>
    public MeasureScale? Scale { get; init; }
    public PdfColor Color { get; init; } = new(255, 220, 30, 30);
    public double LineWidth { get; init; } = 1;
    public double Opacity { get; init; } = 1;
    public string? Author { get; init; }
}

/// <summary>Pomiar odczytany z dokumentu (panel Komentarze, eksport CSV).</summary>
public sealed record MeasurementInfo(AnnotationId Id, MeasureType Type, double Value, string Unit, string Label, string Author, DateTimeOffset? Created, string Ratio);

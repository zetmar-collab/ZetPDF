using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZetPDF.Security.Pdf;

namespace ZetPDF.Security.Signatures;

/// <summary>
/// Wygląd widocznego podpisu: wiersze tekstu (imię, data, powód, miejsce) jako kontury glifów (ścieżki PDF –
/// polskie znaki bez osadzania czcionki) i opcjonalny obraz po lewej stronie. Kolor treści: czarny (treść PDF).
/// Budowa geometrii WPF na osobnym wątku STA.
/// </summary>
internal static class SignatureAppearanceBuilder
{
    public const string ImageName = "Im1";
    private const double Padding = 2;

    public sealed record Line(string Text, bool Bold);

    public static AppearanceStreams Build(double width, double height, IReadOnlyList<Line> lines, byte[]? image)
        => StaRunner.Run(() => BuildOnSta(width, height, lines, image));

    private static AppearanceStreams BuildOnSta(double width, double height, IReadOnlyList<Line> lines, byte[]? image)
    {
        var content = new StringBuilder();
        CosStream? imageStream = null;
        double textLeft = Padding;

        if (image is { Length: > 0 })
        {
            var decoded = DecodeImage(image);
            if (decoded is not null)
            {
                imageStream = decoded.Value.Stream;
                double areaW = lines.Count == 0 ? width - 2 * Padding : Math.Max(1, (width - 2 * Padding) * 0.4);
                double areaH = height - 2 * Padding;
                double scale = Math.Min(areaW / decoded.Value.Width, areaH / decoded.Value.Height);
                double w = decoded.Value.Width * scale, h = decoded.Value.Height * scale;
                double x = Padding + (areaW - w) / 2, y = Padding + (areaH - h) / 2;
                content.Append(CultureInfo.InvariantCulture, $"q {F(w)} 0 0 {F(h)} {F(x)} {F(y)} cm /{ImageName} Do Q\n");
                textLeft = Padding + areaW + Padding;
            }
        }

        if (lines.Count > 0)
        {
            double availW = Math.Max(1, width - textLeft - Padding);
            double availH = Math.Max(1, height - 2 * Padding);
            var regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            double size = Math.Min(14, availH / (lines.Count * 1.25));
            double maxW = lines.Max(l => Measure(l.Text, l.Bold ? bold : regular, size).WidthIncludingTrailingWhitespace);
            if (maxW > availW) size *= availW / maxW;
            size = Math.Max(2, size);
            double lineH = size * 1.25;
            double top = Padding + (availH - lineH * lines.Count) / 2; // od góry pola (Y w dół)
            content.Append("q 0 g\n");
            for (int i = 0; i < lines.Count; i++)
            {
                var ft = Measure(lines[i].Text, lines[i].Bold ? bold : regular, size);
                var geometry = ft.BuildGeometry(new Point(textLeft, top + i * lineH));
                AppendGeometry(content, geometry, height);
            }
            content.Append("Q\n");
        }

        var resources = new CosDictionary();
        var form = new CosStream(new CosDictionary
        {
            ["Type"] = new CosName("XObject"),
            ["Subtype"] = new CosName("Form"),
            ["BBox"] = CosArray.OfNumbers(0, 0, width, height),
            ["Resources"] = resources,
            ["Filter"] = new CosName("FlateDecode"),
        }, CosFilters.Deflate(Encoding.ASCII.GetBytes(content.ToString())));
        return new AppearanceStreams(form, imageStream, ImageName);
    }

    private static FormattedText Measure(string text, Typeface face, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0);

    private static string F(double v) => CosNumber.FormatReal(Math.Round(v, 3));

    /// <summary>Kontury WPF (Y w dół) → operatory ścieżki PDF (Y w górę), wypełnienie nonzero.</summary>
    private static void AppendGeometry(StringBuilder sb, Geometry geometry, double height)
    {
        var path = PathGeometry.CreateFromGeometry(geometry);
        if (path.Figures.Count == 0) return;
        string P(Point p) => $"{F(p.X)} {F(height - p.Y)}";
        foreach (var figure in path.Figures)
        {
            if (figure.Segments.OfType<ArcSegment>().Any())
            {
                var flat = new PathGeometry(new[] { figure.Clone() }).GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
                foreach (var f in flat.Figures) AppendFigure(sb, f, P);
                continue;
            }
            AppendFigure(sb, figure, P);
        }
        sb.Append("f\n");
    }

    private static void AppendFigure(StringBuilder sb, PathFigure figure, Func<Point, string> P)
    {
        var current = figure.StartPoint;
        sb.Append(P(current)).Append(" m\n");
        foreach (var seg in figure.Segments)
        {
            switch (seg)
            {
                case LineSegment l:
                    sb.Append(P(l.Point)).Append(" l\n"); current = l.Point; break;
                case PolyLineSegment pl:
                    foreach (var p in pl.Points) sb.Append(P(p)).Append(" l\n");
                    if (pl.Points.Count > 0) current = pl.Points[^1];
                    break;
                case BezierSegment b:
                    sb.Append(P(b.Point1)).Append(' ').Append(P(b.Point2)).Append(' ').Append(P(b.Point3)).Append(" c\n"); current = b.Point3; break;
                case PolyBezierSegment pb:
                    for (int i = 0; i + 2 < pb.Points.Count; i += 3)
                        sb.Append(P(pb.Points[i])).Append(' ').Append(P(pb.Points[i + 1])).Append(' ').Append(P(pb.Points[i + 2])).Append(" c\n");
                    if (pb.Points.Count > 0) current = pb.Points[^1];
                    break;
                case QuadraticBezierSegment q:
                    AppendQuad(sb, P, current, q.Point1, q.Point2); current = q.Point2; break;
                case PolyQuadraticBezierSegment pq:
                    for (int i = 0; i + 1 < pq.Points.Count; i += 2) { AppendQuad(sb, P, current, pq.Points[i], pq.Points[i + 1]); current = pq.Points[i + 1]; }
                    break;
            }
        }
        if (figure.IsClosed) sb.Append("h\n");
    }

    private static void AppendQuad(StringBuilder sb, Func<Point, string> P, Point p0, Point q, Point p2)
    {
        var c1 = new Point(p0.X + 2.0 / 3 * (q.X - p0.X), p0.Y + 2.0 / 3 * (q.Y - p0.Y));
        var c2 = new Point(p2.X + 2.0 / 3 * (q.X - p2.X), p2.Y + 2.0 / 3 * (q.Y - p2.Y));
        sb.Append(P(c1)).Append(' ').Append(P(c2)).Append(' ').Append(P(p2)).Append(" c\n");
    }

    /// <summary>Obraz (PNG/JPEG/BMP…) → XObject RGB (Flate) + SMask dla przezroczystości.</summary>
    private static (CosStream Stream, int Width, int Height)? DecodeImage(byte[] image)
    {
        BitmapSource src;
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(image), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            src = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool hasAlpha = false;
        for (int i = 0, j = 0, k = 0; i < px.Length; i += 4, j += 3, k++)
        {
            rgb[j] = px[i + 2]; rgb[j + 1] = px[i + 1]; rgb[j + 2] = px[i];
            alpha[k] = px[i + 3];
            if (px[i + 3] != 255) hasAlpha = true;
        }
        var dict = new CosDictionary
        {
            ["Type"] = new CosName("XObject"),
            ["Subtype"] = new CosName("Image"),
            ["Width"] = new CosNumber(w),
            ["Height"] = new CosNumber(h),
            ["ColorSpace"] = new CosName("DeviceRGB"),
            ["BitsPerComponent"] = new CosNumber(8),
            ["Filter"] = new CosName("FlateDecode"),
        };
        if (hasAlpha)
        {
            // SMask jako obiekt bezpośredni nie jest dozwolony – wstawiamy strumień przez specjalny znacznik,
            // który PdfSignatureWriter zamienia na referencję (patrz SMaskHolder).
            dict["SMask"] = new SMaskHolder(new CosStream(new CosDictionary
            {
                ["Type"] = new CosName("XObject"),
                ["Subtype"] = new CosName("Image"),
                ["Width"] = new CosNumber(w),
                ["Height"] = new CosNumber(h),
                ["ColorSpace"] = new CosName("DeviceGray"),
                ["BitsPerComponent"] = new CosNumber(8),
                ["Filter"] = new CosName("FlateDecode"),
            }, CosFilters.Deflate(alpha)));
        }
        return (new CosStream(dict, CosFilters.Deflate(rgb)), w, h);
    }
}

/// <summary>Strumień maski, który musi zostać zapisany jako obiekt pośredni (zamieniany przy dodawaniu obrazu).</summary>
internal sealed class SMaskHolder : CosObject
{
    public SMaskHolder(CosStream stream) => Stream = stream;
    public CosStream Stream { get; }
}

/// <summary>Uruchamia kod WPF (geometria, dekodery obrazów) na osobnym wątku STA.</summary>
internal static class StaRunner
{
    public static T Run<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return func();
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}

using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Generator wyglądu /AP /N adnotacji (widocznego w każdym czytniku, także w PDFium). Rysunki wektorowe – ręczny
/// strumień treści w przestrzeni strony (BBox = /Rect); tekst i obrazy – XForm PDFsharp (BBox [0 0 w h], czytnik
/// dopasowuje go do /Rect).
/// </summary>
internal static class AppearanceBuilder
{
    /// <summary>Generuje i ustawia /AP. Zwraca false, gdy wyglądu nie da się odtworzyć (np. stempel z obrazu) – zostaje stary.</summary>
    public static bool Apply(PdfDocument doc, PdfDictionary annot, AnnotModel m)
    {
        PdfDictionary? normal = MeasureDict.TypeOf(annot) is { } measure ? MeasureAppearance.Build(doc, annot, m, measure) : m.Kind switch
        {
            AnnotationKind.Highlight or AnnotationKind.AreaHighlight => Markup(doc, m, MarkupStyle.Highlight),
            AnnotationKind.Underline => Markup(doc, m, MarkupStyle.Underline),
            AnnotationKind.StrikeOut => Markup(doc, m, MarkupStyle.StrikeOut),
            AnnotationKind.Squiggly => Markup(doc, m, MarkupStyle.Squiggly),
            AnnotationKind.Square => Shape(doc, m, ellipse: false),
            AnnotationKind.Circle => Shape(doc, m, ellipse: true),
            AnnotationKind.Line or AnnotationKind.Arrow => LineAp(doc, m),
            AnnotationKind.Polygon => Poly(doc, m, closed: true),
            AnnotationKind.Polyline => Poly(doc, m, closed: false),
            AnnotationKind.Ink => InkAp(doc, m),
            AnnotationKind.Note => NoteIcon(doc, m),
            AnnotationKind.FreeText or AnnotationKind.Typewriter or AnnotationKind.Callout => FreeText(doc, m),
            AnnotationKind.Stamp => Stamp(doc, m),
            AnnotationKind.FileAttachment => Paperclip(doc, m),
            AnnotationKind.Redact => RedactOutline(doc, m),
            _ => null,
        };
        if (normal is null) return false;
        var ap = NewDict(doc);
        ap.Elements["/N"] = normal.Reference!;
        annot.Elements["/AP"] = ap;
        return true;
    }

    private static PdfColor ColorOr(PdfColor? c, PdfColor fallback) => c ?? fallback;

    /// <summary>Zasoby z ExtGState (krycie, opcjonalnie tryb mieszania Multiply).</summary>
    private static PdfDictionary GsResources(PdfDocument doc, double opacity, bool multiply)
    {
        var gs = NewDict(doc, "/ExtGState");
        gs.Elements["/CA"] = Real(opacity);
        gs.Elements["/ca"] = Real(opacity);
        if (multiply) gs.Elements["/BM"] = Name("/Multiply");
        var ext = NewDict(doc);
        ext.Elements["/GS0"] = gs;
        var res = NewDict(doc);
        res.Elements["/ExtGState"] = ext;
        return res;
    }

    // ------------------------------------------------------------------ adiustacja tekstu

    private enum MarkupStyle { Highlight, Underline, StrikeOut, Squiggly }

    private static PdfPoint At(PdfQuad q, double t, double s)
    {
        // t wzdłuż linii bazowej (0 = lewo), s w górę (0 = dół)
        var ll = q.LowerLeft; var lr = q.LowerRight; var ul = q.UpperLeft;
        return new PdfPoint(ll.X + t * (lr.X - ll.X) + s * (ul.X - ll.X), ll.Y + t * (lr.Y - ll.Y) + s * (ul.Y - ll.Y));
    }

    private static double Dist(PdfPoint a, PdfPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static PdfDictionary? Markup(PdfDocument doc, AnnotModel m, MarkupStyle style)
    {
        var quads = m.Quads.Count > 0 ? m.Quads : new List<PdfQuad> { PdfQuad.FromRect(m.Rect) };
        var color = ColorOr(m.Color, PdfColor.Yellow);
        var c = new ContentBuilder().Gs("GS0");
        if (style == MarkupStyle.Highlight)
        {
            c.Fill(color);
            foreach (var q in quads)
            {
                c.Move(q.LowerLeft).Line(q.LowerRight).Line(q.UpperRight).Line(q.UpperLeft).Close();
            }
            c.Op("f");
        }
        else
        {
            c.Stroke(color);
            foreach (var q in quads)
            {
                double h = Dist(q.LowerLeft, q.UpperLeft);
                double len = Dist(q.LowerLeft, q.LowerRight);
                if (h <= 0 || len <= 0) continue;
                double w = Math.Max(0.5, h / 14);
                c.Width(w);
                switch (style)
                {
                    case MarkupStyle.Underline:
                        c.Move(At(q, 0, -w / h)).Line(At(q, 1, -w / h)).Op("S");
                        break;
                    case MarkupStyle.StrikeOut:
                        c.Move(At(q, 0, 0.45)).Line(At(q, 1, 0.45)).Op("S");
                        break;
                    case MarkupStyle.Squiggly:
                        double period = Math.Max(2, h / 4), amp = h / 12;
                        int n = Math.Max(2, (int)Math.Ceiling(len / (period / 2)));
                        c.Move(At(q, 0, -amp / h));
                        for (int i = 1; i <= n; i++)
                            c.Line(At(q, Math.Min(1, i * (period / 2) / len), (i % 2 == 1 ? amp : -amp) / h));
                        c.Op("S");
                        break;
                }
            }
        }
        return FormXObject(doc, m.Rect, c.ToString(), GsResources(doc, m.Opacity, style == MarkupStyle.Highlight));
    }

    // ------------------------------------------------------------------ rysunki

    private static PdfDictionary Shape(PdfDocument doc, AnnotModel m, bool ellipse)
    {
        double lw = Math.Max(0, m.LineWidth);
        var r = m.Rect.Inflate(-lw / 2);
        if (r.IsEmpty) r = m.Rect;
        var c = new ContentBuilder().Gs("GS0").Width(lw).Stroke(ColorOr(m.Color, PdfColor.Red));
        if (m.Fill is { } f) c.Fill(f);
        if (ellipse) c.Ellipse(r); else c.Rect(r);
        c.Op(m.Fill is not null ? (lw > 0 ? "B" : "f") : (lw > 0 ? "S" : "n"));
        return FormXObject(doc, m.Rect, c.ToString(), GsResources(doc, m.Opacity, false));
    }

    public static double ArrowSize(double lineWidth) => Math.Max(6, lineWidth * 3 + 4);

    private static PdfDictionary LineAp(PdfDocument doc, AnnotModel m)
    {
        var c = new ContentBuilder().Gs("GS0").Width(Math.Max(0.1, m.LineWidth)).Round().Stroke(ColorOr(m.Color, PdfColor.Red));
        if (m.Points.Count >= 2)
        {
            var a = m.Points[0];
            var b = m.Points[1];
            c.Move(a).Line(b).Op("S");
            if (m.Kind == AnnotationKind.Arrow || m.LineEnd is { } le && le != "None")
                c.OpenArrow(a, b, ArrowSize(m.LineWidth)).Op("S");
        }
        return FormXObject(doc, m.Rect, c.ToString(), GsResources(doc, m.Opacity, false));
    }

    private static PdfDictionary Poly(PdfDocument doc, AnnotModel m, bool closed)
    {
        var c = new ContentBuilder().Gs("GS0").Width(Math.Max(0.1, m.LineWidth)).Round().Stroke(ColorOr(m.Color, PdfColor.Red));
        if (closed && m.Fill is { } f) c.Fill(f);
        c.Polyline(m.Points, closed);
        c.Op(closed && m.Fill is not null ? "B" : "S");
        return FormXObject(doc, m.Rect, c.ToString(), GsResources(doc, m.Opacity, false));
    }

    private static PdfDictionary InkAp(PdfDocument doc, AnnotModel m)
    {
        var c = new ContentBuilder().Gs("GS0").Width(Math.Max(0.1, m.LineWidth)).Round().Stroke(ColorOr(m.Color, PdfColor.Red));
        foreach (var s in m.Ink)
        {
            if (s.Count == 1) c.Move(s[0]).Line(new PdfPoint(s[0].X + 0.01, s[0].Y));
            else c.Polyline(s);
            c.Op("S");
        }
        return FormXObject(doc, m.Rect, c.ToString(), GsResources(doc, m.Opacity, false));
    }

    private static PdfDictionary NoteIcon(PdfDocument doc, AnnotModel m)
    {
        var r = m.Rect;
        double w = r.Width, h = r.Height;
        PdfPoint P(double x, double y) => new(r.Left + x * w, r.Bottom + y * h);
        var c = new ContentBuilder().Gs("GS0").Width(Math.Max(0.6, w / 30)).Round()
            .Fill(ColorOr(m.Color, PdfColor.Yellow)).Stroke(new PdfColor(255, 60, 60, 60));
        // dymek z ogonkiem
        c.Move(P(0.1, 0.95)).Line(P(0.9, 0.95)).Line(P(0.9, 0.3)).Line(P(0.45, 0.3)).Line(P(0.2, 0.05)).Line(P(0.25, 0.3)).Line(P(0.1, 0.3)).Close().Op("B");
        c.Width(Math.Max(0.5, w / 40));
        foreach (double y in new[] { 0.78, 0.63, 0.48 }) c.Move(P(0.22, y)).Line(P(0.78, y));
        c.Op("S");
        return FormXObject(doc, r, c.ToString(), GsResources(doc, m.Opacity, false));
    }

    private static PdfDictionary Paperclip(PdfDocument doc, AnnotModel m)
    {
        var r = m.Rect;
        PdfPoint P(double x, double y) => new(r.Left + x * r.Width, r.Bottom + y * r.Height);
        var c = new ContentBuilder().Gs("GS0").Width(Math.Max(0.8, r.Width / 14)).Round().Stroke(ColorOr(m.Color, new PdfColor(255, 60, 60, 60)));
        c.Move(P(0.65, 0.7)).Line(P(0.65, 0.25)).Curve(P(0.65, 0.05), P(0.35, 0.05), P(0.35, 0.25)).Line(P(0.35, 0.85))
         .Curve(P(0.35, 0.98), P(0.55, 0.98), P(0.55, 0.85)).Line(P(0.55, 0.3)).Op("S");
        return FormXObject(doc, r, c.ToString(), GsResources(doc, m.Opacity, false));
    }

    private static PdfDictionary RedactOutline(PdfDocument doc, AnnotModel m)
    {
        var c = new ContentBuilder().Width(1).Stroke(ColorOr(m.Color, PdfColor.Red));
        var quads = m.Quads.Count > 0 ? m.Quads : new List<PdfQuad> { PdfQuad.FromRect(m.Rect) };
        foreach (var q in quads) c.Move(q.LowerLeft).Line(q.LowerRight).Line(q.UpperRight).Line(q.UpperLeft).Close();
        c.Op("S");
        return FormXObject(doc, m.Rect, c.ToString());
    }

    // ------------------------------------------------------------------ tekst

    public const double TextPadding = 3;

    private static PdfDictionary FreeText(PdfDocument doc, AnnotModel m)
    {
        var rect = m.Rect;
        var box = m.Kind == AnnotationKind.Callout && m.TextRect is { } tr ? tr : rect;
        double lw = m.HasBorder ? Math.Max(0, m.LineWidth) : 0;
        var border = ColorOr(m.Color, m.TextColor);
        return XFormHelper.Draw(doc, rect.Width, rect.Height, g =>
        {
            XPoint F(PdfPoint p) => new(p.X - rect.Left, rect.Top - p.Y);
            var b = new XRect(F(new(box.Left, box.Top)), F(new(box.Right, box.Bottom)));
            if (m.Kind == AnnotationKind.Callout && m.Points.Count >= 2)
            {
                var pen = new XPen(XFormHelper.X(border, m.Opacity), Math.Max(0.5, lw > 0 ? lw : 1)) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
                var pts = m.Points.Select(F).ToArray();
                g.DrawLines(pen, pts);
                // grot przy punkcie docelowym (pierwszy punkt)
                var tip = pts[0];
                var from = pts[1];
                double dx = tip.X - from.X, dy = tip.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-6)
                {
                    dx /= len; dy /= len;
                    double size = ArrowSize(pen.Width);
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        double a = sgn * Math.PI / 6, cos = Math.Cos(a), sin = Math.Sin(a);
                        g.DrawLine(pen, tip, new XPoint(tip.X + (-dx * cos + dy * sin) * size, tip.Y + (-dy * cos - dx * sin) * size));
                    }
                }
            }
            var inner = b;
            if (lw > 0) inner.Inflate(-lw / 2, -lw / 2);
            if (m.Fill is { } f) g.DrawRectangle(new XSolidBrush(XFormHelper.X(f, m.Opacity)), inner);
            if (lw > 0) g.DrawRectangle(new XPen(XFormHelper.X(border, m.Opacity), lw), inner);
            var font = XFormHelper.Font(m.FontFamily, m.FontSize);
            var textBox = new XRect(b.Left + lw + TextPadding, b.Top + lw + TextPadding / 2, Math.Max(1, b.Width - 2 * (lw + TextPadding)), Math.Max(1, b.Height - 2 * lw - TextPadding));
            var lines = XFormHelper.Wrap(m.Contents, font, textBox.Width);
            XFormHelper.DrawLines(g, lines, font, new XSolidBrush(XFormHelper.X(m.TextColor, m.Opacity)), textBox);
        });
    }

    /// <summary>Rozmiar prostokąta dla tekstu (pole tekstowe / maszyna do pisania) przy danej szerokości maks.</summary>
    public static (double Width, double Height) MeasureFreeText(string text, string family, double fontSize, double lineWidth, double maxWidth)
    {
        var font = XFormHelper.Font(family, fontSize);
        double pad = lineWidth + TextPadding;
        var lines = XFormHelper.Wrap(string.IsNullOrEmpty(text) ? " " : text, font, Math.Max(10, maxWidth - 2 * pad));
        double w = lines.Max(l => XFormHelper.Measure(l, font).Width) + 2 * pad + 2;
        double h = lines.Count * font.GetHeight() + 2 * lineWidth + TextPadding + 2;
        return (Math.Max(w, fontSize), Math.Max(h, fontSize + 2 * pad));
    }

    // ------------------------------------------------------------------ stemple

    private static PdfDictionary? Stamp(PdfDocument doc, AnnotModel m)
    {
        if (m.IsImageStamp) return null;
        var rect = m.Rect;
        if (StampLibrary.IsFillSignMark(m.StampName)) return FillSignMark(doc, m);
        var color = ColorOr(m.Color, new PdfColor(255, 196, 30, 36));
        var lines = m.StampLines.Count > 0 ? m.StampLines : new List<string> { m.StampName ?? m.Contents };
        return XFormHelper.Draw(doc, rect.Width, rect.Height, g =>
        {
            double w = rect.Width, h = rect.Height;
            double lw = Math.Max(1.5, Math.Min(w, h) / 18);
            var pen = new XPen(XFormHelper.X(color, m.Opacity), lw);
            g.DrawRoundedRectangle(pen, new XSolidBrush(XFormHelper.X(color, m.Opacity * 0.08)), lw / 2, lw / 2, w - lw, h - lw, h / 4, h / 4);
            var brush = new XSolidBrush(XFormHelper.X(color, m.Opacity));
            var fmt = new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.Center };
            if (lines.Count == 1)
            {
                var font = FitFont(lines[0], w - 4 * lw, h * 0.6, bold: true);
                g.DrawString(lines[0], font, brush, new XRect(0, 0, w, h), fmt);
            }
            else
            {
                var big = FitFont(lines[0], w - 4 * lw, h * 0.45, bold: true);
                var small = FitFont(lines[1], w - 4 * lw, h * 0.2, bold: false);
                g.DrawString(lines[0], big, brush, new XRect(0, lw, w, h * 0.62), fmt);
                g.DrawString(lines[1], small, brush, new XRect(0, h * 0.6, w, h * 0.34), fmt);
            }
        });
    }

    private static XFont FitFont(string text, double maxWidth, double maxHeight, bool bold)
    {
        double size = Math.Max(4, maxHeight);
        var font = XFormHelper.Font("Arial", size, bold);
        var s = XFormHelper.Measure(text, font);
        if (s.Width > maxWidth && s.Width > 0)
        {
            size = Math.Max(4, size * maxWidth / s.Width);
            font = XFormHelper.Font("Arial", size, bold);
        }
        return font;
    }

    /// <summary>Rozmiar stempla standardowego (punkty) dla wierszy tekstu.</summary>
    public static (double Width, double Height) MeasureStamp(IReadOnlyList<string> lines)
    {
        var big = XFormHelper.Font("Arial", 22, bold: true);
        double w = XFormHelper.Measure(lines[0], big).Width + 30;
        if (lines.Count > 1) w = Math.Max(w, XFormHelper.Measure(lines[1], XFormHelper.Font("Arial", 9)).Width + 30);
        return (Math.Max(120, w), lines.Count > 1 ? 52 : 40);
    }

    private static PdfDictionary FillSignMark(PdfDocument doc, AnnotModel m)
    {
        var r = m.Rect;
        PdfPoint P(double x, double y) => new(r.Left + x * r.Width, r.Bottom + y * r.Height);
        var color = ColorOr(m.Color, PdfColor.Black);
        var c = new ContentBuilder().Gs("GS0").Round();
        double lw = Math.Max(1, Math.Min(r.Width, r.Height) / 7);
        switch (m.StampName)
        {
            case StampLibrary.Check:
                c.Width(lw).Stroke(color).Move(P(0.12, 0.5)).Line(P(0.4, 0.18)).Line(P(0.88, 0.85)).Op("S");
                break;
            case StampLibrary.Cross:
                c.Width(lw).Stroke(color).Move(P(0.15, 0.15)).Line(P(0.85, 0.85)).Move(P(0.15, 0.85)).Line(P(0.85, 0.15)).Op("S");
                break;
            default:
                c.Fill(color).Ellipse(new PdfRect(P(0.2, 0.2).X, P(0.2, 0.2).Y, P(0.8, 0.8).X, P(0.8, 0.8).Y)).Op("f");
                break;
        }
        return FormXObject(doc, r, c.ToString(), GsResources(doc, m.Opacity, false));
    }

    /// <summary>Stempel z obrazu PNG/JPG (także podpis odręczny) – przezroczystość PNG zachowana (SMask).</summary>
    public static PdfDictionary Image(PdfDocument doc, byte[] imageData, double width, double height, double opacity)
    {
        return XFormHelper.Draw(doc, width, height, g =>
        {
            using var ms = new MemoryStream(imageData, writable: false);
            var img = XImage.FromStream(ms);
            g.DrawImage(img, 0, 0, width, height);
        });
    }

    /// <summary>Rozmiar obrazu w pikselach (do zachowania proporcji).</summary>
    public static (int Width, int Height) ImageSize(byte[] imageData)
    {
        using var ms = new MemoryStream(imageData, writable: false);
        var img = XImage.FromStream(ms);
        return (img.PixelWidth, img.PixelHeight);
    }
}

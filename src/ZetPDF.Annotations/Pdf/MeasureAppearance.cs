using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Measure;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>
/// Wygląd /AP adnotacji pomiarowej: linia z kreskami końcowymi (odległość), linia łamana (obwód) albo wielokąt
/// z lekkim wypełnieniem (pole) oraz etykieta wartości (np. „12,5 cm”) z osadzoną czcionką. Wartość jest liczona
/// z geometrii i słownika /Measure przy każdym odtworzeniu wyglądu (przesunięcie, zmiana rozmiaru, zmiana skali),
/// zapisywana też w /Contents (podpis linii /Cap – widoczny w panelu komentarzy i w innych czytnikach).
/// </summary>
internal static class MeasureAppearance
{
    public const double LabelFontSize = 9;

    /// <summary>Liczy wartość, ustawia /Contents i /Rect (geometria + etykieta), zwraca formę wyglądu.</summary>
    public static PdfDictionary Build(PdfDocument doc, PdfDictionary annot, AnnotModel m, MeasureType type)
    {
        var measure = Dict(annot, "/Measure");
        var format = (measure is not null ? MeasureDict.ReadFormat(measure, CultureInfo.CurrentCulture) : null)
                     ?? MeasureFormat.From(MeasureScale.Default, CultureInfo.CurrentCulture);
        var pts = m.Points.Count > 0 ? m.Points : MeasureDict.Points(annot);
        double value = format.Value(type, pts);
        string label = format.Label(type, value);
        annot.Elements["/Contents"] = Str(label);

        var font = XFormHelper.Font("Arial", LabelFontSize);
        var size = XFormHelper.Measure(label, font);
        double lw = Math.Max(0.5, m.LineWidth);
        double tick = 4 + lw;
        var color = m.Color ?? new PdfColor(255, 220, 30, 30);

        // położenie etykiety (środek) w przestrzeni strony
        PdfPoint center;
        if (type == MeasureType.Distance && pts.Count >= 2)
        {
            var a = pts[0];
            var b = pts[1];
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
            double nx = -dy / len, ny = dx / len;
            if (ny < 0 || ny == 0 && nx < 0) { nx = -nx; ny = -ny; } // normalna „do góry”
            double off = Math.Abs(nx) * size.Width / 2 + Math.Abs(ny) * size.Height / 2 + tick;
            center = new PdfPoint((a.X + b.X) / 2 + nx * off, (a.Y + b.Y) / 2 + ny * off);
        }
        else if (type == MeasureType.Area && pts.Count >= 3) center = MeasureGeometry.Centroid(pts);
        else if (pts.Count > 0) center = new PdfPoint(pts[^1].X + size.Width / 2 + tick, pts[^1].Y + size.Height / 2 + tick);
        else center = new PdfPoint(m.Rect.Left, m.Rect.Top);
        var labelBox = new PdfRect(center.X - size.Width / 2 - 2, center.Y - size.Height / 2 - 1, center.X + size.Width / 2 + 2, center.Y + size.Height / 2 + 1);

        double l = labelBox.Left, bt = labelBox.Bottom, r = labelBox.Right, t = labelBox.Top;
        foreach (var p in pts)
        {
            l = Math.Min(l, p.X - tick); r = Math.Max(r, p.X + tick);
            bt = Math.Min(bt, p.Y - tick); t = Math.Max(t, p.Y + tick);
        }
        var rect = new PdfRect(l - 1, bt - 1, r + 1, t + 1);
        m.Rect = rect;
        annot.Elements["/Rect"] = RectArray(doc, rect);

        return XFormHelper.Draw(doc, rect.Width, rect.Height, g =>
        {
            XPoint F(PdfPoint p) => new(p.X - rect.Left, rect.Top - p.Y);
            var pen = new XPen(XFormHelper.X(color, m.Opacity), lw) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
            var xp = pts.Select(F).ToArray();
            switch (type)
            {
                case MeasureType.Distance when xp.Length >= 2:
                {
                    g.DrawLine(pen, xp[0], xp[1]);
                    double dx = xp[1].X - xp[0].X, dy = xp[1].Y - xp[0].Y, len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
                    double nx = -dy / len * tick, ny = dx / len * tick;
                    foreach (var e in new[] { xp[0], xp[1] }) g.DrawLine(pen, e.X - nx, e.Y - ny, e.X + nx, e.Y + ny);
                    break;
                }
                case MeasureType.Perimeter when xp.Length >= 2:
                    g.DrawLines(pen, xp);
                    foreach (var e in xp) g.DrawEllipse(new XSolidBrush(XFormHelper.X(color, m.Opacity)), e.X - lw, e.Y - lw, 2 * lw, 2 * lw);
                    break;
                case MeasureType.Area when xp.Length >= 3:
                    g.DrawPolygon(pen, new XSolidBrush(XFormHelper.X(color, m.Opacity * 0.15)), xp, XFillMode.Winding);
                    break;
            }
            var box = new XRect(F(new PdfPoint(labelBox.Left, labelBox.Top)), F(new PdfPoint(labelBox.Right, labelBox.Bottom)));
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb((int)(220 * m.Opacity), 255, 255, 255)), box);
            g.DrawString(label, font, new XSolidBrush(XFormHelper.X(color, m.Opacity)), box,
                new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.Center });
        });
    }
}

using System.Text;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>Prosty budowniczy strumienia treści PDF (operatory ścieżek i kolorów) dla wyglądów adnotacji.</summary>
internal sealed class ContentBuilder
{
    private readonly StringBuilder _sb = new();

    public override string ToString() => _sb.ToString();

    public ContentBuilder Op(string op) { _sb.Append(op).Append('\n'); return this; }
    public ContentBuilder Save() => Op("q");
    public ContentBuilder Restore() => Op("Q");
    public ContentBuilder Gs(string name) => Op("/" + name + " gs");
    public ContentBuilder Stroke(PdfColor c) => Op(Rgb(c, true));
    public ContentBuilder Fill(PdfColor c) => Op(Rgb(c, false));
    public ContentBuilder Width(double w) => Op(Num(w) + " w");
    public ContentBuilder Round() => Op("1 J 1 j");
    public ContentBuilder Dash(double on, double off) => Op($"[{Num(on)} {Num(off)}] 0 d");

    public ContentBuilder Move(PdfPoint p) => Op($"{Num(p.X)} {Num(p.Y)} m");
    public ContentBuilder Line(PdfPoint p) => Op($"{Num(p.X)} {Num(p.Y)} l");
    public ContentBuilder Curve(PdfPoint c1, PdfPoint c2, PdfPoint p) => Op($"{Num(c1.X)} {Num(c1.Y)} {Num(c2.X)} {Num(c2.Y)} {Num(p.X)} {Num(p.Y)} c");
    public ContentBuilder Rect(PdfRect r) => Op($"{Num(r.Left)} {Num(r.Bottom)} {Num(r.Width)} {Num(r.Height)} re");
    public ContentBuilder Close() => Op("h");

    public ContentBuilder Polyline(IReadOnlyList<PdfPoint> pts, bool close = false)
    {
        if (pts.Count == 0) return this;
        Move(pts[0]);
        for (int i = 1; i < pts.Count; i++) Line(pts[i]);
        if (close) Close();
        return this;
    }

    /// <summary>Elipsa wpisana w prostokąt (4 krzywe Béziera).</summary>
    public ContentBuilder Ellipse(PdfRect r)
    {
        const double k = 0.5522847498;
        double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2, rx = r.Width / 2, ry = r.Height / 2;
        Move(new(cx + rx, cy));
        Curve(new(cx + rx, cy + ry * k), new(cx + rx * k, cy + ry), new(cx, cy + ry));
        Curve(new(cx - rx * k, cy + ry), new(cx - rx, cy + ry * k), new(cx - rx, cy));
        Curve(new(cx - rx, cy - ry * k), new(cx - rx * k, cy - ry), new(cx, cy - ry));
        Curve(new(cx + rx * k, cy - ry), new(cx + rx, cy - ry * k), new(cx + rx, cy));
        return Close();
    }

    /// <summary>Zaokrąglony prostokąt.</summary>
    public ContentBuilder RoundRect(PdfRect r, double radius)
    {
        const double k = 0.5522847498;
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        double l = r.Left, b = r.Bottom, rt = r.Right, t = r.Top, c = radius * k;
        Move(new(l + radius, b));
        Line(new(rt - radius, b));
        Curve(new(rt - radius + c, b), new(rt, b + radius - c), new(rt, b + radius));
        Line(new(rt, t - radius));
        Curve(new(rt, t - radius + c), new(rt - radius + c, t), new(rt - radius, t));
        Line(new(l + radius, t));
        Curve(new(l + radius - c, t), new(l, t - radius + c), new(l, t - radius));
        Line(new(l, b + radius));
        Curve(new(l, b + radius - c), new(l + radius - c, b), new(l + radius, b));
        return Close();
    }

    /// <summary>Grot otwarty (dwie linie) na końcu odcinka from→tip.</summary>
    public ContentBuilder OpenArrow(PdfPoint from, PdfPoint tip, double size)
    {
        double dx = tip.X - from.X, dy = tip.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return this;
        dx /= len; dy /= len;
        const double ang = Math.PI / 6;
        PdfPoint Wing(double a)
        {
            double cos = Math.Cos(a), sin = Math.Sin(a);
            double rx = -dx * cos + dy * sin * (a > 0 ? 1 : 1), ry = -dy * cos - dx * sin;
            return new PdfPoint(tip.X + rx * size, tip.Y + ry * size);
        }
        var w1 = Wing(ang);
        var w2 = Wing(-ang);
        Move(w1); Line(tip); Line(w2);
        return this;
    }
}

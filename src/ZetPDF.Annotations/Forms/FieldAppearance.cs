using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZetPDF.Annotations.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Forms;

/// <summary>
/// Generowanie wyglądu /AP widżetów pól (tekst/lista/kombi/przycisk/podpis przez XForm z osadzoną czcionką Unicode,
/// pola wyboru i opcje wektorowo). Po wygenerowaniu /NeedAppearances = false – czytniki (w tym PDFium) pokazują
/// dokładnie ten wygląd, także z polskimi znakami.
/// </summary>
internal static class FieldAppearance
{
    private static readonly PdfColor SelectionFill = new(255, 153, 193, 218);

    public static PdfColor? MkColor(PdfDictionary widget, string key) =>
        Dict(widget, "/MK") is { } mk ? PdfLow.Color(mk, key) : null;

    /// <summary>Generuje wygląd wszystkich widżetów pola (pola wyboru/opcje – tylko gdy brak poprawnego /AP lub force).</summary>
    /// <param name="displayText">Tekst wyświetlany zamiast wartości (wynik skryptu formatu /AA /F); null = wartość.</param>
    public static void Build(PdfDocument doc, FieldNode field, bool forceStates = false, string? displayText = null)
    {
        var kind = field.Kind;
        // podpisane pole podpisu: wygląd należy do podpisu – nie nadpisujemy
        if (kind == FormFieldKind.Signature && field.Inherited("/V") is PdfDictionary) return;
        foreach (var w in field.Widgets)
        {
            switch (kind)
            {
                case FormFieldKind.CheckBox or FormFieldKind.RadioButton:
                    if (forceStates || !HasValidStates(w)) BuildStates(doc, field, w, kind == FormFieldKind.RadioButton);
                    SyncAs(field, w);
                    break;
                default:
                    if (BarcodeEngine.Options(field) is { } barcode) SetNormal(doc, w, BarcodeEngine.Appearance(doc, w, field.Value, barcode.Symbology));
                    else SetNormal(doc, w, BuildFlat(doc, field, w, displayText));
                    break;
            }
        }
    }

    public static bool HasValidStates(PdfDictionary w)
    {
        if (Dict(w, "/AP") is not { } ap || Dict(ap, "/N") is not { } n) return false;
        if (n.Stream is not null) return false;
        int streams = n.Elements.Keys.Count(k => Get(n, k) is PdfDictionary { Stream: not null });
        return streams >= 1 && streams == n.Elements.Count;
    }

    /// <summary>/AS widżetu zgodne z /V pola.</summary>
    public static void SyncAs(FieldNode field, PdfDictionary w)
    {
        var v = field.Value;
        var on = FieldNode.OnState(w);
        w.Elements["/AS"] = Name(on is not null && v == on ? "/" + on : "/Off");
    }

    private static void SetNormal(PdfDocument doc, PdfDictionary w, PdfDictionary form)
    {
        var ap = NewDict(doc);
        ap.Elements["/N"] = form.Reference!;
        w.Elements["/AP"] = ap;
    }

    private static PdfRect WidgetRect(PdfDictionary w) => PdfLow.Rect(w) ?? new PdfRect(0, 0, 10, 10);

    private static PdfDictionary BuildFlat(PdfDocument doc, FieldNode field, PdfDictionary w, string? displayText)
    {
        var r = WidgetRect(w);
        double width = Math.Max(1, r.Width), height = Math.Max(1, r.Height);
        var bg = MkColor(w, "/BG");
        var bc = MkColor(w, "/BC");
        double bw = Dict(w, "/BS") is { } bs ? Number(bs, "/W", 1) : 1;
        if (bc is null) bw = 0;
        var da = field.DaText;
        AnnotModel.ParseDa(da, out double fontSize, out var daColor);
        var textColor = daColor ?? PdfColor.Black;
        int q = (int)Number(field.Inherited("/Q"));
        var kind = field.Kind;
        bool multiline = (field.Flags & FieldNode.FfMultiline) != 0;
        string family = Dict(field.Dict, AnnotModel.PrivateKey) is { } priv && Text(priv, "/Font") is { Length: > 0 } f ? f : "Arial";

        return XFormHelper.Draw(doc, width, height, g =>
        {
            if (bg is { } b) g.DrawRectangle(new XSolidBrush(XFormHelper.X(b)), 0, 0, width, height);
            if (bw > 0 && bc is { } c) g.DrawRectangle(new XPen(XFormHelper.X(c), bw), bw / 2, bw / 2, width - bw, height - bw);
            double pad = Math.Max(2, bw * 2);
            var box = new XRect(pad, pad / 2, Math.Max(1, width - 2 * pad), Math.Max(1, height - pad));
            var brush = new XSolidBrush(XFormHelper.X(textColor));
            var align = q switch { 1 => XStringAlignment.Center, 2 => XStringAlignment.Far, _ => XStringAlignment.Near };
            switch (kind)
            {
                case FormFieldKind.Text or FormFieldKind.Date or FormFieldKind.ComboBox:
                {
                    string text = displayText ?? (kind == FormFieldKind.ComboBox ? DisplayOf(field, field.Value) : field.Value);
                    double size = fontSize > 0 ? fontSize : multiline ? 10 : Math.Clamp((height - 4) * 0.7, 4, 14);
                    var font = XFormHelper.Font(family, size);
                    if (multiline)
                    {
                        XFormHelper.DrawLines(g, XFormHelper.Wrap(text, font, box.Width), font, brush, box, align);
                    }
                    else
                    {
                        var fmt = new XStringFormat { Alignment = align, LineAlignment = XLineAlignment.Center };
                        g.DrawString(text, font, brush, new XRect(box.Left, 0, box.Width, height), fmt);
                    }
                    if (kind == FormFieldKind.ComboBox && bw > 0)
                    {
                        // znacznik rozwinięcia
                        double s = Math.Min(height * 0.3, 6), cx = width - pad - s, cy = height / 2;
                        g.DrawPolygon(brush, new[] { new XPoint(cx - s, cy - s / 2), new XPoint(cx + s, cy - s / 2), new XPoint(cx, cy + s / 2) }, XFillMode.Winding);
                    }
                    break;
                }
                case FormFieldKind.ListBox:
                {
                    double size = fontSize > 0 ? fontSize : 10;
                    var font = XFormHelper.Font(family, size);
                    double lh = font.GetHeight();
                    double y = pad / 2;
                    var value = field.Value;
                    foreach (var (export, display) in field.Options)
                    {
                        if (y + lh > height) break;
                        if (export == value || display == value)
                            g.DrawRectangle(new XSolidBrush(XFormHelper.X(SelectionFill)), bw, y, width - 2 * bw, lh);
                        g.DrawString(display, font, brush, new XRect(pad, y, box.Width, lh), new XStringFormat { Alignment = align, LineAlignment = XLineAlignment.Center });
                        y += lh;
                    }
                    break;
                }
                case FormFieldKind.PushButton:
                {
                    string caption = Dict(w, "/MK") is { } mk ? Text(mk, "/CA") : string.Empty;
                    double size = fontSize > 0 ? fontSize : Math.Clamp((height - 4) * 0.6, 4, 14);
                    var font = XFormHelper.Font(family, size);
                    g.DrawString(caption, font, brush, new XRect(0, 0, width, height), new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.Center });
                    break;
                }
                case FormFieldKind.Signature:
                    break;
            }
        });
    }

    public static string DisplayOf(FieldNode field, string value)
    {
        foreach (var (export, display) in field.Options)
            if (export == value) return display;
        return value;
    }

    /// <summary>Stany /AP pola wyboru lub opcji: włączony (nazwa eksportu) i /Off, wygląd normalny i wciśnięty.</summary>
    public static void BuildStates(PdfDocument doc, FieldNode field, PdfDictionary w, bool radio)
    {
        var on = FieldNode.OnState(w) ?? "Yes";
        var r = WidgetRect(w);
        var box = new PdfRect(0, 0, Math.Max(1, r.Width), Math.Max(1, r.Height));
        var bg = MkColor(w, "/BG");
        var bc = MkColor(w, "/BC");
        AnnotModel.ParseDa(field.DaText, out _, out var daColor);
        var mark = daColor ?? PdfColor.Black;

        string Frame(bool down)
        {
            var c = new ContentBuilder();
            var fill = down ? new PdfColor(255, 200, 200, 200) : bg;
            double bw = bc is null ? 0 : 1;
            var inner = box.Inflate(-bw / 2);
            if (radio)
            {
                if (fill is { } f) c.Fill(f).Ellipse(inner).Op("f");
                if (bc is { } s) c.Width(bw).Stroke(s).Ellipse(inner).Op("S");
            }
            else
            {
                if (fill is { } f) c.Fill(f).Rect(inner).Op("f");
                if (bc is { } s) c.Width(bw).Stroke(s).Rect(inner).Op("S");
            }
            return c.ToString();
        }

        string Mark()
        {
            var c = new ContentBuilder();
            double w = box.Width, h = box.Height;
            PdfPoint P(double x, double y) => new(x * w, y * h);
            if (radio)
                c.Fill(mark).Ellipse(new PdfRect(w * 0.28, h * 0.28, w * 0.72, h * 0.72)).Op("f");
            else
                c.Width(Math.Max(1, Math.Min(w, h) / 8)).Round().Stroke(mark).Move(P(0.2, 0.52)).Line(P(0.42, 0.25)).Line(P(0.8, 0.78)).Op("S");
            return c.ToString();
        }

        PdfDictionary States(bool down)
        {
            var d = NewDict(doc);
            d.Elements["/" + on] = FormXObject(doc, box, Frame(down) + Mark()).Reference!;
            d.Elements["/Off"] = FormXObject(doc, box, Frame(down)).Reference!;
            return d;
        }

        var ap = NewDict(doc);
        ap.Elements["/N"] = States(false);
        ap.Elements["/D"] = States(true);
        w.Elements["/AP"] = ap;
        var mk = Dict(w, "/MK") ?? NewDict(doc);
        if (!mk.Elements.ContainsKey("/CA")) mk.Elements["/CA"] = new PdfString(radio ? "l" : "4");
        w.Elements["/MK"] = mk;
    }
}

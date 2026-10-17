using PdfSharp.Pdf;
using ZetPDF.Core.Contracts;
using static ZetPDF.Annotations.Pdf.PdfLow;

namespace ZetPDF.Annotations.Pdf;

/// <summary>Tekstowe zależności fabryki (teksty stempli w języku interfejsu, nazwa typu do /Subj, autor domyślny).</summary>
internal interface IAnnotationTexts
{
    string DefaultAuthor { get; }
    string StampText(StampDefinition stamp);
    string KindName(AnnotationKind kind);
    string FormatStampDate(DateTimeOffset when);
}

/// <summary>Tworzy słownik nowej adnotacji ze <see cref="AnnotationSpec"/> razem z wyglądem /AP.</summary>
internal static class AnnotationFactory
{
    public const double NoteSize = 22;

    public static PdfDictionary Create(PdfDocument doc, AnnotationSpec spec, IAnnotationTexts texts, DateTimeOffset now, string? name = null)
    {
        if (spec.PageIndex < 0 || spec.PageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(spec), "Page index out of range.");
        var page = doc.Pages[spec.PageIndex];
        var annots = EnsureAnnots(page);
        string author = string.IsNullOrWhiteSpace(spec.Author) ? texts.DefaultAuthor : spec.Author!;
        var kind = spec.Kind;
        var m = new AnnotModel
        {
            Kind = kind,
            Subtype = AnnotModel.SubtypeFor(kind),
            Color = spec.Color,
            Fill = spec.FillColor,
            Opacity = Math.Clamp(spec.Opacity, 0.05, 1),
            LineWidth = Math.Max(0, spec.LineWidth),
            Contents = spec.Contents ?? string.Empty,
            Icon = spec.IconName,
        };
        var d = AnnotationStore.NewAnnot(doc, page, m.Subtype, name ?? NewName(), author, now);
        var priv = NewDict(doc);
        if (kind is AnnotationKind.AreaHighlight) priv.Elements["/Kind"] = Name("/AreaHighlight");

        switch (kind)
        {
            case AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.Squiggly or AnnotationKind.StrikeOut or AnnotationKind.Redact:
                if (spec.Quads is { Count: > 0 } qs) m.Quads.AddRange(qs);
                else if (!spec.Rect.IsEmpty) m.Quads.Add(PdfQuad.FromRect(spec.Rect));
                else throw new ArgumentException("Text markup requires quads or a rectangle.", nameof(spec));
                double h = m.Quads.Max(q => q.Bounds.Height);
                m.Rect = m.GeometryBounds(Math.Max(1, h * 0.2));
                break;
            case AnnotationKind.AreaHighlight:
                m.Rect = spec.Rect;
                m.Quads.Add(PdfQuad.FromRect(spec.Rect));
                break;
            case AnnotationKind.Square or AnnotationKind.Circle:
                m.Rect = spec.Rect;
                break;
            case AnnotationKind.Line or AnnotationKind.Arrow:
                if (spec.Points is not { Count: >= 2 }) throw new ArgumentException("Line requires 2 points.", nameof(spec));
                m.Points.Add(spec.Points[0]);
                m.Points.Add(spec.Points[1]);
                m.LineEnd = kind == AnnotationKind.Arrow ? "OpenArrow" : null;
                m.Rect = m.GeometryBounds(AppearanceBuilder.ArrowSize(m.LineWidth) + m.LineWidth);
                break;
            case AnnotationKind.Polygon or AnnotationKind.Polyline:
                if (spec.Points is not { Count: >= 2 }) throw new ArgumentException("Polygon requires points.", nameof(spec));
                m.Points.AddRange(spec.Points);
                m.Rect = m.GeometryBounds(m.LineWidth + 1);
                break;
            case AnnotationKind.Ink:
                if (spec.InkStrokes is not { Count: > 0 }) throw new ArgumentException("Ink requires strokes.", nameof(spec));
                foreach (var s in spec.InkStrokes.Where(s => s.Count > 0)) m.Ink.Add(s.ToList());
                m.Rect = m.GeometryBounds(m.LineWidth + 1);
                break;
            case AnnotationKind.Note:
                m.Rect = spec.Rect.IsEmpty ? new PdfRect(spec.Rect.Left, spec.Rect.Top - NoteSize, spec.Rect.Left + NoteSize, spec.Rect.Top) : spec.Rect;
                m.Icon ??= "Comment";
                break;
            case AnnotationKind.FreeText or AnnotationKind.Typewriter or AnnotationKind.Callout:
                ConfigureFreeText(doc, d, priv, m, spec);
                break;
            case AnnotationKind.Stamp:
                ConfigureStamp(doc, priv, m, spec, texts, author, now);
                break;
            case AnnotationKind.FileAttachment:
                ConfigureAttachment(doc, d, m, spec);
                break;
            default:
                throw new NotSupportedException("Annotation kind not supported: " + kind);
        }

        if (m.Rect.Width < 1 || m.Rect.Height < 1)
            m.Rect = new PdfRect(m.Rect.Left, m.Rect.Bottom, m.Rect.Left + Math.Max(1, m.Rect.Width), m.Rect.Bottom + Math.Max(1, m.Rect.Height));
        WriteStyle(doc, d, m, texts);
        if (priv.Elements.Count > 0) d.Elements[AnnotModel.PrivateKey] = priv;
        m.WriteGeometry(doc, d);
        if (m.IsImageStamp && spec.ImageData is { } img)
        {
            var form = AppearanceBuilder.Image(doc, img, m.Rect.Width, m.Rect.Height, m.Opacity);
            var ap = NewDict(doc);
            ap.Elements["/N"] = form.Reference!;
            d.Elements["/AP"] = ap;
        }
        else AppearanceBuilder.Apply(doc, d, AnnotModel.Read(d));

        annots.Elements.Add(d.Reference!);
        if (kind == AnnotationKind.Note) AnnotationStore.AddPopup(doc, page, d, annots);
        return d;
    }

    /// <summary>Kolory, krycie, grubość, ikona, końcówki linii, temat.</summary>
    private static void WriteStyle(PdfDocument doc, PdfDictionary d, AnnotModel m, IAnnotationTexts texts)
    {
        d.Elements["/Subj"] = Str(texts.KindName(m.Kind));
        if (!string.IsNullOrEmpty(m.Contents)) d.Elements["/Contents"] = Str(m.Contents);
        if (m.Subtype != "/FreeText")
        {
            if (m.Color is { } c) d.Elements["/C"] = ColorArray(doc, c);
            if (m.Fill is { } f && m.Subtype is "/Square" or "/Circle" or "/Polygon" or "/Line") d.Elements["/IC"] = ColorArray(doc, f);
        }
        d.Elements["/CA"] = Real(m.Opacity);
        if (m.Subtype is "/Square" or "/Circle" or "/Line" or "/Polygon" or "/PolyLine" or "/Ink" or "/FreeText")
        {
            var bs = NewDict(doc, "/Border");
            bs.Elements["/W"] = Real(m.Subtype == "/FreeText" && !m.HasBorder ? 0 : m.LineWidth);
            bs.Elements["/S"] = Name("/S");
            d.Elements["/BS"] = bs;
        }
        if (m.Subtype == "/Line")
        {
            var le = new PdfArray(doc);
            le.Elements.Add(Name("/None"));
            le.Elements.Add(Name(m.LineEnd is null ? "/None" : "/" + m.LineEnd));
            d.Elements["/LE"] = le;
        }
        if (m.Icon is not null && m.Subtype is "/Text" or "/Stamp" or "/FileAttachment") d.Elements["/Name"] = Name("/" + m.Icon);
    }

    private static void ConfigureFreeText(PdfDocument doc, PdfDictionary d, PdfDictionary priv, AnnotModel m, AnnotationSpec spec)
    {
        var style = spec.TextStyle ?? new TextStyle { Color = spec.Color };
        m.FontFamily = style.FontFamily;
        m.FontSize = style.FontSize > 0 ? style.FontSize : 12;
        m.TextColor = style.Color;
        m.HasBorder = spec.Kind != AnnotationKind.Typewriter && spec.LineWidth > 0;
        if (spec.Kind == AnnotationKind.Typewriter) m.Fill = null;
        double lw = m.HasBorder ? m.LineWidth : 0;
        var box = spec.Rect;
        if (box.Width <= 1 || box.Height <= 1)
        {
            var (w, h) = AppearanceBuilder.MeasureFreeText(m.Contents, m.FontFamily, m.FontSize, lw, box.Width > 1 ? box.Width : 300);
            box = new PdfRect(box.Left, box.Top - h, box.Left + Math.Max(box.Width, w), box.Top);
        }
        if (spec.Kind == AnnotationKind.Callout)
        {
            m.TextRect = box;
            var pts = spec.Points is { Count: >= 2 } p ? p.ToList() : new List<PdfPoint> { new(box.Left - 60, box.Bottom - 40), new(box.Left, (box.Bottom + box.Top) / 2) };
            // ostatni punkt dociągamy do krawędzi pola
            m.Points.AddRange(pts);
            m.Rect = m.GeometryBounds(AppearanceBuilder.ArrowSize(Math.Max(1, lw)) + 1);
            d.Elements["/IT"] = Name("/FreeTextCallout");
            var le = new PdfArray(doc);
            le.Elements.Add(Name("/OpenArrow"));
            d.Elements["/LE"] = le;
        }
        else
        {
            m.Rect = box;
            if (spec.Kind == AnnotationKind.Typewriter) d.Elements["/IT"] = Name("/FreeTextTypeWriter");
        }
        d.Elements["/DA"] = Str(AnnotModel.BuildDa(m.FontSize, m.TextColor));
        d.Elements["/DS"] = Str($"font: {Num(m.FontSize)}pt {m.FontFamily}; font-family: {m.FontFamily}; color:{Hex(m.TextColor)}");
        d.Elements["/Q"] = new PdfInteger(0);
        if (m.Fill is { } f) d.Elements["/C"] = ColorArray(doc, f);
        if (m.Color is { } bc) priv.Elements["/Border"] = ColorArray(doc, bc);
    }

    private static void ConfigureStamp(PdfDocument doc, PdfDictionary priv, AnnotModel m, AnnotationSpec spec, IAnnotationTexts texts, string author, DateTimeOffset now)
    {
        var at = new PdfPoint(spec.Rect.Left, spec.Rect.Top);
        if (spec.ImageData is { Length: > 0 } img)
        {
            m.IsImageStamp = true;
            m.Icon = "ZetImage";
            priv.Elements["/Image"] = new PdfBoolean(true);
            if (!spec.Rect.IsEmpty) m.Rect = spec.Rect;
            else
            {
                var (pw, ph) = AppearanceBuilder.ImageSize(img);
                double w = 150, h = pw > 0 ? w * ph / pw : 60;
                m.Rect = new PdfRect(at.X, at.Y - h, at.X + w, at.Y);
            }
            return;
        }
        if (StampLibrary.IsFillSignMark(spec.StampName))
        {
            m.StampName = spec.StampName;
            m.Icon = spec.StampName;
            priv.Elements["/Stamp"] = Name("/" + spec.StampName);
            m.Rect = spec.Rect.IsEmpty ? new PdfRect(at.X, at.Y - 14, at.X + 14, at.Y) : spec.Rect;
            return;
        }
        var def = StampLibrary.Find(spec.StampName);
        string main = def is not null ? texts.StampText(def) : (spec.StampName ?? spec.Contents);
        var lines = new List<string> { main.ToUpper(System.Globalization.CultureInfo.CurrentCulture), $"{author}, {texts.FormatStampDate(now)}" };
        m.StampName = def?.Id ?? spec.StampName ?? "Custom";
        m.Icon = m.StampName;
        if (def is not null && spec.Color == PdfColor.Yellow) m.Color = def.Color; // domyślny kolor wzoru, gdy użytkownik nie wybrał
        priv.Elements["/Stamp"] = Name("/" + m.StampName);
        var arr = new PdfArray(doc);
        foreach (var l in lines) arr.Elements.Add(Str(l));
        priv.Elements["/Lines"] = arr;
        if (!spec.Rect.IsEmpty) m.Rect = spec.Rect;
        else
        {
            var (w, h) = AppearanceBuilder.MeasureStamp(lines);
            m.Rect = new PdfRect(at.X, at.Y - h, at.X + w, at.Y);
        }
        if (string.IsNullOrEmpty(m.Contents)) m.Contents = main;
    }

    private static void ConfigureAttachment(PdfDocument doc, PdfDictionary d, AnnotModel m, AnnotationSpec spec)
    {
        if (string.IsNullOrEmpty(spec.AttachmentPath) || !File.Exists(spec.AttachmentPath)) throw new FileNotFoundException("Attachment not found.", spec.AttachmentPath);
        var at = new PdfPoint(spec.Rect.Left, spec.Rect.Top);
        m.Rect = spec.Rect.IsEmpty ? new PdfRect(at.X, at.Y - 20, at.X + 14, at.Y) : spec.Rect;
        m.Icon = "PushPin";
        d.Elements["/FS"] = EmbeddedFiles.CreateFileSpec(doc, spec.AttachmentPath!, Path.GetFileName(spec.AttachmentPath!), spec.Contents).Reference!;
        if (string.IsNullOrEmpty(m.Contents)) m.Contents = Path.GetFileName(spec.AttachmentPath!);
    }
}

using ZetPDF.Core.Contracts;

namespace ZetPDF.Annotations.UI;

/// <summary>Styl narzędzia komentarza (kolor treści wybrany przez użytkownika, krycie, grubość, czcionka).</summary>
public sealed class ToolStyle
{
    public byte R { get; set; } = 220;
    public byte G { get; set; } = 30;
    public byte B { get; set; } = 30;
    public bool HasFill { get; set; }
    public byte FillR { get; set; } = 255;
    public byte FillG { get; set; } = 255;
    public byte FillB { get; set; } = 255;
    public double Opacity { get; set; } = 1;
    public double LineWidth { get; set; } = 2;
    public double FontSize { get; set; } = 12;
    public string FontFamily { get; set; } = "Arial";

    public PdfColor Color { get => new(255, R, G, B); set { R = value.R; G = value.G; B = value.B; } }
    public PdfColor? Fill { get => HasFill ? new PdfColor(255, FillR, FillG, FillB) : null; set { HasFill = value is not null; if (value is { } v) { FillR = v.R; FillG = v.G; FillB = v.B; } } }

    public ToolStyle Clone() => (ToolStyle)MemberwiseClone();
}

/// <summary>Ustawienia modułu (settings.json, sekcja "Annotations").</summary>
public sealed class AnnotationSettings
{
    public const string Key = "Annotations";

    /// <summary>Styl per narzędzie (klucz = id polecenia).</summary>
    public Dictionary<string, ToolStyle> Styles { get; set; } = new();
    public bool HighlightFields { get; set; } = true;
    public string? LastSignature { get; set; }

    public ToolStyle StyleFor(string toolId)
    {
        if (Styles.TryGetValue(toolId, out var s)) return s;
        s = DefaultFor(toolId);
        Styles[toolId] = s;
        return s;
    }

    public static ToolStyle DefaultFor(string toolId)
    {
        var s = new ToolStyle();
        switch (toolId)
        {
            case "Comment.Highlight" or "Comment.AreaHighlight":
                s.Color = PdfColor.Yellow; s.Opacity = 0.5; break;
            case "Comment.Underline" or "Comment.Squiggly":
                s.Color = new PdfColor(255, 30, 110, 220); break;
            case "Comment.StrikeOut":
                s.Color = PdfColor.Red; break;
            case "Comment.Note":
                s.Color = PdfColor.Yellow; break;
            case "Comment.Typewriter" or "FillSign.Text" or "FillSign.Check" or "FillSign.Cross" or "FillSign.Dot" or "FillSign.Line":
                s.Color = PdfColor.Black; s.LineWidth = 1.5; break;
            case "Comment.TextBox" or "Comment.Callout":
                s.Color = PdfColor.Red; s.LineWidth = 1; s.Fill = PdfColor.White; break;
            case "Comment.Pencil":
                s.Color = new PdfColor(255, 30, 110, 220); break;
        }
        return s;
    }
}

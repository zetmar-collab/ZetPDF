namespace ZetPDF.Core.Theming;

/// <summary>
/// Klucze zasobów motywu (pędzle i czcionki). Słowniki Light/Dark w ZetPDF.App\Themes (ui-shell) definiują
/// KAŻDY z tych kluczy. W XAML: Background="{DynamicResource Brush.Window.Background}".
/// W kodzie: element.SetResourceReference(Control.BackgroundProperty, ThemeKeys.WindowBackground).
/// Nowe klucze – przez koordynatora (zmiana kontraktu).
/// </summary>
public static class ThemeKeys
{
    // Okno i powierzchnie
    public const string WindowBackground = "Brush.Window.Background";
    public const string Surface = "Brush.Surface";                 // panele, paski
    public const string SurfaceAlt = "Brush.Surface.Alt";          // naprzemienne wiersze, nagłówki sekcji
    public const string SurfaceRaised = "Brush.Surface.Raised";    // okna dialogowe, karty, menu

    // Tekst
    public const string TextPrimary = "Brush.Text.Primary";
    public const string TextSecondary = "Brush.Text.Secondary";
    public const string TextDisabled = "Brush.Text.Disabled";
    public const string TextOnAccent = "Brush.Text.OnAccent";
    public const string TextLink = "Brush.Text.Link";

    // Obramowania i fokus
    public const string Border = "Brush.Border";
    public const string BorderStrong = "Brush.Border.Strong";
    public const string Focus = "Brush.Focus";

    // Akcent
    public const string Accent = "Brush.Accent";
    public const string AccentHover = "Brush.Accent.Hover";
    public const string AccentPressed = "Brush.Accent.Pressed";

    // Kontrolki
    public const string ControlBackground = "Brush.Control.Background";
    public const string ControlBackgroundHover = "Brush.Control.Background.Hover";
    public const string ControlBackgroundPressed = "Brush.Control.Background.Pressed";
    public const string ControlBorder = "Brush.Control.Border";
    public const string InputBackground = "Brush.Input.Background";
    public const string InputBorder = "Brush.Input.Border";
    public const string SelectionBackground = "Brush.Selection.Background";
    public const string SelectionText = "Brush.Selection.Text";
    public const string ScrollbarThumb = "Brush.Scrollbar.Thumb";
    public const string ScrollbarTrack = "Brush.Scrollbar.Track";
    public const string TooltipBackground = "Brush.Tooltip.Background";
    public const string TooltipText = "Brush.Tooltip.Text";
    public const string StatusBarBackground = "Brush.StatusBar.Background";
    public const string StatusBarText = "Brush.StatusBar.Text";

    // Widok dokumentu
    public const string ViewerBackground = "Brush.Viewer.Background";      // obszar za stronami
    public const string ViewerPageBorder = "Brush.Viewer.PageBorder";
    public const string ViewerPageShadow = "Brush.Viewer.PageShadow";
    public const string ViewerSearchHit = "Brush.Viewer.SearchHit";        // półprzezroczysty
    public const string ViewerSearchHitCurrent = "Brush.Viewer.SearchHit.Current";
    public const string ViewerTextSelection = "Brush.Viewer.TextSelection"; // półprzezroczysty
    public const string ViewerOverlayOutline = "Brush.Viewer.Overlay.Outline";
    public const string ViewerOverlayHandle = "Brush.Viewer.Overlay.Handle";
    public const string ViewerFormFieldHighlight = "Brush.Viewer.FormField.Highlight";
    public const string ViewerRedactionMark = "Brush.Viewer.RedactionMark";

    // Stany
    public const string StatusError = "Brush.Status.Error";
    public const string StatusWarning = "Brush.Status.Warning";
    public const string StatusSuccess = "Brush.Status.Success";
    public const string StatusInfo = "Brush.Status.Info";

    // Porównanie
    public const string DiffInserted = "Brush.Diff.Inserted";
    public const string DiffDeleted = "Brush.Diff.Deleted";
    public const string DiffChanged = "Brush.Diff.Changed";

    // Czcionki (FontFamily)
    public const string FontUi = "Font.Family.Ui";        // Segoe UI
    public const string FontIcons = "Font.Family.Icons";  // Segoe Fluent Icons, Segoe MDL2 Assets
    public const string FontMono = "Font.Family.Mono";    // Cascadia Mono, Consolas

    /// <summary>Wszystkie klucze pędzli (test słowników motywu).</summary>
    public static IReadOnlyList<string> AllBrushes { get; } = typeof(ThemeKeys).GetFields()
        .Where(f => f.IsLiteral && ((string)f.GetValue(null)!).StartsWith("Brush.", StringComparison.Ordinal))
        .Select(f => (string)f.GetValue(null)!).ToArray();

    public static IReadOnlyList<string> AllFonts { get; } = new[] { FontUi, FontIcons, FontMono };
}

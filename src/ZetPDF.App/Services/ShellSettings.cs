namespace ZetPDF.App.Services;

/// <summary>
/// Ustawienia powłoki spoza AppSettings (sekcja "Shell" w settings.json, ISettingsService.GetModuleSettings).
/// </summary>
public sealed class ShellSettings
{
    public const string Key = "Shell";

    /// <summary>Szerokość panelu bocznego (DIP).</summary>
    public double SidePanelWidth { get; set; } = 260;
}

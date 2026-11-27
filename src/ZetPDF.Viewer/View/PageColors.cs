using System.Windows.Media;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Viewer.View;

/// <summary>Kolory strony w widoku: zwykłe, tryb nocny (jasny tekst na ciemnym tle) albo sepia. Plik się nie zmienia.</summary>
public enum PageColorMode { Normal, Night, Sepia }

/// <summary>Ustawienie zapisywane w ustawieniach modułu (klucz „ViewerPageColors”).</summary>
public sealed class PageColorSettings
{
    public PageColorMode Mode { get; set; }
}

/// <summary>
/// Tryb kolorów strony wspólny dla wszystkich widoków. Dotyczy tylko podglądu na ekranie: miniatury, druk, migawka,
/// eksport i zapis mają zawsze oryginalne kolory.
/// </summary>
public sealed class PageColorService
{
    private const string SettingsKey = "ViewerPageColors";
    private readonly ISettingsService? _settings;
    private PageColorMode _mode;

    public PageColorService(ISettingsService? settings = null)
    {
        _settings = settings;
        var stored = settings?.GetModuleSettings<PageColorSettings>(SettingsKey).Mode ?? PageColorMode.Normal;
        _mode = Enum.IsDefined(stored) ? stored : PageColorMode.Normal;
    }

    public PageColorMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value) || value == _mode) return;
            _mode = value;
            _settings?.SetModuleSettings(SettingsKey, new PageColorSettings { Mode = value });
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    /// <summary>Przełącznik: włączenie trybu albo powrót do zwykłych kolorów, gdy był już włączony.</summary>
    public void Toggle(PageColorMode mode) => Mode = Mode == mode ? PageColorMode.Normal : mode;
}

/// <summary>Przeliczanie pikseli strony (BGRA, premnożone przez alfę; strony PDFium są nieprzezroczyste).</summary>
public static class PageColorFilter
{
    // Tło nocne nie jest czysto czarne (mniejszy kontrast męczy oczy mniej), tekst nie jest czysto biały.
    private const byte NightPaper = 30, NightInk = 225;
    private static readonly (byte R, byte G, byte B) SepiaPaper = (244, 236, 216), SepiaInk = (60, 45, 30);

    public static Color PaperColor(PageColorMode mode) => mode switch
    {
        PageColorMode.Night => Color.FromRgb(NightPaper, NightPaper, NightPaper),
        PageColorMode.Sepia => Color.FromRgb(SepiaPaper.R, SepiaPaper.G, SepiaPaper.B),
        _ => Colors.White,
    };

    /// <summary>Nowa bitmapa z przeliczonymi kolorami (wejście bez zmian). Dla trybu zwykłego zwraca wejście.</summary>
    public static RenderedBitmap Apply(RenderedBitmap bmp, PageColorMode mode)
    {
        if (mode == PageColorMode.Normal) return bmp;
        var src = bmp.Bgra;
        var dst = new byte[src.Length];
        // Tablice dla jasności 0..255: tryb nocny odwraca jasność, zachowując odcień (kolorowe wyróżnienia zostają kolorowe),
        // sepia miesza papier i tusz według jasności.
        for (int y = 0; y < bmp.Height; y++)
        {
            int row = y * bmp.Stride;
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = row + x * 4;
                if (i + 3 >= src.Length) break;
                byte b = src[i], g = src[i + 1], r = src[i + 2], a = src[i + 3];
                (r, g, b) = mode == PageColorMode.Night ? Night(r, g, b) : Sepia(r, g, b);
                dst[i] = b; dst[i + 1] = g; dst[i + 2] = r; dst[i + 3] = a;
            }
        }
        return bmp with { Bgra = dst };
    }

    private static int Luma(byte r, byte g, byte b) => (r * 299 + g * 587 + b * 114) / 1000;

    private static (byte, byte, byte) Night(byte r, byte g, byte b)
    {
        int l = Luma(r, g, b);
        int target = NightPaper + (255 - l) * (NightInk - NightPaper) / 255; // jasność po odwróceniu
        int shift = target - l;
        return (Clamp(r + shift), Clamp(g + shift), Clamp(b + shift));
    }

    private static (byte, byte, byte) Sepia(byte r, byte g, byte b)
    {
        int l = Luma(r, g, b);
        byte Mix(byte ink, byte paper) => (byte)((ink * (255 - l) + paper * l) / 255);
        return (Mix(SepiaInk.R, SepiaPaper.R), Mix(SepiaInk.G, SepiaPaper.G), Mix(SepiaInk.B, SepiaPaper.B));
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}

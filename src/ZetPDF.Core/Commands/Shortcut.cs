using System.Text;
using System.Windows.Input;

namespace ZetPDF.Core.Commands;

/// <summary>
/// Skrót klawiszowy z katalogu ("Ctrl+Shift+I", "Shift+H", "F3", "Ctrl+D0"). UWAGA (zweryfikowane testem):
/// WPF KeyGesture/KeyBinding NIE przyjmuje skrótów bez Ctrl/Alt dla klawiszy znakowych (np. Shift+H, H) –
/// rzuca NotSupportedException. Takie skróty (<see cref="RequiresNonTextFocus"/>) powłoka obsługuje sama
/// w PreviewKeyDown i tylko wtedy, gdy fokus NIE jest w polu tekstowym.
/// </summary>
public readonly record struct Shortcut(Key Key, ModifierKeys Modifiers)
{
    private static readonly Dictionary<string, ModifierKeys> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = ModifierKeys.Control,
        ["Control"] = ModifierKeys.Control,
        ["Shift"] = ModifierKeys.Shift,
        ["Alt"] = ModifierKeys.Alt,
        ["Win"] = ModifierKeys.Windows,
    };

    public static bool TryParse(string text, out Shortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var mods = ModifierKeys.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!ModifierNames.TryGetValue(parts[i], out var m)) return false;
            mods |= m;
        }
        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key) || key == Key.None) return false;
        shortcut = new Shortcut(key, mods);
        return true;
    }

    public static Shortcut Parse(string text) =>
        TryParse(text, out var s) ? s : throw new FormatException($"Invalid shortcut '{text}'.");

    /// <summary>Skrót koliduje z wpisywaniem tekstu (brak Ctrl/Alt/Win i nie jest klawiszem funkcyjnym/nawigacyjnym).</summary>
    public bool RequiresNonTextFocus =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0
        && !(Key >= Key.F1 && Key <= Key.F24)
        && Key is not (Key.Escape or Key.Delete);

    /// <summary>Czy można użyć WPF KeyBinding (w przeciwnym razie obsługa ręczna w PreviewKeyDown).</summary>
    public bool CanUseKeyBinding => !RequiresNonTextFocus;

    public bool Matches(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == Key && Keyboard.Modifiers == Modifiers;
    }

    /// <summary>Tekst do podpowiedzi ("Ctrl+Shift+I", "Ctrl+0").</summary>
    public string ToDisplayString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(Key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (Key - Key.D0))).ToString(),
            Key.OemPlus or Key.Add => "+",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            _ => Key.ToString(),
        });
        return sb.ToString();
    }
}

using System.Windows.Input;
using ZetPDF.Core.Commands;

namespace ZetPDF.App.Shell;

/// <summary>
/// Centralna mapa skrótów klawiszowych: zbiera skróty ze wszystkich deskryptorów (katalog + polecenia dodatkowe
/// modułów), wykrywa konflikty (ten sam skrót dla dwóch różnych poleceń – wygrywa pierwszy w kolejności katalogu,
/// konflikt trafia do dziennika) i rozdziela na:
///  - skróty z Ctrl/Alt/klawiszami funkcyjnymi → WPF KeyBinding,
///  - skróty bez Ctrl/Alt (Shift+H…) → obsługa ręczna poza polami tekstowymi,
///  - klawisze jednoliterowe (SingleKey) → tylko przy włączonej opcji w Ustawieniach i poza polami tekstowymi.
/// </summary>
public sealed class ShortcutMap
{
    public sealed record Binding(Shortcut Shortcut, string CommandId);

    public sealed record Conflict(Shortcut Shortcut, string KeptCommandId, string IgnoredCommandId);

    private ShortcutMap(IReadOnlyList<Binding> keyBindings, IReadOnlyList<Binding> manual, IReadOnlyList<Binding> singleKeys, IReadOnlyList<Conflict> conflicts, IReadOnlyList<string> invalid)
    {
        KeyBindings = keyBindings;
        Manual = manual;
        SingleKeys = singleKeys;
        Conflicts = conflicts;
        Invalid = invalid;
    }

    public IReadOnlyList<Binding> KeyBindings { get; }
    public IReadOnlyList<Binding> Manual { get; }
    public IReadOnlyList<Binding> SingleKeys { get; }
    public IReadOnlyList<Conflict> Conflicts { get; }

    /// <summary>Teksty skrótów, których nie udało się sparsować ("id: tekst").</summary>
    public IReadOnlyList<string> Invalid { get; }

    public static ShortcutMap Build(IEnumerable<CommandDescriptor> descriptors)
    {
        var owner = new Dictionary<Shortcut, string>();
        var keyBindings = new List<Binding>();
        var manual = new List<Binding>();
        var single = new List<Binding>();
        var conflicts = new List<Conflict>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in descriptors)
        {
            if (!seen.Add(d.Id)) continue; // to samo polecenie w kilku grupach wstążki
            foreach (var text in d.Shortcuts)
            {
                if (!Shortcut.TryParse(text, out var s))
                {
                    invalid.Add($"{d.Id}: {text}");
                    continue;
                }
                if (!TryClaim(s, d.Id)) continue;
                (s.CanUseKeyBinding ? keyBindings : manual).Add(new Binding(s, d.Id));
            }
            if (!string.IsNullOrWhiteSpace(d.SingleKey))
            {
                if (Enum.TryParse<Key>(d.SingleKey.Trim(), ignoreCase: true, out var k) && k != Key.None)
                {
                    var s = new Shortcut(k, ModifierKeys.None);
                    if (TryClaim(s, d.Id)) single.Add(new Binding(s, d.Id));
                }
                else invalid.Add($"{d.Id}: {d.SingleKey}");
            }
        }
        return new ShortcutMap(keyBindings, manual, single, conflicts, invalid);

        bool TryClaim(Shortcut s, string id)
        {
            if (owner.TryGetValue(s, out var existing))
            {
                if (!string.Equals(existing, id, StringComparison.Ordinal)) conflicts.Add(new Conflict(s, existing, id));
                return false;
            }
            owner[s] = id;
            return true;
        }
    }

    /// <summary>Tekst skrótów polecenia do podpowiedzi/listy (np. "Ctrl+W, Ctrl+F4").</summary>
    public static string Describe(CommandDescriptor d, bool includeSingleKey)
    {
        var parts = new List<string>();
        foreach (var t in d.Shortcuts)
            parts.Add(Shortcut.TryParse(t, out var s) ? s.ToDisplayString() : t);
        if (includeSingleKey && !string.IsNullOrWhiteSpace(d.SingleKey)) parts.Add(d.SingleKey.Trim().ToUpperInvariant());
        return string.Join(", ", parts);
    }
}

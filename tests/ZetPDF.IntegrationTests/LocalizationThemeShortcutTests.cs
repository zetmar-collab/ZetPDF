using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml.Linq;
using ZetPDF.App.Shell;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Lokalizacja (pliki .resx pl/en wszystkich modułów, klucze użyte w kodzie, teksty na sztywno w XAML i C#),
/// kolory na sztywno poza słownikami motywów, skróty klawiszowe po rejestracji wszystkich modułów.
/// Skanowane są pliki źródłowe w src\ (bez bin/obj).
/// </summary>
public class LocalizationThemeShortcutTests
{
    private static string Src => SkeletonIntegrationTests.FindRepoFile("src");

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Src, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Rel(string f) => Path.GetRelativePath(Src, f);

    private static Dictionary<string, string> Resx(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .Where(e => e.Attribute("type") is null) // tylko teksty
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty);

    private static IEnumerable<(string En, string Pl)> ResxPairs()
    {
        foreach (var en in SourceFiles("Strings.resx"))
        {
            var pl = Path.Combine(Path.GetDirectoryName(en)!, "Strings.pl.resx");
            yield return (en, pl);
        }
    }

    // ================================================================== .resx

    [Fact]
    public void Every_resx_on_disk_has_polish_twin_with_same_keys_non_empty_values_and_matching_placeholders()
    {
        var errors = new List<string>();
        var allKeys = new Dictionary<string, string>();
        int files = 0;
        foreach (var (enFile, plFile) in ResxPairs())
        {
            files++;
            if (!File.Exists(plFile)) { errors.Add($"{Rel(enFile)}: brak Strings.pl.resx"); continue; }
            var en = Resx(enFile);
            var pl = Resx(plFile);
            foreach (var k in en.Keys.Except(pl.Keys)) errors.Add($"{Rel(plFile)}: brak klucza {k}");
            foreach (var k in pl.Keys.Except(en.Keys)) errors.Add($"{Rel(enFile)}: brak klucza {k}");
            foreach (var (k, v) in en) if (string.IsNullOrWhiteSpace(v)) errors.Add($"{Rel(enFile)}: pusty {k}");
            foreach (var (k, v) in pl) if (string.IsNullOrWhiteSpace(v)) errors.Add($"{Rel(plFile)}: pusty {k}");
            foreach (var k in en.Keys.Intersect(pl.Keys))
            {
                var a = Placeholders(en[k]);
                var b = Placeholders(pl[k]);
                if (!a.SetEquals(b)) errors.Add($"{k}: symbole formatowania en {{{string.Join(",", a)}}} ≠ pl {{{string.Join(",", b)}}}");
            }
            foreach (var k in en.Keys)
            {
                if (allKeys.TryGetValue(k, out var other)) errors.Add($"klucz {k} w {Rel(enFile)} i {other}");
                else allKeys[k] = Rel(enFile);
            }
        }
        Assert.True(files >= 9, $"znaleziono tylko {files} plików Strings.resx");
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static HashSet<string> Placeholders(string s) =>
        Regex.Matches(s.Replace("{{", "").Replace("}}", ""), @"\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void Polish_texts_are_translated_not_copied_from_english()
    {
        // Tekst pl identyczny z en, zawierający co najmniej dwa słowa po 3+ litery, to zwykle brak tłumaczenia.
        var suspicious = new List<string>();
        var word = new Regex(@"\p{L}{3,}");
        foreach (var (enFile, plFile) in ResxPairs())
        {
            var en = Resx(enFile);
            var pl = Resx(plFile);
            foreach (var (k, v) in en)
                if (pl.TryGetValue(k, out var p) && p == v && word.Matches(v).Count >= 2) suspicious.Add($"{k} = \"{v}\"");
        }
        Assert.True(suspicious.Count == 0, "Nieprzetłumaczone teksty pl:\n" + string.Join("\n", suspicious));
    }

    [Fact]
    public void Localization_keys_used_in_code_and_xaml_exist_in_resources()
    {
        var keys = ResxPairs().SelectMany(p => Resx(p.En).Keys).ToHashSet();
        var commandIds = RibbonCatalog.Commands.Select(c => c.Id).ToHashSet();
        var prefixes = "Core|Shell|Cmd|Ribbon|Render|Viewer|Pages|Annot|Edit|Ocr|Security";
        var literal = new Regex($"\"((?:{prefixes})\\.[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)*)\"");
        var xamlLoc = new Regex(@"\{loc:Loc\s+([A-Za-z0-9_.]+)");
        var missing = new List<string>();
        // nazwy stylów/zasobów XAML (x:Key) – to nie są klucze tekstów
        XNamespace xns = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resourceKeys = SourceFiles("*.xaml").SelectMany(f => XDocument.Load(f).Descendants().Select(e => (string?)e.Attribute(xns + "Key"))).OfType<string>().ToHashSet();
        foreach (var f in SourceFiles("*.cs"))
        {
            if (f.EndsWith(".g.cs", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("AutomationId", StringComparison.Ordinal)) continue;
                foreach (Match m in literal.Matches(line))
                {
                    var k = m.Groups[1].Value;
                    if (keys.Contains(k) || commandIds.Contains(k) || resourceKeys.Contains(k)) continue;
                    if (line.Contains($"new CommandDescriptor(\"{k}\"", StringComparison.Ordinal)) continue; // id polecenia dodatkowego
                    if (keys.Any(x => x.StartsWith(k + ".", StringComparison.Ordinal))) continue; // prefiks klucza złożonego w kodzie
                    if (k.StartsWith("Cmd.", StringComparison.Ordinal) && commandIds.Contains(k[4..])) continue;
                    missing.Add($"{Rel(f)}:{i + 1}: {k}");
                }
            }
        }
        foreach (var f in SourceFiles("*.xaml"))
            foreach (Match m in xamlLoc.Matches(File.ReadAllText(f)))
                if (!keys.Contains(m.Groups[1].Value)) missing.Add($"{Rel(f)}: {m.Groups[1].Value}");
        Assert.True(missing.Count == 0, "Klucze bez tekstu w .resx (pokazałyby się jako [Klucz]):\n" + string.Join("\n", missing.Distinct()));
    }

    [Fact]
    public void All_registered_keys_resolve_in_both_languages_after_modules_initialize() => Sta.Run(() =>
    {
        using var host = new TestHost(initializeModules: true);
        var loc = host.Get<ILocalizationService>();
        // teksty poleceń (Cmd.*) i kart rejestruje powłoka przy starcie (App) – tu ręcznie, jak App.OnStartup
        loc.RegisterResources(new System.Resources.ResourceManager("ZetPDF.App.Resources.Strings", typeof(ZetPDF.App.ModuleCatalog).Assembly));
        var registry = host.Get<ICommandRegistry>();
        var errors = new List<string>();
        foreach (var culture in new[] { new CultureInfo("pl"), new CultureInfo("en") })
        {
            loc.SetCulture(culture);
            foreach (var k in loc.AllKeys)
            {
                var v = loc[k];
                if (string.IsNullOrWhiteSpace(v) || v == $"[{k}]") errors.Add($"{culture}: {k}");
            }
            // etykiety wszystkich poleceń (także dodatkowych z RegisterExtra) i pozycji menu dynamicznych
            foreach (var d in registry.Descriptors)
            {
                if (!loc.HasKey(d.TextKey)) errors.Add($"{culture}: brak etykiety polecenia {d.Id} ({d.TextKey})");
                foreach (var item in registry.GetMenuItems(d.Id))
                    if (item.Text is null && (item.TextKey is null || !loc.HasKey(item.TextKey))) errors.Add($"{culture}: menu {d.Id}/{item.Id} bez tekstu");
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors.Distinct()));
        return Task.CompletedTask;
    });

    // ================================================================== XAML: teksty i kolory

    private static readonly string[] TextAttributes = { "Content", "Text", "Header", "ToolTip", "Title", "Description", "InputGestureText" };
    private static readonly string[] ColorAttributes = { "Background", "Foreground", "Fill", "Stroke", "BorderBrush", "Color", "SelectionBrush", "CaretBrush", "OpacityMask" };
    private static readonly Regex Geometry = new(@"^[MLHVCSQTAZmlhvcsqtaz0-9\s,.\-]+$");

    private static HashSet<string> NamedColors() =>
        typeof(Colors).GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsThemeDictionary(string file) => Path.GetFileName(file) is "Light.xaml" or "Dark.xaml";

    [Fact]
    public void Xaml_has_no_hard_coded_texts()
    {
        var bad = new List<string>();
        foreach (var f in SourceFiles("*.xaml"))
        {
            var doc = XDocument.Load(f, LoadOptions.SetLineInfo);
            foreach (var e in doc.Descendants())
            {
                foreach (var a in e.Attributes())
                {
                    if (!TextAttributes.Contains(a.Name.LocalName)) continue;
                    var v = a.Value;
                    if (v.StartsWith('{') || string.IsNullOrWhiteSpace(v) || Geometry.IsMatch(v)) continue;
                    if (a.Name.LocalName == "InputGestureText" && Regex.IsMatch(v, @"^(Ctrl\+|Shift\+|Alt\+)*(F\d+|Del|Delete|Enter|Esc|[A-Z0-9])$")) continue;
                    if (v.Any(char.IsLetter)) bad.Add($"{Rel(f)}:{((System.Xml.IXmlLineInfo)e).LineNumber}: <{e.Name.LocalName} {a.Name.LocalName}=\"{v}\">");
                }
                // treść elementu tekstowego, np. <TextBlock>Tekst</TextBlock>
                if (e.Name.LocalName is "TextBlock" or "Run" or "Label" or "Button" && !e.HasElements && e.Value.Any(char.IsLetter))
                    bad.Add($"{Rel(f)}:{((System.Xml.IXmlLineInfo)e).LineNumber}: <{e.Name.LocalName}>{e.Value}</{e.Name.LocalName}>");
            }
        }
        Assert.True(bad.Count == 0, "Teksty na sztywno w XAML:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Xaml_has_no_hard_coded_colors_outside_theme_dictionaries()
    {
        var named = NamedColors();
        var bad = new List<string>();
        foreach (var f in SourceFiles("*.xaml"))
        {
            if (IsThemeDictionary(f)) continue;
            var doc = XDocument.Load(f, LoadOptions.SetLineInfo);
            foreach (var e in doc.Descendants())
            {
                int line = ((System.Xml.IXmlLineInfo)e).LineNumber;
                foreach (var a in e.Attributes())
                {
                    if (!ColorAttributes.Contains(a.Name.LocalName)) continue;
                    var v = a.Value.Trim();
                    if (v.StartsWith('{')) continue;
                    if (v.Equals("Transparent", StringComparison.OrdinalIgnoreCase)) continue;
                    if (v.StartsWith('#') || named.Contains(v) || v.StartsWith("sc#", StringComparison.OrdinalIgnoreCase))
                        bad.Add($"{Rel(f)}:{line}: {e.Name.LocalName}.{a.Name.LocalName}=\"{v}\"");
                }
                if (e.Name.LocalName is "Color" or "SolidColorBrush" && !e.HasElements && !string.IsNullOrWhiteSpace(e.Value))
                    bad.Add($"{Rel(f)}:{line}: <{e.Name.LocalName}>{e.Value}");
            }
        }
        Assert.True(bad.Count == 0, "Kolory na sztywno w XAML poza Light.xaml/Dark.xaml:\n" + string.Join("\n", bad));
    }

    // ================================================================== C#: teksty i kolory w kodzie interfejsu

    /// <summary>
    /// Przejrzane wyjątki (plik, fragment) – kolory treści PDF wybrane przez użytkownika, czerń/biel samej strony PDF
    /// (papier), znaki niebędące tekstem. Nowe wystąpienia poza listą = błąd.
    /// </summary>
    private static readonly (string File, string Fragment, string Why)[] ReviewedCodeExceptions =
    {
        ("SignatureDialog.cs", "Color.FromRgb(0, 0, 0)", "kolor atramentu podpisu (treść PDF)"),
        ("SignatureDialog.cs", "new SolidColorBrush(InkColor)", "podgląd atramentu (treść PDF)"),
        ("SignatureDialog.cs", "new SolidColorBrush(color)", "render podpisu do PNG (treść PDF)"),
        ("Ui.cs", "new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(c.A", "kolor adnotacji (treść PDF)"),
        ("TextEditorPopup.cs", "new SolidColorBrush(Ui.ToColor(c))", "próbka koloru tekstu PDF"),
        ("PagesDialog.cs", "new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B))", "próbka koloru znaku wodnego/tła (treść PDF)"),
        ("SignatureAppearanceBuilder.cs", "Brushes.Black", "kontury glifów w wyglądzie podpisu (treść PDF)"),
        ("PrintDialogWindow.cs", "Brushes.White", "papier w podglądzie wydruku"),
        ("PagesPresenter.cs", "Brushes.White", "tło strony PDF przed renderem"),
        ("DocumentView.cs", "System.Windows.Media.Brushes.White", "papier strony PDF (zwykłe kolory)"),
        ("PageColors.cs", "Color.FromRgb(", "papier strony w trybie nocnym i sepii (podgląd treści PDF)"),
        ("PageColors.cs", "Colors.White", "papier strony PDF (zwykłe kolory)"),
        ("BookmarkColors.cs", "new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B))", "kolor zakładki zapisany w pliku (treść PDF)"),
        ("TextLayerWriter.cs", "XBrushes.Black", "PDFsharp – treść PDF"),
        ("PageOperations.Transform.cs", "XColors.Black", "PDFsharp – treść PDF"),
    };

    [Fact]
    public void Ui_code_has_no_hard_coded_colors_except_reviewed_pdf_content_colors()
    {
        var pattern = new Regex(@"(?<![A-Za-z])(Colors\.[A-Z][a-z]+|Color\.FromA?Rgb\(|Brushes\.(?!Transparent)[A-Z][a-z]+|new SolidColorBrush\((?!Colors\.Transparent))");
        var hex = new Regex("\"#[0-9A-Fa-f]{6,8}\"");
        var bad = new List<string>();
        foreach (var f in SourceFiles("*.cs"))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.TrimStart().StartsWith("//", StringComparison.Ordinal) || l.TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
                if (!pattern.IsMatch(l) && !hex.IsMatch(l)) continue;
                if (l.Contains("SystemColors.", StringComparison.Ordinal) && !pattern.IsMatch(l.Replace("SystemColors.", ""))) continue;
                if (ReviewedCodeExceptions.Any(x => Path.GetFileName(f) == x.File && l.Contains(x.Fragment, StringComparison.Ordinal))) continue;
                bad.Add($"{Rel(f)}:{i + 1}: {l.Trim()}");
            }
        }
        Assert.True(bad.Count == 0, "Kolory na sztywno w kodzie:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Ui_code_has_no_hard_coded_texts_in_properties_and_message_calls()
    {
        var prop = new Regex("\\b(Content|Text|Header|ToolTip|Title|Description|PlaceholderText)\\s*=\\s*\\$?\"([^\"]*)\"");
        var call = new Regex("\\b(ShowMessage|Confirm|AskYesNoCancel|SetStatus|RunWithProgressAsync|MessageBox\\.Show|OpenFiles|SaveFile|SelectFolder)\\(\\s*\\$?\"([^\"]*)\"");
        var allowed = new HashSet<string> { "×", "Aa", "_zetpdf", "%", "…", "-", "–", "/", ":" };
        var bad = new List<string>();
        foreach (var f in SourceFiles("*.cs"))
        {
            if (f.EndsWith(".g.cs", StringComparison.Ordinal)) continue;
            if (f.Contains($"{Path.DirectorySeparatorChar}ZetPDF.Core{Path.DirectorySeparatorChar}Theming", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in prop.Matches(l).Concat(call.Matches(l)))
                {
                    var v = m.Groups[2].Value;
                    if (m.Value.Contains("$\"", StringComparison.Ordinal)) v = Regex.Replace(v, @"\{[^}]*\}", ""); // interpolacja: liczy się tylko stały tekst
                    if (!v.Any(char.IsLetter) || allowed.Contains(v)) continue;
                    if (Regex.IsMatch(v, @"^(Core|Shell|Cmd|Ribbon|Render|Viewer|Pages|Annot|Edit|Ocr|Security)\.")) continue; // klucz
                    if (Regex.IsMatch(v, @"^/[A-Z]")) continue; // nazwa PDF
                    bad.Add($"{Rel(f)}:{i + 1}: {l.Trim()}");
                }
            }
        }
        Assert.True(bad.Count == 0, "Teksty na sztywno w kodzie interfejsu:\n" + string.Join("\n", bad));
    }

    // ================================================================== skróty klawiszowe

    [Fact]
    public void Shortcuts_of_all_registered_commands_have_no_conflicts_and_point_to_handled_commands() => Sta.Run(() =>
    {
        using var host = new TestHost(initializeModules: true);
        var registry = host.Get<ICommandRegistry>();
        var map = ShortcutMap.Build(registry.Descriptors);
        Assert.True(map.Conflicts.Count == 0, string.Join("\n", map.Conflicts.Select(c => $"{c.Shortcut.ToDisplayString()}: {c.KeptCommandId} vs {c.IgnoredCommandId}")));
        Assert.Empty(map.Invalid);
        // Skróty powłoki (ui-shell) obsługuje okno główne – tu nie jest uruchamiane.
        var shellOwned = RibbonCatalog.Commands.Where(c => c.Module == FeatureModuleId.Shell).Select(c => c.Id).ToHashSet();
        var dead = map.KeyBindings.Concat(map.Manual).Concat(map.SingleKeys)
            .Where(b => !shellOwned.Contains(b.CommandId) && registry.GetHandler(b.CommandId) is null)
            .Select(b => $"{b.Shortcut.ToDisplayString()} → {b.CommandId}").ToList();
        Assert.True(dead.Count == 0, "Skróty do poleceń bez obsługi:\n" + string.Join("\n", dead));
        // Standardowe skróty (sekcja 20 raportu) przypisane do właściwych poleceń
        var expected = new Dictionary<string, string>
        {
            ["Ctrl+O"] = CommandIds.File.Open, ["Ctrl+S"] = CommandIds.File.Save, ["Ctrl+P"] = CommandIds.File.Print, ["Ctrl+F"] = CommandIds.Nav.Find,
            ["Ctrl+Z"] = CommandIds.Edit.Undo, ["Ctrl+Y"] = CommandIds.Edit.Redo, ["Ctrl+W"] = CommandIds.File.Close, ["Ctrl+G"] = CommandIds.Nav.GoToPage,
            ["F11"] = CommandIds.View.FullScreen, ["Ctrl+C"] = CommandIds.Edit.Copy, ["Ctrl+A"] = CommandIds.Edit.SelectAll, ["Ctrl+B"] = CommandIds.Nav.AddBookmark,
        };
        var all = map.KeyBindings.Concat(map.Manual).ToDictionary(b => b.Shortcut, b => b.CommandId);
        foreach (var (text, id) in expected)
            Assert.True(all.TryGetValue(Shortcut.Parse(text), out var got) && got == id, $"{text}: oczekiwano {id}, jest {got}");
        return Task.CompletedTask;
    });
}

using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZetPDF.App;
using ZetPDF.App.Dialogs;
using ZetPDF.App.Licenses;
using ZetPDF.App.Services;
using ZetPDF.App.Shell;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Theming;

namespace ZetPDF.App.Tests;

/// <summary>Testy powłoki: motywy (klucze, kontrast, brak kolorów na sztywno), lokalizacja, skróty, wyszukiwanie poleceń, ustawienia.</summary>
public class ShellSmokeTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    internal static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ZetPDF.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "ZetPDF.App");
    }

    [Fact]
    public void Module_catalog_lists_all_modules() => Assert.Equal(7, ModuleCatalog.CreateAll().Count);

    // ------------------------------------------------------------------ motywy

    private static Dictionary<string, string> ThemeColors(string name)
    {
        var doc = XDocument.Load(Path.Combine(AppDir(), "Themes", name + ".xaml"));
        return doc.Root!.Elements(P + "SolidColorBrush").ToDictionary(e => (string)e.Attribute(X + "Key")!, e => (string)e.Attribute("Color")!);
    }

    private static double Luminance(string argb)
    {
        var hex = argb.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        double Ch(int i)
        {
            var c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(0) + 0.7152 * Ch(2) + 0.0722 * Ch(4);
    }

    internal static double Contrast(string a, string b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    public static IEnumerable<object[]> TextPairs()
    {
        var pairs = new (string Fg, string Bg)[]
        {
            (ThemeKeys.TextPrimary, ThemeKeys.WindowBackground), (ThemeKeys.TextPrimary, ThemeKeys.Surface), (ThemeKeys.TextPrimary, ThemeKeys.SurfaceAlt),
            (ThemeKeys.TextPrimary, ThemeKeys.SurfaceRaised), (ThemeKeys.TextPrimary, ThemeKeys.ControlBackground), (ThemeKeys.TextPrimary, ThemeKeys.ControlBackgroundHover),
            (ThemeKeys.TextPrimary, ThemeKeys.ControlBackgroundPressed), (ThemeKeys.TextPrimary, ThemeKeys.InputBackground),
            (ThemeKeys.TextSecondary, ThemeKeys.WindowBackground), (ThemeKeys.TextSecondary, ThemeKeys.Surface), (ThemeKeys.TextSecondary, ThemeKeys.SurfaceAlt),
            (ThemeKeys.TextSecondary, ThemeKeys.SurfaceRaised), (ThemeKeys.TextSecondary, ThemeKeys.InputBackground),
            (ThemeKeys.TextOnAccent, ThemeKeys.Accent), (ThemeKeys.TextOnAccent, ThemeKeys.AccentHover), (ThemeKeys.TextOnAccent, ThemeKeys.AccentPressed),
            (ThemeKeys.TextLink, ThemeKeys.SurfaceRaised), (ThemeKeys.TextLink, ThemeKeys.WindowBackground),
            (ThemeKeys.SelectionText, ThemeKeys.SelectionBackground), (ThemeKeys.TooltipText, ThemeKeys.TooltipBackground),
            (ThemeKeys.StatusBarText, ThemeKeys.StatusBarBackground),
            (ThemeKeys.StatusError, ThemeKeys.SurfaceRaised), (ThemeKeys.StatusWarning, ThemeKeys.SurfaceRaised), (ThemeKeys.StatusSuccess, ThemeKeys.SurfaceRaised), (ThemeKeys.StatusInfo, ThemeKeys.SurfaceRaised),
            (ThemeKeys.Accent, ThemeKeys.SurfaceRaised), (ThemeKeys.TextSecondary, ThemeKeys.ViewerBackground),
        };
        foreach (var theme in new[] { "Light", "Dark" })
            foreach (var (fg, bg) in pairs) yield return new object[] { theme, fg, bg };
    }

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Theme_text_contrast_is_at_least_4_5(string theme, string fg, string bg)
    {
        var c = ThemeColors(theme);
        var ratio = Contrast(c[fg], c[bg]);
        Assert.True(ratio >= 4.5, $"{theme}: {fg} on {bg} = {ratio:0.00}");
    }

    [Fact]
    public void Theme_dictionaries_define_all_keys_and_are_opaque_for_text()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var c = ThemeColors(theme);
            foreach (var k in ThemeKeys.AllBrushes) Assert.True(c.ContainsKey(k), $"{theme}: brak {k}");
            foreach (var k in new[] { ThemeKeys.TextPrimary, ThemeKeys.TextSecondary, ThemeKeys.WindowBackground, ThemeKeys.SurfaceRaised })
                Assert.StartsWith("#FF", c[k], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_hard_coded_colors_outside_theme_dictionaries()
    {
        var hex = new Regex("#[0-9A-Fa-f]{6,8}\\b");
        var named = new Regex(@"(Colors\.|Color\.FromA?Rgb|Brushes\.(?!Transparent))");
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(AppDir(), "*.*", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(f);
            if (name is "Light.xaml" or "Dark.xaml" or "generate_icon.ps1") continue;
            if (!f.EndsWith(".xaml") && !f.EndsWith(".cs")) continue;
            var text = File.ReadAllText(f);
            if (f.EndsWith(".xaml"))
            {
                // Glify ikon (&#xE711;) to nie kolory.
                foreach (Match m in hex.Matches(text))
                    if (m.Index < 2 || text.Substring(m.Index - 2, 2) != "&#") bad.Add($"{name}: {m.Value}");
            }
            foreach (Match m in named.Matches(text)) bad.Add($"{name}: {m.Value}");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void Styles_provide_default_styles_for_all_standard_controls()
    {
        var doc = XDocument.Load(Path.Combine(AppDir(), "Themes", "Styles.xaml"));
        var implicitTypes = doc.Root!.Elements(P + "Style").Where(e => e.Attribute(X + "Key") is null).Select(e => (string)e.Attribute("TargetType")!).ToHashSet();
        foreach (var t in new[] { "Window", "Button", "RepeatButton", "ToggleButton", "TextBox", "PasswordBox", "ComboBox", "ComboBoxItem", "CheckBox", "RadioButton",
                     "ListBox", "ListBoxItem", "ListView", "ListViewItem", "TreeView", "TreeViewItem", "ScrollBar", "ScrollViewer", "ToolTip", "ContextMenu", "MenuItem",
                     "TabControl", "TabItem", "DataGrid", "DataGridColumnHeader", "DataGridCell", "ProgressBar", "Slider", "GroupBox", "Expander", "Label" })
            Assert.Contains(t, implicitTypes);
        // Style używają tylko DynamicResource dla pędzli.
        Assert.DoesNotContain("StaticResource Brush.", File.ReadAllText(Path.Combine(AppDir(), "Themes", "Styles.xaml")));
    }

    // ------------------------------------------------------------------ lokalizacja

    private static Dictionary<string, string> Resx(string file) =>
        XDocument.Load(Path.Combine(AppDir(), "Resources", file)).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    [Fact]
    public void Every_catalog_text_exists_in_polish_and_english()
    {
        var en = Resx("Strings.resx");
        var pl = Resx("Strings.pl.resx");
        Assert.Equal(en.Keys.OrderBy(k => k), pl.Keys.OrderBy(k => k));
        var keys = RibbonCatalog.Commands.Select(c => c.TextKey)
            .Concat(RibbonCatalog.Tabs.Select(t => t.TextKey))
            .Concat(RibbonCatalog.Tabs.SelectMany(t => t.Groups).Select(g => g.TextKey))
            .Append(RibbonCatalog.FileTabTextKey);
        foreach (var k in keys)
        {
            Assert.True(en.ContainsKey(k), "en: " + k);
            Assert.False(string.IsNullOrWhiteSpace(pl[k]), "pl: " + k);
        }
        Assert.All(en.Keys, k => Assert.True(k.StartsWith("Shell.") || k.StartsWith("Cmd.") || k.StartsWith("Ribbon."), k));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        var en = Resx("Strings.resx");
        var pl = Resx("Strings.pl.resx");
        var ph = new Regex(@"\{\d+\}");
        foreach (var (k, v) in en)
        {
            var a = ph.Matches(v).Select(m => m.Value).Distinct().OrderBy(s => s);
            var b = ph.Matches(pl[k]).Select(m => m.Value).Distinct().OrderBy(s => s);
            Assert.True(a.SequenceEqual(b), k);
        }
    }

    [Fact]
    public void Shell_keys_used_in_code_exist_in_resources()
    {
        var en = Resx("Strings.resx");
        var core = XDocument.Load(Path.Combine(AppDir(), "..", "ZetPDF.Core", "Resources", "Strings.resx")).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();
        var used = new Regex("\"(Shell\\.(?:[A-Z][A-Za-z]*\\.)*[A-Z][A-Za-z]*|Core\\.[A-Za-z.]+)\"|Loc (Shell\\.[A-Za-z.]+|Core\\.[A-Za-z.]+)");
        var nonText = new[] { "Shell.Button.", "Shell.Text.", "Shell.IconText", "Shell.Status.Zoom", "Shell.RailButton", "Shell.StatusButton", "Shell.StatusSeparator", "Shell.DocumentTab", "Shell.DocumentStrip", "Shell.Status.Page", "Shell.CommandSearch.Box", "Shell.Shortcuts.Grid", "Shell.Rail", "Shell.ButtonBase", "Shell.FocusVisual", "Shell.AccentButtonTemplate", "Shell.TextBoxBase", "Shell.ScrollBar.", "Shell.MenuItem.", "Shell.ComboBox.", "Shell.TreeView.", "Shell.Slider." };
        var missing = new List<string>();
        foreach (var f in Directory.EnumerateFiles(AppDir(), "*.*", SearchOption.AllDirectories).Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains("obj")))
        {
            foreach (Match m in used.Matches(File.ReadAllText(f)))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (key.EndsWith('.') || key.EndsWith("Window")) continue;
                if (nonText.Any(n => key.StartsWith(n))) continue;
                if (!en.ContainsKey(key) && !core.Contains(key)) missing.Add($"{Path.GetFileName(f)}: {key}");
            }
        }
        Assert.Empty(missing.Distinct());
    }

    // ------------------------------------------------------------------ skróty i wyszukiwanie

    [Fact]
    public void Catalog_shortcuts_have_no_conflicts_and_parse()
    {
        var map = ShortcutMap.Build(RibbonCatalog.Commands);
        Assert.Empty(map.Conflicts);
        Assert.Empty(map.Invalid);
        Assert.Contains(map.KeyBindings, b => b.CommandId == CommandIds.Shell.CommandSearch);
        Assert.Contains(map.KeyBindings, b => b.CommandId == CommandIds.View.TogglePanel);
        Assert.Contains(map.Manual, b => b.CommandId == CommandIds.Comment.Highlight); // Shift+H – ręcznie
        Assert.Contains(map.SingleKeys, b => b.CommandId == CommandIds.Tool.Hand);
    }

    [Fact]
    public void Shortcut_conflicts_are_detected()
    {
        var a = new CommandDescriptor("X.A", FeatureModuleId.Shell, CommandKind.Button, "", "Ctrl+J", "");
        var b = new CommandDescriptor("X.B", FeatureModuleId.Shell, CommandKind.Button, "", "Ctrl+J;Ctrl+Shift+J", "");
        var map = ShortcutMap.Build(new[] { a, b });
        var c = Assert.Single(map.Conflicts);
        Assert.Equal("X.A", c.KeptCommandId);
        Assert.Equal("X.B", c.IgnoredCommandId);
        Assert.Equal(2, map.KeyBindings.Count);
    }

    [Theory]
    [InlineData("zapisz", "Zapisz")]
    [InlineData("ZOLC", "Zażółć")]
    [InlineData("lacze", "Łącze")]
    [InlineData("jako", "Zapisz jako")]
    public void Command_search_ignores_case_and_diacritics(string query, string expectedLabel)
    {
        var entries = new[]
        {
            new CommandSearch.Entry("A", "Zapisz"), new CommandSearch.Entry("B", "Zapisz jako"), new CommandSearch.Entry("C", "Zażółć"),
            new CommandSearch.Entry("D", "Łącze"), new CommandSearch.Entry("E", "Otwórz"),
        };
        Assert.Contains(CommandSearch.Find(query, entries), m => m.Entry.Label == expectedLabel);
        Assert.DoesNotContain(CommandSearch.Find(query, entries), m => m.Entry.Label == "Otwórz");
    }

    [Fact]
    public void Command_search_ranks_prefix_first_and_empty_query_returns_nothing()
    {
        var entries = new[] { new CommandSearch.Entry("A", "Dopasuj stronę"), new CommandSearch.Entry("B", "Strona pusta") };
        Assert.Equal("B", CommandSearch.Find("stron", entries)[0].Entry.CommandId);
        Assert.Empty(CommandSearch.Find("   ", entries));
    }

    // ------------------------------------------------------------------ ustawienia, linia poleceń, licencje

    [Theory]
    [InlineData("", true)]
    [InlineData("https://tsa.example.com/rfc3161", true)]
    [InlineData("http://10.0.0.1:8080/tsa", true)]
    [InlineData("ftp://x.example", false)]
    [InlineData("tsa.example.com", false)]
    public void Tsa_validation(string url, bool ok) => Assert.Equal(ok, SettingsViewModel.ValidateTsa(url));

    [Theory]
    [InlineData("", true)]
    [InlineData("jan.kowalski@example.pl", true)]
    [InlineData("jan@", false)]
    [InlineData("a b@c.pl", false)]
    public void Email_validation(string email, bool ok) => Assert.Equal(ok, SettingsViewModel.ValidateEmail(email));

    [Fact]
    public void Folder_and_number_validation()
    {
        Assert.True(SettingsViewModel.ValidateFolder(@"C:\Próbny folder\tmp"));
        Assert.False(SettingsViewModel.ValidateFolder(@"relative\path"));
        Assert.True(SettingsViewModel.TryInt("30", 1, 500, out var v) && v == 30);
        Assert.False(SettingsViewModel.TryInt("0", 1, 500, out _));
        Assert.True(SettingsViewModel.TryZoom("125%", out var z) && Math.Abs(z - 1.25) < 1e-9);
        Assert.False(SettingsViewModel.TryZoom("5", out _));
    }

    [Fact]
    public void Command_line_parses_files_and_password()
    {
        var (files, pwd) = App.ParseArgs(new[] { @"C:\a b\zażółć.pdf", "-pwdTajne123", "drugi.pdf" });
        Assert.Equal(2, files.Count);
        Assert.Equal("Tajne123", pwd);
    }

    [Fact]
    public void Theme_resolution_follows_system()
    {
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(AppTheme.System, systemIsLight: false));
        Assert.Equal(AppTheme.Light, ThemeService.Resolve(AppTheme.System, systemIsLight: true));
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(AppTheme.Dark, systemIsLight: true));
    }

    [Fact]
    public void About_lists_pdfium_with_bsd3_and_license_texts_are_embedded()
    {
        var pdfium = ThirdPartyLibraries.All.Single(l => l.Name == "PDFium");
        Assert.Equal("BSD-3-Clause", pdfium.License);
        Assert.Contains("Redistribution and use in source and binary forms", pdfium.FullText);
        Assert.Contains("FreeType", pdfium.FullText);
        Assert.All(ThirdPartyLibraries.All, l => Assert.DoesNotContain(".txt", l.FullText.Split('\n').Last()));
        Assert.Equal("0.1.0-test", ThirdPartyLibraries.AppVersion);
        Assert.Contains("Apache License", ThirdPartyLibraries.ReadLicense("Apache-2.0.txt"));
    }

    [Fact]
    public void Property_window_formats_sizes()
    {
        var c = CultureInfo.GetCultureInfo("pl");
        Assert.Equal("512 B", DocumentPropertiesWindow.FormatSize(512, c));
        Assert.StartsWith("1,5 KB", DocumentPropertiesWindow.FormatSize(1536, c));
    }
}

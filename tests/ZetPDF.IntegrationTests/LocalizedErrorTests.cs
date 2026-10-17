using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Utilities;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// O-3: komunikaty wyjątków domenowych pokazywane użytkownikowi są w języku interfejsu (klucz .resx w wyjątku),
/// a wyjątki bez klucza dostają ogólny zlokalizowany opis wg rodzaju – angielski komunikat tylko w szczegółach.
/// </summary>
public class LocalizedErrorTests
{
    /// <summary>Zasoby wszystkich modułów (w aplikacji rejestruje je Initialize modułu).</summary>
    private static ILocalizationService Loc(TestHost host)
    {
        var loc = host.Get<ILocalizationService>();
        foreach (var m in ZetPDF.App.ModuleCatalog.CreateAll())
        {
            var asm = m.GetType().Assembly;
            loc.RegisterResources(new System.Resources.ResourceManager(asm.GetName().Name + ".Resources.Strings", asm));
        }
        return loc;
    }

    private static async Task<Exception> Catch(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { return ex; }
        throw new Xunit.Sdk.XunitException("Oczekiwano wyjątku.");
    }

    private static Exception Catch(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex; }
        throw new Xunit.Sdk.XunitException("Oczekiwano wyjątku.");
    }

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "Annot.Error.EmptyXfdf", "pusty" },
        new object[] { "Annot.Error.NotFdf", "FDF" },
        new object[] { "Annot.Error.FieldNameUsed", "imie" },
        new object[] { "Core.Error.PageRangeOutside", "1–3" },
        new object[] { "Core.Error.PageRangeInvalid", "1-3,5" },
    };

    private static async Task<Exception> Produce(TestHost host, string key)
    {
        var pdf = SamplePdfFactory.WithForm();
        return key switch
        {
            "Annot.Error.EmptyXfdf" => await Catch(() => host.Get<IFormService>().ImportDataAsync(pdf, new MemoryStream(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><a/>")), FormDataFormat.Xfdf)),
            "Annot.Error.NotFdf" => await Catch(() => host.Get<IFormService>().ImportDataAsync(pdf, new MemoryStream(Encoding.ASCII.GetBytes("%FDF-1.2\n1 0 obj << /X 1 >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF")), FormDataFormat.Fdf)),
            "Annot.Error.FieldNameUsed" => await Catch(() => host.Get<IFormService>().CreateFieldAsync(pdf, new FormFieldSpec { Kind = FormFieldKind.Text, Name = "imie", PageIndex = 0, Rect = new PdfRect(50, 50, 200, 70) })),
            "Core.Error.PageRangeOutside" => Catch(() => PageRange.Parse("2-9", 3)),
            "Core.Error.PageRangeInvalid" => Catch(() => PageRange.Parse("a-b", 3)),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Domain_errors_carry_resource_key_and_are_shown_in_ui_language(string key, string fragment)
    {
        using var host = new TestHost();
        var loc = Loc(host);
        var ex = await Produce(host, key);
        Assert.True(LocalizedError.TryGetMessageKey(ex, out var actual, out _), $"brak klucza w {ex.GetType().Name}: {ex.Message}");
        Assert.Equal(key, actual);

        loc.SetCulture(new CultureInfo("pl"));
        var pl = loc.Describe(ex, out var specific);
        Assert.True(specific);
        Assert.DoesNotContain(ex.Message, pl); // bez angielskiej wstawki
        Assert.Contains(fragment, pl, StringComparison.OrdinalIgnoreCase);
        loc.SetCulture(new CultureInfo("en"));
        var en = loc.Describe(ex);
        Assert.NotEqual(pl, en);
        Assert.DoesNotContain("(Parameter", en);
    }

    [Fact]
    public void Library_errors_get_localized_generic_text_by_kind()
    {
        using var host = new TestHost();
        var loc = Loc(host);
        loc.SetCulture(new CultureInfo("pl"));
        Assert.Equal(loc.Format("Core.Error.FileInUse", "a.pdf"), loc.Describe(new IOException("The process cannot access the file."), "a.pdf"));
        Assert.Equal(loc["Core.Error.InvalidFormat"], loc.Describe(new System.Xml.XmlException("Data at the root level is invalid.")));
        Assert.Equal(loc["Core.Error.Clipboard"], loc.Describe(new System.Runtime.InteropServices.COMException("OpenClipboard failed", unchecked((int)0x800401D0))));
        var unknown = loc.Describe(new InvalidOperationException("Sequence contains no elements"), out var specific);
        Assert.False(specific);
        Assert.Equal(loc["Core.Error.Unexpected"], unknown);
        // tekst już przetłumaczony przez moduł (np. krok Kreatora działań)
        Assert.Equal("Gotowe zdanie.", loc.Describe(new Exception("Gotowe zdanie.").MarkLocalized()));
    }

    [Fact]
    public void Every_message_key_used_in_code_exists_in_both_languages()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "WithMessageKey\\(\"([\\w.]+)\""))
                keys.Add(m.Groups[1].Value);
        Assert.True(keys.Count > 40, $"znaleziono tylko {keys.Count} kluczy");
        var all = new Dictionary<string, HashSet<string>>();
        foreach (var resx in Directory.EnumerateFiles(src, "Strings*.resx", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var lang = resx.EndsWith(".pl.resx", StringComparison.OrdinalIgnoreCase) ? "pl" : "en";
            if (!all.TryGetValue(lang, out var set)) all[lang] = set = new HashSet<string>();
            foreach (var d in XDocument.Load(resx).Root!.Elements("data")) set.Add((string)d.Attribute("name")!);
        }
        foreach (var k in keys)
        {
            Assert.True(all["en"].Contains(k), $"brak klucza {k} (en)");
            Assert.True(all["pl"].Contains(k), $"brak klucza {k} (pl)");
        }
    }
}

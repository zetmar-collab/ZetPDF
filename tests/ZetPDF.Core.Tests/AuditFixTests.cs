using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

/// <summary>Poprawki z audytu 2026-11-27: wartości ustawień spoza zakresu, ostatnie pliki, limit Cofnij na żywo, plik za duży, łącza IDN.</summary>
public class AuditFixTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_home.Paths.SettingsFile)!);
        File.WriteAllText(_home.Paths.SettingsFile, json);
    }

    [Fact]
    public void Hand_edited_settings_out_of_range_are_clamped_on_load()
    {
        WriteSettings("""{ "General": { "MaxRecentFiles": -1, "UndoLevels": 0, "AutoSaveMinutes": 99999, "OcrSuspectThreshold": 400, "DefaultZoom": -2, "AuthorName": null } }""");
        var s = new SettingsService(_home.Paths).Current;
        Assert.Equal(AppSettings.MinRecentFiles, s.MaxRecentFiles);
        Assert.Equal(AppSettings.MinUndoLevels, s.UndoLevels);
        Assert.Equal(AppSettings.MaxAutoSaveMinutes, s.AutoSaveMinutes);
        Assert.Equal(100, s.OcrSuspectThreshold);
        Assert.Equal(1.0, s.DefaultZoom);
        Assert.Equal(string.Empty, s.AuthorName);
    }

    [Fact]
    public void Recent_files_add_survives_invalid_limit_set_in_memory()
    {
        var settings = new SettingsService(_home.Paths);
        settings.Update(s => s.MaxRecentFiles = -5);
        var recent = new RecentFilesService(settings);
        recent.Add(Path.Combine(_home.Root, "a.pdf"));
        recent.Add(Path.Combine(_home.Root, "b.pdf"));
        Assert.Single(recent.Files);
        Assert.EndsWith("b.pdf", recent.Files[0]);
    }

    [Fact]
    public void Recent_files_remove_matches_relative_and_full_path()
    {
        var settings = new SettingsService(_home.Paths);
        var recent = new RecentFilesService(settings);
        var full = Path.Combine(Environment.CurrentDirectory, "relatywny.pdf");
        recent.Add(full);
        recent.Remove("relatywny.pdf");
        Assert.Empty(recent.Files);
    }

    [Fact]
    public async Task Changing_undo_levels_applies_to_open_documents()
    {
        using var sp = _home.BuildServices();
        var manager = sp.GetRequiredService<IDocumentManager>();
        var session = await manager.CreateNewAsync(SamplePdfFactory.Text(1), "nowy.pdf");
        sp.GetRequiredService<ISettingsService>().Update(s => s.UndoLevels = 7);
        Assert.Equal(7, session.History.Limit);
    }

    [Fact]
    public async Task File_larger_than_limit_is_refused_with_localized_message_before_reading()
    {
        using var sp = _home.BuildServices();
        var manager = sp.GetRequiredService<IDocumentManager>();
        var path = Path.Combine(TestPaths.NewDirectory(), "ogromny.pdf");
        using (var fs = new FileStream(path, FileMode.CreateNew)) fs.SetLength(DocumentManager.MaxOpenFileBytes + 1);
        try
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => manager.OpenAsync(path));
            Assert.True(LocalizedError.TryGetMessageKey(ex, out var key, out _));
            Assert.Equal("Core.Error.FileTooLarge", key);
            Assert.Empty(manager.Documents);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://example.com/a", false)]
    [InlineData("https://аpple.com/", true)] // pierwsza litera to cyrylickie „а”
    [InlineData("mailto:jan@example.com", false)]
    public void Link_description_reveals_punycode_for_non_ascii_domains(string address, bool punycode)
    {
        var uri = new Uri(address);
        Assert.True(LinkSafety.IsAllowed(uri));
        var text = LinkSafety.Describe(uri);
        Assert.Equal(punycode, text.Contains("xn--", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    public void Unsafe_schemes_are_not_allowed(string address) => Assert.False(LinkSafety.IsAllowed(new Uri(address)));
}

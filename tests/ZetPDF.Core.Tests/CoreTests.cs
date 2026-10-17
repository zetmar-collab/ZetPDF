using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Services;
using ZetPDF.Core.Utilities;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Catalog_ids_are_unique_and_every_layout_entry_exists()
    {
        var ids = RibbonCatalog.Commands.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var tab in RibbonCatalog.Tabs)
            foreach (var g in tab.Groups)
                foreach (var id in g.CommandIds)
                    Assert.NotNull(RibbonCatalog.Find(id));
        foreach (var id in RibbonCatalog.Backstage.Concat(RibbonCatalog.QuickAccess).Concat(RibbonCatalog.Menus.SelectMany(m => m.Value)))
            Assert.NotNull(RibbonCatalog.Find(id));
    }

    [Fact]
    public void All_shortcuts_parse_without_conflicts_and_keybinding_safe_ones_are_valid_wpf_gestures()
    {
        var seen = new Dictionary<Shortcut, string>();
        foreach (var c in RibbonCatalog.Commands)
            foreach (var text in c.Shortcuts)
            {
                var s = Shortcut.Parse(text);
                Assert.False(seen.TryGetValue(s, out var other), $"{text}: {c.Id} vs {other}");
                seen[s] = c.Id;
                if (s.CanUseKeyBinding) _ = new KeyGesture(s.Key, s.Modifiers); // nie rzuca
                else Assert.Throws<NotSupportedException>(() => new KeyGesture(s.Key, s.Modifiers));
            }
        Assert.True(Shortcut.Parse("Shift+H").RequiresNonTextFocus);
        Assert.False(Shortcut.Parse("F3").RequiresNonTextFocus);
        Assert.Equal("Ctrl+0", Shortcut.Parse("Ctrl+D0").ToDisplayString());
    }

    [Fact]
    public void Registry_rejects_unknown_and_duplicate_handlers()
    {
        var r = new CommandRegistry();
        var cmd = new ToggleCommand(_ => { }, () => false);
        r.Register(CommandIds.Pages.Delete, cmd);
        Assert.Same(cmd, r.GetHandler(CommandIds.Pages.Delete));
        Assert.Throws<InvalidOperationException>(() => r.Register(CommandIds.Pages.Delete, cmd));
        Assert.Throws<ArgumentException>(() => r.Register("Nope.Nothing", cmd));
        Assert.False(r.TryExecute(CommandIds.Pages.Rotate));
    }
}

public class PageRangeTests
{
    [Theory]
    [InlineData("", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData("1-3,5", 5, new[] { 0, 1, 2, 4 })]
    [InlineData("4-", 5, new[] { 3, 4 })]
    [InlineData(" 2 ; 2, 3–1 ", 5, new[] { 0, 1, 2 })]
    public void Parse(string text, int count, int[] expected) => Assert.Equal(expected, PageRange.Parse(text, count));

    [Theory]
    [InlineData("0", 5)]
    [InlineData("6", 5)]
    [InlineData("a-b", 5)]
    public void Parse_invalid(string text, int count) => Assert.Throws<FormatException>(() => PageRange.Parse(text, count));

    [Fact]
    public void Format_roundtrip() => Assert.Equal("1-3,5,7-8", PageRange.Format(new[] { 0, 1, 2, 4, 6, 7 }));
}

public class PageTransformTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Roundtrip_for_all_rotations(int rotation)
    {
        var t = new PageTransform(new PdfSize(600, 800), new PdfPoint(10, 20), rotation, 1.5, new Point(30, 40));
        foreach (var p in new[] { new PdfPoint(10, 20), new PdfPoint(610, 820), new PdfPoint(123.4, 567.8) })
        {
            var back = t.ToPage(t.ToView(p));
            Assert.Equal(p.X, back.X, 6);
            Assert.Equal(p.Y, back.Y, 6);
        }
    }

    [Fact]
    public void Corners_map_as_expected()
    {
        // Bez obrotu: lewy górny róg strony (x=0, y=H) → początek widoku.
        var t0 = new PageTransform(new PdfSize(600, 800), default, 0, 1, new Point(0, 0));
        Assert.Equal(new Point(0, 0), t0.ToView(new PdfPoint(0, 800)));
        Assert.Equal(new Size(600, 800), t0.ViewSize);
        // 90° w prawo: lewy górny róg strony trafia do prawego górnego rogu widoku.
        var t90 = new PageTransform(new PdfSize(600, 800), default, 90, 1, new Point(0, 0));
        Assert.Equal(new Point(800, 0), t90.ToView(new PdfPoint(0, 800)));
        Assert.Equal(new Size(800, 600), t90.ViewSize);
        // 270°: lewy górny róg strony → lewy dolny róg widoku.
        var t270 = new PageTransform(new PdfSize(600, 800), default, 270, 1, new Point(0, 0));
        Assert.Equal(new Point(0, 600), t270.ToView(new PdfPoint(0, 800)));
    }
}

public sealed class TempHome : IDisposable
{
    public TempHome()
    {
        Root = TestPaths.NewDirectory();
        Paths = new AppPaths(Root);
    }

    public string Root { get; }
    public AppPaths Paths { get; }

    public ServiceProvider BuildServices(Action<IServiceCollection>? extra = null)
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(Paths);
        extra?.Invoke(s);
        return s.BuildServiceProvider();
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Root)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class DocumentSessionTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private PdfDocumentSession NewSession(byte[] pdf, long budget = 256L << 20, DocumentPermissions perms = DocumentPermissions.All) =>
        new(null, "test.pdf", DocumentSnapshot.FromBytes(pdf), PdfSecurityInfo.None, perms, new SnapshotStorageOptions(_home.Paths.TempDirectory, budget));

    private static IDocumentCommand AddPage() => new DelegateDocumentCommand("Test.AddPage",
        (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);

    private static int Pages(DocumentSnapshot s) => PdfSharpEdit.Read(s.GetBytes(), d => d.PageCount);

    [Fact]
    public async Task Execute_undo_redo_restores_identical_state()
    {
        var original = SamplePdfFactory.Text(2);
        using var s = NewSession(original);
        var events = new List<DocumentChangeSource>();
        s.Changed += (_, e) => events.Add(e.Source);

        Assert.False(s.IsModified);
        Assert.True(await s.ExecuteAsync(AddPage()));
        Assert.True(await s.ExecuteAsync(AddPage()));
        Assert.Equal(4, Pages(s.Current));
        Assert.True(s.IsModified);
        Assert.Equal("Test.AddPage", s.History.UndoDescriptionKey);

        await s.History.UndoAsync();
        await s.History.UndoAsync();
        Assert.Equal(original, s.Current.GetBytes());
        Assert.False(s.IsModified);
        Assert.False(s.History.CanUndo);

        await s.History.RedoAsync();
        Assert.Equal(3, Pages(s.Current));
        Assert.True(s.IsModified);
        Assert.Equal(new[] { DocumentChangeSource.Command, DocumentChangeSource.Command, DocumentChangeSource.Undo, DocumentChangeSource.Undo, DocumentChangeSource.Redo }, events);
    }

    [Fact]
    public async Task New_command_clears_redo_and_noop_is_not_recorded()
    {
        using var s = NewSession(SamplePdfFactory.Text(1));
        await s.ExecuteAsync(AddPage());
        await s.History.UndoAsync();
        Assert.True(s.History.CanRedo);
        await s.ExecuteAsync(AddPage());
        Assert.False(s.History.CanRedo);
        var noop = new DelegateDocumentCommand("Test.Noop", (pdf, _, _) => Task.FromResult(pdf));
        Assert.False(await s.ExecuteAsync(noop));
    }

    [Fact]
    public async Task Permission_is_enforced()
    {
        using var s = NewSession(SamplePdfFactory.Text(1), perms: DocumentPermissions.Print);
        await Assert.ThrowsAsync<DocumentPermissionException>(() => s.ExecuteAsync(AddPage()));
    }

    [Fact]
    public async Task Failing_command_leaves_state_unchanged()
    {
        using var s = NewSession(SamplePdfFactory.Text(1));
        var before = s.Current;
        var bad = new DelegateDocumentCommand("Test.Bad", (_, _, _) => throw new InvalidDataException("boom"));
        await Assert.ThrowsAsync<InvalidDataException>(() => s.ExecuteAsync(bad));
        Assert.Same(before, s.Current);
        Assert.False(s.History.CanUndo);
    }

    [Fact]
    public async Task Snapshots_over_budget_spill_to_disk_and_still_restore()
    {
        var original = SamplePdfFactory.Text(3);
        using var s = NewSession(original, budget: 1); // wszystko poza bieżącym na dysk
        for (int i = 0; i < 5; i++) await s.ExecuteAsync(AddPage());
        var snapDir = _home.Paths.TempDirectory;
        Assert.True(Directory.Exists(snapDir) && Directory.EnumerateFiles(snapDir).Any());
        for (int i = 0; i < 5; i++) await s.History.UndoAsync();
        Assert.Equal(original, s.Current.GetBytes());
    }

    [Fact]
    public async Task Undo_limit_drops_oldest()
    {
        using var s = NewSession(SamplePdfFactory.Text(1));
        s.History.Limit = 2;
        for (int i = 0; i < 4; i++) await s.ExecuteAsync(AddPage());
        await s.History.UndoAsync();
        await s.History.UndoAsync();
        Assert.False(s.History.CanUndo);
        Assert.Equal(3, Pages(s.Current));
    }

    [Fact]
    public async Task Encryption_change_is_undoable()
    {
        using var s = NewSession(SamplePdfFactory.Text(1));
        var enc = new PdfEncryptionSettings("u", "o");
        await s.ExecuteAsync(new SetEncryptionCommand("Core.Undo.SetEncryption", enc));
        Assert.Equal(enc, s.Current.Encryption);
        await s.History.UndoAsync();
        Assert.Null(s.Current.Encryption);
    }
}

public class DocumentManagerTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public async Task Open_modify_save_with_polish_path()
    {
        using var sp = _home.BuildServices();
        var manager = sp.GetRequiredService<IDocumentManager>();
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(2));

        var session = await manager.OpenAsync(path);
        Assert.NotNull(session);
        Assert.Same(session, manager.ActiveDocument);
        Assert.Equal("zażółć gęślą.pdf", session!.DisplayName);

        await session.ExecuteAsync(new DelegateDocumentCommand("Test.AddPage", (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage()))));
        await manager.SaveAsync(session);
        Assert.False(session.IsModified);
        Assert.Equal(3, PdfSharpEdit.Read(File.ReadAllBytes(path), d => d.PageCount));
        Assert.False(File.Exists(path + ".bak"));
        Assert.Contains(path, sp.GetRequiredService<IRecentFilesService>().Files);

        // Ponowne otwarcie tego samego pliku aktywuje istniejącą kartę.
        Assert.Same(session, await manager.OpenAsync(path));
        manager.Close(session);
        Assert.Empty(manager.Documents);
        Assert.Null(manager.ActiveDocument);
    }

    [Fact]
    public async Task Encrypted_document_opened_with_user_password_is_held_decrypted_and_reencrypted_on_save()
    {
        // Odszyfrowanie hasłem użytkownika robi PDFium (IPdfRenderer.RemoveSecurityAsync, renderer-viewer).
        // Tu podstawiamy renderer testowy, żeby sprawdzić przepływ DocumentManager niezależnie od PDFium.
        using var sp = _home.BuildServices(s => s.AddSingleton<IPdfRenderer, FakeDecryptingRenderer>());
        var manager = sp.GetRequiredService<IDocumentManager>();
        var path = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "szyfrowany AES.pdf");

        // Bez IDialogService i bez hasła → wyjątek.
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => manager.OpenAsync(path));

        var session = await manager.OpenAsync(path, SamplePdfFactory.UserPassword);
        Assert.NotNull(session);
        Assert.True(session!.SecurityInfo.IsEncrypted);
        Assert.Equal(PdfEncryptionMethod.Aes256, session.SecurityInfo.Method);
        Assert.False(session.SecurityInfo.OpenedWithOwnerPassword);
        Assert.False(session.Can(DocumentPermissions.ModifyContent));   // uprawnienia z pliku
        Assert.True(session.Can(DocumentPermissions.Print));
        Assert.False(PdfSecurityHelper.IsEncrypted(session.Current.GetBytes())); // w pamięci bez szyfrowania
        Assert.Equal(SamplePdfFactory.UserPassword, session.Current.Encryption!.UserPassword);

        var outPath = Path.Combine(Path.GetDirectoryName(path)!, "zapisany ąę.pdf");
        await manager.SaveAsync(session, outPath);
        var saved = File.ReadAllBytes(outPath);
        Assert.True(PdfSecurityHelper.IsEncrypted(saved));
        var info = PdfSecurityHelper.Analyze(saved, SamplePdfFactory.UserPassword);
        Assert.Equal(PdfEncryptionMethod.Aes256, info.Method);
        Assert.False(info.OpenedWithOwnerPassword);
        Assert.False(info.FilePermissions.HasFlag(DocumentPermissions.ModifyContent));
    }

    [Fact]
    public async Task Encrypted_document_opened_with_owner_password_has_full_rights()
    {
        using var sp = _home.BuildServices();
        var manager = sp.GetRequiredService<IDocumentManager>();
        var path = TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "właściciel.pdf");
        var session = await manager.OpenAsync(path, SamplePdfFactory.OwnerPassword);
        Assert.True(session!.SecurityInfo.OpenedWithOwnerPassword);
        Assert.Equal(DocumentPermissions.All, session.Permissions);
        // Hasło użytkownika nieznane (AES-256 nie pozwala go odtworzyć) → przy zapisie = hasło właściciela (DECYZJE D-08).
        Assert.Equal(SamplePdfFactory.OwnerPassword, session.Current.Encryption!.UserPassword);
        Assert.Equal(SamplePdfFactory.OwnerPassword, session.Current.Encryption.OwnerPassword);
    }

    [Fact]
    public async Task Corrupted_and_non_pdf_files_raise_domain_exception()
    {
        using var sp = _home.BuildServices();
        var manager = sp.GetRequiredService<IDocumentManager>();
        await Assert.ThrowsAsync<PdfCorruptException>(() => manager.OpenAsync(TestPaths.WriteSample(SamplePdfFactory.NotAPdf(), "nie pdf.pdf")));
        await Assert.ThrowsAsync<PdfCorruptException>(() => manager.OpenAsync(TestPaths.WriteSample(SamplePdfFactory.Corrupted(), "uszkodzony.pdf")));
        Assert.Empty(manager.Documents);
    }

    [Fact]
    public async Task Save_transforms_run_in_order_before_encryption_and_do_not_change_session()
    {
        using var sp = _home.BuildServices();
        var manager = sp.GetRequiredService<IDocumentManager>();
        var path = TestPaths.WriteSample(SamplePdfFactory.Text(1), "hak zapisu.pdf");
        var session = (await manager.OpenAsync(path))!;
        var calls = new List<string>();
        using var first = manager.AddSaveTransform((s, pdf, _) =>
        {
            calls.Add("1");
            Assert.False(PdfSecurityHelper.IsEncrypted(pdf));
            return Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage()));
        });
        var second = manager.AddSaveTransform((s, pdf, _) =>
        {
            calls.Add("2:" + PdfSharpEdit.Read(pdf, d => d.PageCount));
            return Task.FromResult(pdf);
        });
        await session.ExecuteAsync(new SetEncryptionCommand("Test.Encrypt", new PdfEncryptionSettings("user1", "owner1")));
        await manager.SaveAsync(session);

        Assert.Equal(new[] { "1", "2:2" }, calls);
        var saved = File.ReadAllBytes(path);
        Assert.True(PdfSecurityHelper.IsEncrypted(saved));
        Assert.Equal(2, PdfSharpEdit.Read(PdfSecurityHelper.RemoveSecurity(saved, "owner1"), d => d.PageCount));
        Assert.Equal(1, PdfSharpEdit.Read(session.Current.GetBytes(), d => d.PageCount)); // sesja bez zmian
        Assert.False(session.IsModified);

        second.Dispose();
        calls.Clear();
        await manager.GetBytesForSaveAsync(session);
        Assert.Equal(new[] { "1" }, calls);
    }

    [Fact]
    public async Task CreateNew_is_modified_and_needs_path()
    {
        using var sp = _home.BuildServices();
        var manager = sp.GetRequiredService<IDocumentManager>();
        var s = await manager.CreateNewAsync(SamplePdfFactory.Text(1), "Nowy");
        Assert.True(s.IsModified);
        Assert.Null(s.FilePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveAsync(s));
    }
}

/// <summary>Renderer testowy: sprawdzenie przez PDFsharp, odszyfrowanie znanym hasłem właściciela (symulacja PDFium).</summary>
internal sealed class FakeDecryptingRenderer : IPdfRenderer
{
    public Task<IRenderDocument> OpenAsync(byte[] pdf, string? password = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<PdfProbeResult> ProbeAsync(byte[] pdf, string? password = null, CancellationToken ct = default) => Task.FromResult(DocumentManager.ProbeWithPdfSharp(pdf, password));
    public Task<byte[]> RemoveSecurityAsync(byte[] pdf, string password, CancellationToken ct = default) => Task.FromResult(PdfSecurityHelper.RemoveSecurity(pdf, SamplePdfFactory.OwnerPassword));
}

public class SecurityHelperTests
{
    [Fact]
    public void Analyze_distinguishes_owner_and_user_password()
    {
        var enc = SamplePdfFactory.EncryptedAes256();
        var owner = PdfSecurityHelper.Analyze(enc, SamplePdfFactory.OwnerPassword);
        var user = PdfSecurityHelper.Analyze(enc, SamplePdfFactory.UserPassword);
        Assert.True(owner.OpenedWithOwnerPassword);
        Assert.False(user.OpenedWithOwnerPassword);
        Assert.False(user.FilePermissions.HasFlag(DocumentPermissions.ModifyContent));
        Assert.True(user.FilePermissions.HasFlag(DocumentPermissions.Print));
        Assert.Throws<PdfInvalidPasswordException>(() => PdfSecurityHelper.Analyze(enc, "złe"));
        Assert.False(PdfSecurityHelper.HasEmptyUserPassword(enc));
    }

    [Fact]
    public void Resave_settings_with_user_password_generate_new_owner_password()
    {
        var info = new PdfSecurityInfo(true, PdfEncryptionMethod.Aes256, 6, DocumentPermissions.Print, false, "u");
        var s = PdfSecurityHelper.CreateResaveSettings(info, userPasswordEmpty: false)!;
        Assert.Equal("u", s.UserPassword);
        Assert.NotEqual("u", s.OwnerPassword);
        Assert.Equal(DocumentPermissions.Print, s.Permissions);
    }
}

public class SettingsAndLocalizationTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private sealed class ModuleCfg { public int Value { get; set; } = 1; public string Name { get; set; } = ""; }

    [Fact]
    public void Settings_persist_including_module_sections()
    {
        var a = new SettingsService(_home.Paths);
        a.Update(s => { s.Language = "pl"; s.Theme = AppTheme.Dark; s.UndoLevels = 12; });
        a.SetModuleSettings("Test", new ModuleCfg { Value = 7, Name = "zażółć" });
        var b = new SettingsService(_home.Paths);
        Assert.Equal("pl", b.Current.Language);
        Assert.Equal(AppTheme.Dark, b.Current.Theme);
        Assert.Equal(12, b.Current.UndoLevels);
        var m = b.GetModuleSettings<ModuleCfg>("Test");
        Assert.Equal(7, m.Value);
        Assert.Equal("zażółć", m.Name);
    }

    [Fact]
    public void Localization_switches_live_and_reports_missing_keys()
    {
        var loc = new LocalizationService(initial: new CultureInfo("en"));
        var changed = new List<string?>();
        loc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("Cancel", loc["Core.Button.Cancel"]);
        loc.SetCulture(new CultureInfo("pl-PL"));
        Assert.Equal("Anuluj", loc["Core.Button.Cancel"]);
        Assert.Contains("Item[]", changed);
        Assert.Equal("[Nie.Ma.Klucza]", loc["Nie.Ma.Klucza"]);
        Assert.Equal("Strona 2 z 5", loc.Format("Core.Progress.Page", 2, 5));
    }

    [Fact]
    public void Core_resources_have_same_keys_in_pl_and_en_without_empty_values()
    {
        var rm = CoreResources.Manager;
        var en = rm.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        var pl = rm.GetResourceSet(new CultureInfo("pl"), true, false)!.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        Assert.Equal(en.Keys.OrderBy(k => k), pl.Keys.OrderBy(k => k));
        Assert.All(en.Values.Concat(pl.Values), v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.All(en.Keys, k => Assert.StartsWith("Core.", k));
    }
}

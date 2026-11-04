using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Core.Tests;

/// <summary>Autozapis i odzyskiwanie po awarii: format, DPAPI, porzucone kopie, sprzątanie, ochrona hasłem.</summary>
public class RecoveryTests : IDisposable
{
    /// <summary>Numer procesu, który nie istnieje.</summary>
    private const int DeadPid = 0x7FFFFFF0;

    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private RecoveryStore Store() => new(_home.Paths.RecoveryDirectory);

    private static IDocumentCommand AddPage() => new DelegateDocumentCommand("Test.AddPage",
        (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.AddPage())), DocumentChangeKind.PageStructure, DocumentPermissions.AssembleDocument);

    private static RecoveryInfo DeadInfo(string? path = null, string name = "zażółć gęślą.pdf", bool isProtected = false) => new()
    {
        Id = $"{DeadPid}-{Guid.NewGuid():N}",
        ProcessId = DeadPid,
        ProcessStartUtcTicks = 1,
        OriginalPath = path,
        DisplayName = name,
        SavedUtc = DateTime.UtcNow,
        State = path is null ? RecoveryDocumentState.NeverSaved : RecoveryDocumentState.Modified,
        IsProtected = isProtected,
    };

    private string[] Files() => Directory.Exists(_home.Paths.RecoveryDirectory) ? Directory.GetFiles(_home.Paths.RecoveryDirectory) : Array.Empty<string>();

    private sealed class Dialogs : IDialogService
    {
        public Queue<string?> Passwords { get; } = new();
        public int Asked { get; private set; }
        public string? AskPassword(string documentName, bool isRetry) { Asked++; return Passwords.Count > 0 ? Passwords.Dequeue() : null; }
        public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null) => null;
        public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null) => null;
        public string? SelectFolder(string title, string? initialDirectory = null) => null;
        public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) { }
        public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) => true;
        public YesNoCancel AskYesNoCancel(string text, string? title = null) => YesNoCancel.No;
        public Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true) => Task.FromResult(true);
        public bool? ShowDialog(System.Windows.Window dialog) => false;
        public void ShowModeless(System.Windows.Window window) { }
        public void ShowError(Exception exception, string? contextText = null) { }
    }

    // ------------------------------------------------------------------ magazyn

    [Fact]
    public void Write_then_read_roundtrip_and_files_are_protected()
    {
        var store = Store();
        var pdf = SamplePdfFactory.Text(2);
        var info = DeadInfo(@"C:\Dokumenty\zażółć gęślą.pdf");
        Assert.True(store.Write(info, pdf));

        Assert.Equal(pdf, store.ReadPdf(info.Id));
        var files = Files();
        Assert.Equal(2, files.Length);
        foreach (var file in files)
        {
            var raw = File.ReadAllBytes(file);
            Assert.True(raw.AsSpan().IndexOf("%PDF"u8) < 0, "jawny PDF w " + file);
            var text = Encoding.UTF8.GetString(raw);
            Assert.DoesNotContain("Dokumenty", text);
            Assert.DoesNotContain("DisplayName", text);
            // DPAPI: nagłówek blobu i brak możliwości odczytu bez właściwej entropii
            Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));
        }
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Abandoned_copy_is_listed_and_running_process_copy_is_not()
    {
        var store = Store();
        var dead = DeadInfo(@"C:\a\b.pdf");
        store.Write(dead, SamplePdfFactory.Text(1));
        var live = DeadInfo() with
        {
            Id = RecoveryStore.NewId(),
            ProcessId = Environment.ProcessId,
            ProcessStartUtcTicks = RecoveryStore.CurrentProcessStartUtcTicks(),
        };
        store.Write(live, SamplePdfFactory.Text(1));

        var found = store.FindAbandoned();

        var single = Assert.Single(found);
        Assert.Equal(dead.Id, single.Id);
        Assert.Equal(@"C:\a\b.pdf", single.OriginalPath);
        Assert.Equal(dead.DisplayName, single.DisplayName);
        Assert.Equal(RecoveryDocumentState.Modified, single.State);
    }

    [Fact]
    public void Reused_process_number_does_not_hide_abandoned_copy()
    {
        var store = Store();
        var info = DeadInfo() with { Id = RecoveryStore.NewId(), ProcessId = Environment.ProcessId, ProcessStartUtcTicks = 12345 };
        store.Write(info, SamplePdfFactory.Text(1));
        Assert.Equal(info.Id, Assert.Single(store.FindAbandoned()).Id);
    }

    [Fact]
    public void Cleanup_removes_temp_files_orphans_and_unreadable_descriptions()
    {
        var store = Store();
        var keep = DeadInfo();
        store.Write(keep, SamplePdfFactory.Text(1));
        var dir = _home.Paths.RecoveryDirectory;
        var orphanTemp = Path.Combine(dir, $"{DeadPid}-{Guid.NewGuid():N}.abc.pdf.tmp");
        var orphanData = Path.Combine(dir, $"{DeadPid}-{Guid.NewGuid():N}.recovery");
        var badId = $"{DeadPid}-{Guid.NewGuid():N}";
        File.WriteAllBytes(orphanTemp, new byte[] { 1 });
        File.WriteAllBytes(orphanData, new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, badId + ".info"), new byte[] { 1, 2, 3 }); // nieczytelny opis
        File.WriteAllBytes(Path.Combine(dir, badId + ".recovery"), new byte[] { 1 });
        var ownTemp = Path.Combine(dir, $"{Environment.ProcessId}-{Guid.NewGuid():N}.abc.pdf.tmp");
        File.WriteAllBytes(ownTemp, new byte[] { 1 });

        Assert.Equal(keep.Id, Assert.Single(store.FindAbandoned()).Id);

        Assert.False(File.Exists(orphanTemp));
        Assert.False(File.Exists(orphanData));
        Assert.False(File.Exists(Path.Combine(dir, badId + ".info")));
        Assert.False(File.Exists(Path.Combine(dir, badId + ".recovery")));
        Assert.True(File.Exists(ownTemp)); // plik działającego procesu zostaje
    }

    [Fact]
    public void Write_cancelled_by_save_does_not_publish_copy()
    {
        var store = Store();
        Assert.False(store.Write(DeadInfo(), SamplePdfFactory.Text(1), stillWanted: () => false));
        Assert.Empty(Files());
    }

    [Fact]
    public void Delete_own_leaves_copies_of_other_processes()
    {
        var store = Store();
        var dead = DeadInfo();
        store.Write(dead, SamplePdfFactory.Text(1));
        var own = DeadInfo() with { Id = RecoveryStore.NewId() };
        store.Write(own, SamplePdfFactory.Text(1));
        store.DeleteOwn();
        Assert.Equal(2, Files().Length);
        Assert.All(Files(), f => Assert.Contains(dead.Id, f));
    }

    [Theory]
    [InlineData(@"C:\a\b.pdf", true)]
    [InlineData(@"\\serwer\udzial\b.pdf", true)]
    [InlineData(@"a\b.pdf", false)]
    [InlineData(@"C:\a\b.pdf:ukryty", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"\\?\C:\a.pdf", false)]
    public void Original_path_is_validated(string path, bool ok) => Assert.Equal(ok, RecoveryStore.IsAcceptablePath(path));

    // ------------------------------------------------------------------ usługa autozapisu

    private async Task<(ServiceProvider Sp, DocumentManager Manager, AutoSaveService Auto, IPdfDocumentSession Session, string Path)> OpenAsync(byte[] pdf, string? password = null, Dialogs? dialogs = null)
    {
        var sp = _home.BuildServices(s => { if (dialogs is not null) s.AddSingleton<IDialogService>(dialogs); });
        var manager = (DocumentManager)sp.GetRequiredService<IDocumentManager>();
        var path = Path.Combine(TestPaths.NewDirectory(), "zażółć gęślą.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        var session = (await manager.OpenAsync(path, password))!;
        var auto = sp.GetRequiredService<AutoSaveService>();
        return (sp, manager, auto, session, path);
    }

    [Fact]
    public async Task Autosave_writes_only_changed_documents_and_save_removes_the_copy()
    {
        var (sp, manager, auto, session, _) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp)
        {
            auto.Start();
            await auto.SaveNowAsync();
            Assert.Empty(Files()); // bez zmian nie ma kopii

            await session.ExecuteAsync(AddPage());
            await auto.SaveNowAsync();
            var info = Assert.Single(Files(), f => f.EndsWith(".info", StringComparison.Ordinal));
            Assert.Equal(2, Files().Length);
            var id = Path.GetFileNameWithoutExtension(info);
            Assert.Equal(session.Current.GetBytes(), auto.Store.ReadPdf(id));

            await manager.SaveAsync(session);
            Assert.Empty(Files());
            auto.Stop(deleteFiles: true);
        }
    }

    [Fact]
    public async Task Next_pass_overwrites_the_same_copy_with_newer_content()
    {
        var (sp, _, auto, session, _) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp)
        {
            auto.Start();
            await session.ExecuteAsync(AddPage());
            await auto.SaveNowAsync();
            await session.ExecuteAsync(AddPage());
            await auto.SaveNowAsync();
            Assert.Equal(2, Files().Length);
            var id = Path.GetFileNameWithoutExtension(Files()[0]);
            Assert.Equal(session.Current.GetBytes(), auto.Store.ReadPdf(id));
            auto.Stop(deleteFiles: true);
        }
    }

    [Fact]
    public async Task Close_without_saving_and_normal_exit_remove_copies()
    {
        var (sp, manager, auto, session, _) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp)
        {
            auto.Start();
            await session.ExecuteAsync(AddPage());
            await auto.SaveNowAsync();
            Assert.NotEmpty(Files());
            manager.Close(session);
            Assert.Empty(Files());

            var other = await manager.CreateNewAsync(SamplePdfFactory.Text(1), "nowy.pdf");
            await auto.SaveNowAsync();
            Assert.NotEmpty(Files());
            auto.Stop(deleteFiles: true);
            Assert.Empty(Files());
            GC.KeepAlive(other);
        }
    }

    [Fact]
    public async Task Crash_keeps_copy_which_new_process_can_recover()
    {
        // Awaria: proces nie woła Stop(deleteFiles: true); kolejny proces widzi kopię, gdy opis wskazuje martwego właściciela.
        var (sp, manager, auto, session, path) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp)
        {
            auto.Start();
            await session.ExecuteAsync(AddPage());
            await auto.SaveNowAsync();
            auto.Stop(deleteFiles: false);
            Assert.Equal(2, Files().Length);
            var expected = session.Current.GetBytes();
            var id = Path.GetFileNameWithoutExtension(Files()[0]);
            manager.Close(session);

            // ten sam stan co po awarii: dane opisu z kopii, właściciel martwy
            var info = DeadInfo(path) with { SourceLength = new FileInfo(path).Length, SourceModifiedUtcTicks = new FileInfo(path).LastWriteTimeUtc.Ticks };
            var store = auto.Store;
            store.Write(info, store.ReadPdf(id));
            var found = Assert.Single(store.FindAbandoned(), f => f.ProcessId == DeadPid);

            var recovered = (await manager.OpenRecoveredAsync(found, store.ReadPdf(found.Id), _ => null))!;
            Assert.True(recovered.IsModified);
            Assert.Equal(path, recovered.FilePath);
            Assert.Equal(expected, recovered.Current.GetBytes());
            Assert.Null(recovered.Current.Encryption);
            Assert.False(manager.HasChangedOnDisk(recovered));
        }
    }

    [Fact]
    public async Task Disabling_autosave_removes_copies_of_this_process()
    {
        var (sp, _, auto, session, _) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp)
        {
            auto.Start();
            await session.ExecuteAsync(AddPage());
            await auto.SaveNowAsync();
            Assert.NotEmpty(Files());
            sp.GetRequiredService<ISettingsService>().Update(s => s.AutoSaveEnabled = false);
            Assert.Empty(Files());
            auto.Stop(deleteFiles: true);
        }
    }

    [Fact]
    public void Settings_default_to_autosave_every_five_minutes()
    {
        var s = new AppSettings();
        Assert.True(s.AutoSaveEnabled);
        Assert.Equal(5, s.AutoSaveMinutes);
        Assert.EndsWith(Path.Combine("Local", "recovery"), _home.Paths.RecoveryDirectory, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ odzyskiwanie: ostrzeżenia i ochrona

    [Fact]
    public async Task Recovered_document_still_warns_about_changed_file_and_signature()
    {
        var sp = _home.BuildServices();
        using (sp)
        {
            var manager = (DocumentManager)sp.GetRequiredService<IDocumentManager>();
            var path = Path.Combine(TestPaths.NewDirectory(), "a.pdf");
            await File.WriteAllBytesAsync(path, SamplePdfFactory.Text(1).Concat("\n%/ByteRange\n"u8.ToArray()).ToArray());
            // stan pliku z opisu inny niż na dysku: ktoś zmienił plik po awarii
            var info = DeadInfo(path) with { SourceLength = 1, SourceModifiedUtcTicks = 1 };
            var session = (await manager.OpenRecoveredAsync(info, SamplePdfFactory.Text(2), _ => null))!;
            Assert.True(session.IsModified);
            Assert.True(manager.HasChangedOnDisk(session));
            Assert.True(manager.HasSignatures(session));
        }
    }

    [Fact]
    public async Task Recovered_copy_of_open_file_gets_own_tab_without_path()
    {
        var (sp, manager, _, session, path) = await OpenAsync(SamplePdfFactory.Text(1));
        using (sp)
        {
            var recovered = (await manager.OpenRecoveredAsync(DeadInfo(path), SamplePdfFactory.Text(2), _ => null))!;
            Assert.NotSame(session, recovered);
            Assert.Null(recovered.FilePath);
            Assert.True(recovered.IsModified);
        }
    }

    [Fact]
    public async Task Protected_document_copy_contains_no_password_and_recovery_restores_protection_from_original()
    {
        var dialogs = new Dialogs();
        var (sp, manager, auto, session, path) = await OpenAsync(SamplePdfFactory.EncryptedAes256(), SamplePdfFactory.OwnerPassword, dialogs);
        using (sp)
        {
            auto.Start();
            await session.ExecuteAsync(new DelegateDocumentCommand("Test.Comment", (pdf, _, _) => Task.FromResult(PdfSharpEdit.Modify(pdf, d => d.Info.Keywords = "x")), DocumentChangeKind.Metadata, DocumentPermissions.None));
            await auto.SaveNowAsync();
            var encryption = session.Current.Encryption!;
            Assert.NotNull(encryption);

            var id = Path.GetFileNameWithoutExtension(Files()[0]);
            var infoBlob = File.ReadAllBytes(Path.Combine(_home.Paths.RecoveryDirectory, id + ".info"));
            // odszyfrowany opis też nie zawiera haseł
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(infoBlob, "ZetPDF.Recovery.Info.1"u8.ToArray(), DataProtectionScope.CurrentUser));
            Assert.DoesNotContain(SamplePdfFactory.OwnerPassword, json);
            Assert.DoesNotContain(encryption.OwnerPassword, json);
            Assert.Contains("\"IsProtected\":true", json);
            foreach (var file in Files())
                Assert.DoesNotContain(SamplePdfFactory.OwnerPassword, Encoding.Latin1.GetString(File.ReadAllBytes(file)));

            var expected = session.Current.GetBytes();
            var permissions = session.Permissions;
            auto.Stop(deleteFiles: false);
            manager.Close(session);

            dialogs.Passwords.Enqueue(SamplePdfFactory.OwnerPassword);
            var info = DeadInfo(path, isProtected: true) with { Permissions = permissions };
            var recovered = (await manager.OpenRecoveredAsync(info, expected, _ => throw new InvalidOperationException("nie powinno pytać o nowe hasło")))!;

            Assert.Equal(1, dialogs.Asked);
            Assert.True(recovered.IsModified);
            Assert.NotNull(recovered.Current.Encryption);
            Assert.Equal(SamplePdfFactory.OwnerPassword, recovered.Current.Encryption!.OwnerPassword);
            Assert.Equal(permissions, recovered.Permissions);

            // po zapisie plik nadal wymaga hasła
            await manager.SaveAsync(recovered);
            var probe = DocumentManager.ProbeWithPdfSharp(await File.ReadAllBytesAsync(path), null);
            Assert.Equal(PdfProbeStatus.PasswordRequired, probe.Status);
            Assert.Equal(PdfProbeStatus.Ok, DocumentManager.ProbeWithPdfSharp(await File.ReadAllBytesAsync(path), SamplePdfFactory.OwnerPassword).Status);
        }
    }

    [Fact]
    public async Task Protected_copy_without_original_asks_for_new_password_and_cancel_keeps_it_closed()
    {
        var sp = _home.BuildServices(s => s.AddSingleton<IDialogService>(new Dialogs()));
        using (sp)
        {
            var manager = (DocumentManager)sp.GetRequiredService<IDocumentManager>();
            var missing = Path.Combine(TestPaths.NewDirectory(), "brak.pdf");
            var info = DeadInfo(missing, isProtected: true) with { Permissions = DocumentPermissions.Print };

            Assert.Null(await manager.OpenRecoveredAsync(info, SamplePdfFactory.Text(1), _ => null));
            Assert.Empty(manager.Documents);

            var recovered = (await manager.OpenRecoveredAsync(info, SamplePdfFactory.Text(1), _ => "nowe-hasło"))!;
            Assert.Equal("nowe-hasło", recovered.Current.Encryption!.UserPassword);
            Assert.Equal(DocumentPermissions.Print, recovered.Permissions);
            Assert.Equal(DocumentPermissions.Print, recovered.Current.Encryption.Permissions);

            await manager.SaveAsync(recovered);
            Assert.Equal(PdfProbeStatus.PasswordRequired, DocumentManager.ProbeWithPdfSharp(await File.ReadAllBytesAsync(missing), null).Status);
        }
    }

    [Fact]
    public async Task Protection_added_in_session_on_plain_original_is_not_lost()
    {
        var sp = _home.BuildServices(s => s.AddSingleton<IDialogService>(new Dialogs()));
        using (sp)
        {
            var manager = (DocumentManager)sp.GetRequiredService<IDocumentManager>();
            var path = Path.Combine(TestPaths.NewDirectory(), "jawny.pdf");
            await File.WriteAllBytesAsync(path, SamplePdfFactory.Text(1));
            var recovered = (await manager.OpenRecoveredAsync(DeadInfo(path, isProtected: true), SamplePdfFactory.Text(2), _ => "h"))!;
            Assert.Equal("h", recovered.Current.Encryption!.UserPassword);
        }
    }
}

using ZetPDF.App.Shell;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// DECYZJE D-23: plik z samym hasłem uprawnień (puste hasło otwarcia) otwarty bez hasła właściciela – przed zapisem
/// wybór: hasło właściciela / nowe hasło uprawnień / bez ochrony (strażnik powłoki <see cref="OwnerProtectionGuard"/>
/// z atrapą okien, prawdziwy DocumentManager i usługi). DECYZJE D-22: informacja o ponownym szyfrowaniu RC4 → AES-256.
/// </summary>
public class OwnerOnlyProtectionTests
{
    private const string Owner = "właściciel-ą1";
    private const DocumentPermissions Perms = DocumentPermissions.Print | DocumentPermissions.Annotate;

    private sealed class Prompts(OwnerProtectionChoice[] choices, params string?[] passwords) : IOwnerProtectionPrompts
    {
        private int _choice, _password;
        public int ChoiceCalls => _choice;
        public List<bool> PasswordRetries { get; } = new();
        public OwnerProtectionChoice AskOwnerProtectionChoice(string documentName, bool canEnterOwnerPassword)
        {
            Assert.True(canEnterOwnerPassword);
            return _choice < choices.Length ? choices[_choice++] : OwnerProtectionChoice.Cancel;
        }
        public string? AskOwnerPassword(string documentName, bool isRetry)
        {
            PasswordRetries.Add(isRetry);
            return _password < passwords.Length ? passwords[_password++] : null;
        }
        public string? AskNewPermissionsPassword(string documentName) => _password < passwords.Length ? passwords[_password++] : null;
    }

    private static IDocumentCommand Note(TestHost host)
    {
        var op = Operations.Get("Annot.Note");
        return new DelegateDocumentCommand("Test", (pdf, _, _) => op.Run(host, pdf), DocumentChangeKind.Annotations, op.Permission);
    }

    private static async Task<(TestHost Host, IPdfDocumentSession Session, string Path)> OpenOwnerOnlyAsync(string name)
    {
        var host = new TestHost();
        var enc = await host.Get<ISecurityService>().EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings(null, Owner, PdfEncryptionMethod.Aes256, Perms));
        var path = TestPaths.WriteSample(enc, name);
        var s = (await host.Documents.OpenAsync(path))!;
        Assert.False(s.SecurityInfo.OpenedWithOwnerPassword);
        await s.ExecuteAsync(Note(host));
        return (host, s, path);
    }

    [Fact]
    public async Task Owner_password_keeps_the_original_protection_after_a_wrong_attempt()
    {
        var (host, s, path) = await OpenOwnerOnlyAsync("tylko uprawnienia ą.pdf");
        using var _ = host;
        var dm = (DocumentManager)host.Documents;
        Assert.True(dm.RequiresOwnerPasswordDecision(s));
        var prompts = new Prompts(new[] { OwnerProtectionChoice.EnterOwnerPassword }, "złe hasło", Owner);
        Assert.True(await new OwnerProtectionGuard(host.Documents, prompts).EnsureAsync(s));
        Assert.Equal(new[] { false, true }, prompts.PasswordRetries); // drugie pytanie z komunikatem o złym haśle
        Assert.False(dm.RequiresOwnerPasswordDecision(s));
        await host.Documents.SaveAsync(s);

        var saved = File.ReadAllBytes(path);
        Assert.True(PdfSecurityHelper.IsEncrypted(saved));
        Assert.True(PdfSecurityHelper.IsOwnerPassword(saved, Owner)); // autor nadal zdejmie ograniczenia swoim hasłem
        Assert.Contains("gęślą", await Inspect.PdfiumText(saved)); // nadal bez hasła otwarcia
        Assert.Contains("gęślą", Inspect.PigText(saved));
        var p = await Inspect.Renderer.ProbeAsync(saved);
        Assert.False(p.Permissions.HasFlag(DocumentPermissions.ModifyContent));
        Assert.True(p.Permissions.HasFlag(DocumentPermissions.Annotate));
        Assert.Contains((0, "/Text"), Inspect.Annotations(PdfSecurityHelper.RemoveSecurity(saved, Owner)));

        // wybór obowiązuje dalej – kolejny zapis bez pytania
        Assert.True(await new OwnerProtectionGuard(host.Documents, new Prompts(Array.Empty<OwnerProtectionChoice>())).EnsureAsync(s));
    }

    [Fact]
    public async Task New_permissions_password_replaces_the_unknown_one()
    {
        var (host, s, path) = await OpenOwnerOnlyAsync("nowe hasło ą.pdf");
        using var _ = host;
        const string fresh = "nowe-źdźbło-2";
        Assert.True(await new OwnerProtectionGuard(host.Documents, new Prompts(new[] { OwnerProtectionChoice.NewPermissionsPassword }, fresh)).EnsureAsync(s));
        await host.Documents.SaveAsync(s);
        var saved = File.ReadAllBytes(path);
        Assert.True(PdfSecurityHelper.IsOwnerPassword(saved, fresh));
        Assert.False(PdfSecurityHelper.IsOwnerPassword(saved, Owner));
        Assert.Contains("/AESV3", Inspect.Latin1(saved));
        Assert.Contains("gęślą", await Inspect.PdfiumText(saved));
        var p = await Inspect.Renderer.ProbeAsync(saved);
        Assert.False(p.Permissions.HasFlag(DocumentPermissions.ModifyContent));
        Assert.True(p.Permissions.HasFlag(DocumentPermissions.Print));
    }

    [Fact]
    public async Task Save_without_protection_writes_plain_pdf_and_cancel_writes_nothing()
    {
        var (host, s, path) = await OpenOwnerOnlyAsync("bez ochrony ą.pdf");
        using var _ = host;
        var before = File.ReadAllBytes(path);

        // Anuluj (także po Anuluj w oknie hasła – powrót do wyboru, potem Anuluj)
        var cancel = new Prompts(new[] { OwnerProtectionChoice.EnterOwnerPassword, OwnerProtectionChoice.Cancel });
        Assert.False(await new OwnerProtectionGuard(host.Documents, cancel).EnsureAsync(s));
        Assert.Equal(2, cancel.ChoiceCalls);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(((DocumentManager)host.Documents).RequiresOwnerPasswordDecision(s));

        Assert.True(await new OwnerProtectionGuard(host.Documents, new Prompts(new[] { OwnerProtectionChoice.NoProtection })).EnsureAsync(s));
        await host.Documents.SaveAsync(s);
        var saved = File.ReadAllBytes(path);
        Assert.False(PdfSecurityHelper.IsEncrypted(saved));
        Assert.Contains((0, "/Text"), Inspect.Annotations(saved));
    }

    [Fact]
    public async Task No_question_for_files_with_open_password_or_opened_by_owner()
    {
        using var host = new TestHost();
        var guard = new OwnerProtectionGuard(host.Documents, new Prompts(Array.Empty<OwnerProtectionChoice>()));
        var dm = (DocumentManager)host.Documents;
        // hasło otwarcia (D-08: nowe losowe hasło właściciela, ale odbiorca i tak potrzebuje hasła otwarcia)
        var user = (await host.Documents.OpenAsync(TestPaths.WriteSample(SamplePdfFactory.EncryptedAes256(), "hasło otwarcia ą.pdf"), SamplePdfFactory.UserPassword))!;
        Assert.False(dm.RequiresOwnerPasswordDecision(user));
        Assert.True(await guard.EnsureAsync(user));
        // tylko hasło uprawnień, ale otwarte hasłem właściciela
        var enc = await host.Get<ISecurityService>().EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings(null, Owner, PdfEncryptionMethod.Aes256, Perms));
        var owner = (await host.Documents.OpenAsync(TestPaths.WriteSample(enc, "właściciel ą.pdf"), Owner))!;
        Assert.False(dm.RequiresOwnerPasswordDecision(owner));
        // bez szyfrowania
        var plain = (await host.Documents.OpenAsync(TestPaths.WriteSample(SamplePdfFactory.Text(1), "zwykły ą.pdf")))!;
        Assert.False(dm.RequiresOwnerPasswordDecision(plain));
    }

    [Theory]
    [InlineData("pl", PdfEncryptionMethod.Rc4_128, "Szyfrowanie RC4 128-bitowe zastąpiono AES-256")]
    [InlineData("en", PdfEncryptionMethod.Rc4_128, "Encryption RC4 128-bit was replaced with AES-256")]
    [InlineData("pl", PdfEncryptionMethod.Aes256, null)]
    public async Task Status_after_save_tells_about_reencryption(string culture, PdfEncryptionMethod method, string? expected)
    {
        using var host = new TestHost();
        var loc = host.Get<ILocalizationService>();
        loc.RegisterResources(new System.Resources.ResourceManager("ZetPDF.App.Resources.Strings", typeof(ZetPDF.App.ModuleCatalog).Assembly));
        loc.SetCulture(new System.Globalization.CultureInfo(culture));
        var enc = await host.Get<ISecurityService>().EncryptAsync(SamplePdfFactory.Text(1), new PdfEncryptionSettings("user1", "owner1", method, Perms));
        var s = (await host.Documents.OpenAsync(TestPaths.WriteSample(enc, "rc4 ą.pdf"), "user1"))!;
        var text = DocumentActions.SavedStatus(loc, host.Documents, s, "rc4 ą.pdf");
        Assert.Contains("rc4 ą.pdf", text);
        if (expected is null) Assert.DoesNotContain("AES", text);
        else Assert.Contains(expected, text);
    }
}

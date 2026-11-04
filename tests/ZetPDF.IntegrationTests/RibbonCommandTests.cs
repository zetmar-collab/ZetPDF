using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>
/// Prawdziwe polecenia wstążki (moduły po Initialize, atrapy powłoki i dialogów potwierdzające) na dokumencie
/// otwartym z polskiej ścieżki: wynik w pliku, Cofnij = identyczne bajty, Ponów = identyczne bajty.
/// Polecenia z oknem parametrów są tu pomijane (okno w atrapie = Anuluj) – ich silniki sprawdzają inne testy.
/// </summary>
public class RibbonCommandTests
{
    private sealed class Ctx : IDisposable
    {
        public Ctx(byte[] sample, string? password = null)
        {
            Host = new TestHost(initializeModules: true);
            Sample = sample;
            Password = password;
        }

        public TestHost Host { get; }
        public byte[] Sample { get; }
        public string? Password { get; }
        public IPdfDocumentSession Session { get; private set; } = null!;
        public IDocumentView? View { get; private set; }
        public ICommandRegistry Registry => Host.Get<ICommandRegistry>();

        public async Task OpenAsync(bool withView = true)
        {
            var path = TestPaths.WriteSample(Sample, "wstążka zażółć.pdf");
            Session = (await Host.Documents.OpenAsync(path, Password))!;
            Host.Documents.ActiveDocument = Session;
            if (withView)
            {
                View = Host.Get<IDocumentViewFactory>().CreateView(Session);
                Sta.Layout(View.Element);
                await Sta.WaitFor(() => View.GetPageTransform(0) is not null, 30000, "widok strony");
            }
        }

        /// <summary>Wykonuje polecenie i czeka, aż handler znów będzie gotowy (ActiveDocumentCommand blokuje się na czas pracy).</summary>
        public async Task<bool> RunAsync(string id, int timeoutMs = 120000, Func<bool>? done = null)
        {
            var handler = Registry.GetHandler(id);
            Assert.True(handler is not null, $"{id}: brak obsługi");
            Assert.True(handler!.CanExecute(null), $"{id}: CanExecute=false");
            var before = Session.Current;
            Assert.True(Registry.TryExecute(id), id);
            var start = Environment.TickCount64;
            await Task.Delay(30);
            done ??= () => handler.CanExecute(null);
            while (!done() && Environment.TickCount64 - start < timeoutMs) await Task.Delay(30);
            Assert.True(done(), $"{id}: polecenie nie zakończyło się w {timeoutMs} ms");
            Assert.True(Host.Dialogs.Errors.Count == 0, $"{id}: błąd: {string.Join(" | ", Host.Dialogs.Errors.Select(e => e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace))}");
            return !ReferenceEquals(before, Session.Current);
        }

        public async Task AssertUndoRedoAsync(byte[] before, byte[] after)
        {
            await Session.History.UndoAsync();
            Assert.Equal(before, Session.Current.GetBytes());
            await Session.History.RedoAsync();
            Assert.Equal(after, Session.Current.GetBytes());
        }

        public void Dispose()
        {
            View?.Dispose();
            Host.Dispose();
        }
    }

    private static void Run(byte[] sample, Func<Ctx, Task> body, string? password = null, bool withView = true) => Sta.Run(async () =>
    {
        using var ctx = new Ctx(sample, password);
        await ctx.OpenAsync(withView);
        await body(ctx);
    });

    private static byte[] Prepare(Func<TestHost, Task<byte[]>> f)
    {
        using var h = new TestHost();
        return Task.Run(() => f(h)).GetAwaiter().GetResult();
    }

    private static async Task Check(Ctx c, string id, Func<byte[], Task> verify)
    {
        var before = c.Session.Current.GetBytes();
        Assert.True(await c.RunAsync(id), $"{id}: dokument się nie zmienił; komunikaty: {string.Join(" | ", c.Host.Dialogs.Messages)}");
        var after = c.Session.Current.GetBytes();
        await verify(after);
        Assert.NotNull(c.Session.History.UndoDescriptionKey);
        await c.AssertUndoRedoAsync(before, after);
    }

    [Fact]
    public void Pages_rotate_right_and_duplicate() => Run(SamplePdfFactory.Text(2), async c =>
    {
        await Check(c, CommandIds.Pages.RotateRight, pdf => { Assert.Equal(90, Inspect.Read(pdf, d => d.Pages[0].Rotate)); return Task.CompletedTask; });
        await Check(c, CommandIds.Pages.Duplicate, pdf => { Assert.Equal(3, Inspect.PageCount(pdf)); return Task.CompletedTask; });
    });

    [Fact]
    public void Remove_all_watermarks() => Run(Prepare(h => h.Get<IStampingService>().AddWatermarkAsync(SamplePdfFactory.Text(1), new WatermarkOptions { Text = "KOPIA" })), async c =>
    {
        await Check(c, CommandIds.Stamp.WatermarkRemoveAll, async pdf =>
        {
            Assert.False((await c.Host.Get<IStampingService>().DetectMarksAsync(pdf)).HasFlag(PageMarkKind.Watermark));
            Assert.DoesNotContain("KOPIA", Inspect.PigText(pdf));
        });
    });

    [Fact]
    public void Delete_all_comments() => Run(SamplePdfFactory.WithAnnotations(), c =>
        Check(c, CommandIds.Comment.DeleteAll, pdf => { Assert.Empty(Inspect.Annotations(pdf)); return Task.CompletedTask; }));

    [Fact]
    public void Reset_form() => Run(Prepare(h => h.Get<IFormService>().SetValuesAsync(SamplePdfFactory.WithForm(), new Dictionary<string, string> { ["imie"] = "Żaneta" })), c =>
        Check(c, CommandIds.Form.Reset, async pdf =>
        {
            var f = await c.Host.Get<IFormService>().GetFieldsAsync(pdf);
            Assert.Equal(string.Empty, f.Single(x => x.FullName == "imie").Value);
        }));

    [Fact]
    public void Create_and_remove_links_from_urls() => Run(ExtraSamples.WithUrls(), async c =>
    {
        await Check(c, CommandIds.Edit.CreateLinksFromUrls, async pdf => Assert.Equal(2, (await c.Host.Get<ILinkService>().GetLinksAsync(pdf)).Count));
        await Check(c, CommandIds.Edit.RemoveAllLinks, async pdf => Assert.Empty(await c.Host.Get<ILinkService>().GetLinksAsync(pdf)));
    });

    [Fact]
    public void Bookmarks_from_headings() => Run(SamplePdfFactory.Text(3), c =>
        Check(c, CommandIds.Edit.BookmarksFromHeadings, async pdf =>
        {
            var tree = await c.Host.Get<IBookmarkService>().GetBookmarksAsync(pdf);
            Assert.Contains(Operations.Flatten(tree), b => b.Title == "Strona 2" && b.PageIndex == 1);
        }));

    [Fact]
    public void Quick_ocr_on_scan() => Run(SamplePdfFactory.Scanned(), c =>
        Check(c, CommandIds.Ocr.QuickRecognize, pdf => { Assert.Contains("Faktura", Inspect.PigText(pdf)); return Task.CompletedTask; }));

    [Fact]
    public void Apply_redactions() => Run(ExtraSamples.WithLiteralText(new[] { "Jawne", ExtraSamples.SecretPhrase }), async c =>
    {
        var red = c.Host.Get<IRedactionService>();
        await c.Session.ExecuteAsync(new DelegateDocumentCommand("Test.Mark", async (pdf, _, ct) =>
        {
            var hits = await red.FindAsync(pdf, new RedactionSearch { Phrases = new[] { ExtraSamples.SecretPhrase } }, null, ct);
            return await red.MarkAsync(pdf, hits.Select(h => new RedactionMark(h.PageIndex, h.Quads)).ToList(), ct);
        }));
        await Check(c, CommandIds.Redact.Apply, pdf => { Assert.False(Inspect.ContainsAnywhere(pdf, ExtraSamples.SecretPhrase)); return Task.CompletedTask; });
        Assert.Contains(c.Host.Dialogs.Confirmations, t => t.Length > 0); // ostrzeżenie o nieodwracalności
    });

    [Fact]
    public void Remove_security_from_aes256_document() => Run(SamplePdfFactory.EncryptedAes256(), async c =>
    {
        Assert.NotNull(c.Session.Current.Encryption);
        Assert.True(await c.RunAsync(CommandIds.Security.RemoveSecurity, done: () => c.Session.Current.Encryption is null));
        Assert.Null(c.Session.Current.Encryption);
        var outPath = Path.Combine(TestPaths.NewDirectory(), "bez hasła ą.pdf");
        await c.Host.Documents.SaveAsync(c.Session, outPath);
        Assert.False(PdfSecurityHelper.IsEncrypted(File.ReadAllBytes(outPath)));
        await c.Session.History.UndoAsync();
        Assert.Equal(PdfEncryptionMethod.Aes256, c.Session.Current.Encryption!.Method);
    }, password: SamplePdfFactory.OwnerPassword);

    [Fact]
    public void Commands_are_disabled_without_permission_for_user_password_document() => Run(SamplePdfFactory.EncryptedAes256(), c =>
    {
        // EncryptedAes256: bez modyfikacji treści i kopiowania
        foreach (var id in new[] { CommandIds.Stamp.WatermarkAdd, CommandIds.Redact.Apply, CommandIds.Edit.AddImage, CommandIds.Convert.Grayscale, CommandIds.Sanitize.Document, CommandIds.Pages.Crop, CommandIds.Edit.FindReplace })
        {
            var h = c.Registry.GetHandler(id);
            Assert.True(h is not null, id);
            Assert.False(h!.CanExecute(null), $"{id} aktywne mimo braku uprawnień");
        }
        Assert.True(c.Registry.GetHandler(CommandIds.Comment.DeleteAll)!.CanExecute(null)); // komentarze dozwolone
        // Ochrona hasłem / Usuń zabezpieczenia są aktywne celowo – pytają o hasło uprawnień (docs/ograniczenia/security-signatures.md)
        return Task.CompletedTask;
    }, password: SamplePdfFactory.UserPassword);

    /// <summary>
    /// Każde polecenie z obsługą: uruchomienie przy otwartym dokumencie i atrapie dialogów (okna = Anuluj, potwierdzenia = Tak)
    /// nie kończy się błędem (IDialogService.ShowError), nie otwiera okien z pominięciem IDialogService i nie zawiesza się.
    /// </summary>
    [Fact]
    public void Every_registered_command_runs_without_error_with_cancelled_dialogs() => Run(SamplePdfFactory.WithForm(), async c =>
    {
        var skip = new HashSet<string> { CommandIds.File.Exit };
        var failures = new List<string>();
        foreach (var d in c.Registry.Descriptors)
        {
            if (skip.Contains(d.Id)) continue;
            var h = c.Registry.GetHandler(d.Id);
            if (h is null || !h.CanExecute(null)) continue;
            c.Host.Dialogs.Errors.Clear();
            try
            {
                c.Registry.TryExecute(d.Id);
                var start = Environment.TickCount64;
                await Task.Delay(20);
                while (!h.CanExecute(null) && Environment.TickCount64 - start < 30000) await Task.Delay(20);
                if (!h.CanExecute(null)) failures.Add($"{d.Id}: nie zakończyło się w 30 s");
            }
            catch (Exception ex) { failures.Add($"{d.Id}: wyjątek {ex.GetType().Name}: {ex.Message}"); }
            foreach (var e in c.Host.Dialogs.Errors) failures.Add($"{d.Id}: ShowError {e.GetType().Name}: {e.Message}");
            // okna niemodalne przeszły przez IDialogService: atrapa ich nie pokazuje, kolejne polecenia nie mogą ich widzieć jako otwartych
            foreach (var w in c.Host.Dialogs.ModelessShown) w.Close();
            c.Host.Dialogs.ModelessShown.Clear();
            // okna pokazane bez IDialogService (np. Window.Show) – zamknij i zgłoś; CurrentSources obejmuje wszystkie wątki,
            // a inne testy działają równolegle na własnych wątkach STA, więc liczą się tylko okna tego wątku
            foreach (var w in PresentationSource.CurrentSources.OfType<HwndSource>().Where(s => s.CheckAccess()).Select(s => s.RootVisual).OfType<Window>().ToList())
            {
                failures.Add($"{d.Id}: otwarte okno {w.GetType().Name} z pominięciem IDialogService");
                w.Close();
            }
            c.Host.Get<IToolService>().ActivateDefault();
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    });
}

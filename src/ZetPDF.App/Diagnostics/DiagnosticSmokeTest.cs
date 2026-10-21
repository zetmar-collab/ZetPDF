using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.App.Shell;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.App.Diagnostics;

/// <summary>
/// Test dymny w prawdziwym exe, bez dostępu do pulpitu (np. zablokowana sesja):
/// <c>ZetPDF.exe "plik.pdf" --diag-smoke &lt;folder&gt;</c>. Otwiera plik przez te same akcje co powłoka, wykonuje po jednym
/// poleceniu z każdej karty wstążki przez rejestr poleceń (jak kliknięcie), okna dialogowe, które się pojawią,
/// zapisuje do PNG i zamyka (= Anuluj), przełącza język i motyw, zapisuje dokument jako nowy plik, otwiera go ponownie
/// i zapisuje raport <c>smoke.txt</c>. Nie wysyła niczego w sieć; ustawienia języka i motywu przywraca.
/// </summary>
internal static class DiagnosticSmokeTest
{
    public static async Task RunAsync(IServiceProvider sp, MainWindow window, string folder, IReadOnlyList<string> args)
    {
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        void Log(string line) { report.AppendLine(line); }
        var registry = sp.GetRequiredService<ICommandRegistry>();
        var docs = sp.GetRequiredService<IDocumentManager>();
        var actions = sp.GetRequiredService<DocumentActions>();
        var loc = sp.GetRequiredService<ILocalizationService>();
        var theme = sp.GetRequiredService<IThemeService>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var originalTheme = settings.Current.Theme;
        var originalLanguage = settings.Current.Language;
        int dialogNo = 0, failures = 0;

        async Task Settle(int ms = 300)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(ms);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        void Png(Visual v, string name)
        {
            var fe = (FrameworkElement)v;
            var dpi = VisualTreeHelper.GetDpi(v);
            int w = (int)Math.Ceiling(fe.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(fe.ActualHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0) return;
            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(v);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var f = File.Create(Path.Combine(folder, name + ".png"));
            enc.Save(f);
        }

        // Okna dialogowe otwarte przez polecenia: zrzut + zamknięcie (Anuluj), żeby test się nie zatrzymał.
        var handled = new HashSet<Window>();
        var closer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
        closer.Tick += (_, _) =>
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (ReferenceEquals(w, window) || !w.IsVisible || !w.IsLoaded || !handled.Add(w)) continue;
                var name = $"dialog{++dialogNo:00}";
                try { w.UpdateLayout(); Png(w, name); } catch (Exception) { /* zrzut nieistotny dla testu */ }
                Log($"    okno: „{w.Title}” ({w.GetType().Name}) → {name}.png, zamknięte (Anuluj)");
                w.Close();
            }
        };
        closer.Start();

        async Task Run(string tab, string commandId, bool expectChange = false)
        {
            var d = docs.ActiveDocument;
            long before = d?.Current.Version ?? 0;
            var handler = registry.GetHandler(commandId);
            bool can = handler?.CanExecute(null) == true;
            bool ok = can && registry.TryExecute(commandId);
            var start = Environment.TickCount64;
            while (expectChange && ok && d is not null && d.Current.Version == before && Environment.TickCount64 - start < 60000) await Task.Delay(100);
            await Settle(expectChange ? 300 : 1200);
            bool changed = d is not null && d.Current.Version != before;
            string state = !can ? "NIEDOSTĘPNE" : ok ? (changed ? "wykonane, dokument zmieniony" : "wykonane") : "BŁĄD wykonania";
            if (!ok || (expectChange && !changed)) failures++;
            Log($"[{tab}] {commandId} ({loc["Cmd." + commandId]}): {state}{(expectChange && !changed ? " – OCZEKIWANO ZMIANY" : string.Empty)}");
        }

        try
        {
            var (files, password) = App.ParseArgs(args);
            Log($"ZetPDF {Licenses.ThirdPartyLibraries.AppVersion} – test dymny exe {DateTime.Now:yyyy-MM-dd HH:mm}");
            Log($"Pliki z linii poleceń: {string.Join("; ", files)}");
            if (files.Count == 0) { Log("BRAK pliku – przerwano"); return; }
            await actions.OpenAsync(files, password);
            await Settle(1500);
            var doc = docs.ActiveDocument;
            if (doc is null) { Log("BŁĄD: dokument się nie otworzył"); failures++; return; }
            int pagesBefore;
            using (var r = await sp.GetRequiredService<IPdfRenderer>().OpenAsync(doc.Current.GetBytes())) pagesBefore = r.PageCount;
            Log($"Otwarto: {doc.FilePath} ({pagesBefore} str., zaszyfrowany={doc.SecurityInfo.IsEncrypted})");
            Png(window, "smoke-01-otwarty");

            await Run("Początek", CommandIds.View.ZoomIn);
            await Run("Początek", CommandIds.Tool.SelectText);
            await Run("Konwertuj", CommandIds.Ocr.QuickRecognize);
            await Run("Edytuj", CommandIds.Edit.CreateLinksFromUrls);
            await Run("Organizuj", CommandIds.Pages.RotateRight, expectChange: true);
            await Run("Organizuj", CommandIds.Pages.Duplicate, expectChange: true);
            await Run("Komentarz", CommandIds.Comment.HideAll);
            await Run("Komentarz", CommandIds.Comment.ShowAll);
            await Run("Widok", CommandIds.View.TwoPages);
            await Run("Widok", CommandIds.View.RotateRight);
            await Run("Formularz", CommandIds.Form.HighlightFields);
            await Run("Formularz", CommandIds.Form.Reset);
            await Run("Ochrona", CommandIds.Security.Properties);
            await Run("Pomoc", CommandIds.Help.About);
            await Run("Plik", CommandIds.File.Properties);
            await Run("Cofnij", CommandIds.Edit.Undo, expectChange: true);
            await Run("Ponów", CommandIds.Edit.Redo, expectChange: true);
            Png(window, "smoke-02-po-poleceniach");

            foreach (var (lang, th) in new[] { ("en", AppTheme.Dark), ("pl", AppTheme.Light) })
            {
                loc.SetCulture(LocalizationService.ResolveCulture(lang));
                theme.SetTheme(th);
                await Settle(600);
                Png(window, $"smoke-03-{lang}-{th.ToString().ToLowerInvariant()}");
                Log($"Przełączono: język={loc.CurrentCulture.Name}, motyw={theme.EffectiveTheme}; etykieta Zapisz = „{loc["Cmd.File.Save"]}”");
            }

            int pagesExpected;
            using (var r = await sp.GetRequiredService<IPdfRenderer>().OpenAsync(doc.Current.GetBytes())) pagesExpected = r.PageCount;
            var target = Path.Combine(Path.GetDirectoryName(doc.FilePath!)!, Path.GetFileNameWithoutExtension(doc.FilePath) + " – zapisany przez exe.pdf");
            if (File.Exists(target)) File.Delete(target);
            if (!await sp.GetRequiredService<OwnerProtectionGuard>().EnsureAsync(doc)) { Log("Zapis anulowany w oknie ochrony"); failures++; return; }
            await docs.SaveAsync(doc, target);
            Log($"Zapisano jako: {target} ({new FileInfo(target).Length} B), zmodyfikowany po zapisie={doc.IsModified}");
            if (doc.IsModified) failures++;
            docs.Close(doc);
            await actions.OpenAsync(new[] { target }, password);
            await Settle(1500);
            var reopened = docs.ActiveDocument;
            int pagesAfter = 0;
            if (reopened is not null)
                using (var r = await sp.GetRequiredService<IPdfRenderer>().OpenAsync(reopened.Current.GetBytes())) pagesAfter = r.PageCount;
            bool same = reopened?.FilePath == Path.GetFullPath(target) && pagesAfter == pagesExpected;
            if (!same) failures++;
            Log($"Ponowne otwarcie: {(same ? "OK" : "BŁĄD")} – stron {pagesAfter} (oczekiwano {pagesExpected}, przed zmianami {pagesBefore})");
            Png(window, "smoke-04-ponownie-otwarty");
        }
        catch (Exception ex)
        {
            failures++;
            Log("WYJĄTEK: " + loc.Describe(ex) + " | " + LocalizedError.TechnicalDetails(ex));
        }
        finally
        {
            closer.Stop();
            Log(failures == 0 ? "WYNIK: OK" : $"WYNIK: {failures} problemów");
            File.WriteAllText(Path.Combine(folder, "smoke.txt"), report.ToString(), new UTF8Encoding(true));
            theme.SetTheme(originalTheme);
            settings.Update(s => s.Language = originalLanguage);
            foreach (var d in docs.Documents.ToArray()) docs.Close(d);
            window.Close();
        }
    }
}

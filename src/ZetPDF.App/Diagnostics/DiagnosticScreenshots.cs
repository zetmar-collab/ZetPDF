using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.App.Dialogs;
using ZetPDF.App.Shell;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;

namespace ZetPDF.App.Diagnostics;

/// <summary>
/// Diagnostyka wyglądu (dla testów/recenzji, bez sieci): <c>ZetPDF.exe --diag-screenshots &lt;folder&gt;</c>
/// renderuje okno główne i okna dialogowe (RenderTargetBitmap) w motywie jasnym/ciemnym i po polsku/angielsku,
/// zapisuje PNG i zamyka program. Nie zmienia zapisanych ustawień użytkownika (przywraca motyw i język).
/// </summary>
internal static class DiagnosticScreenshots
{
    public static async Task RunAsync(IServiceProvider sp, MainWindow window, string folder, IReadOnlyList<string> args)
    {
        Directory.CreateDirectory(folder);
        var theme = sp.GetRequiredService<IThemeService>();
        var loc = sp.GetRequiredService<ILocalizationService>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var originalTheme = settings.Current.Theme;
        var originalLanguage = settings.Current.Language;

        async Task Settle() => await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        async Task Shot(Visual v, string name)
        {
            await Settle();
            await Task.Delay(400);
            await Settle();
            var fe = (FrameworkElement)v;
            var dpi = VisualTreeHelper.GetDpi(v);
            var w = (int)Math.Ceiling(fe.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Ceiling(fe.ActualHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0) return;
            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(v);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            await using var f = File.Create(Path.Combine(folder, name + ".png"));
            enc.Save(f);
        }

        async Task Dialog(Window dlg, string name)
        {
            dlg.Owner = window;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dlg.ShowActivated = false;
            Services.WindowThemer.Prepare(dlg);
            dlg.Show();
            await Shot(dlg, name);
            dlg.Close();
        }

        var docs = sp.GetRequiredService<IDocumentManager>();
        var registry = sp.GetRequiredService<ICommandRegistry>();
        async Task Stage3Async(string tag)
        {
            var ribbon = window.Ribbon;
            var tab = ribbon.Tabs.FirstOrDefault(t => Equals(t.Header, loc["Ribbon.Tab.Accessibility"]));
            if (tab is not null) ribbon.SelectedTabIndex = ribbon.Tabs.IndexOf(tab);
            if (docs.ActiveDocument is not null)
            {
                registry.TryExecute(ZetPDF.Core.Commands.CommandIds.Access.FullCheck);
                await Task.Delay(1500);
            }
            await Shot(window, "tab-accessibility-" + tag);
            registry.TryExecute(ZetPDF.Core.Commands.CommandIds.Index.Search);
            await Shot(window, "panel-advanced-search-" + tag);
            if (docs.ActiveDocument is not null && registry.TryExecute(ZetPDF.Core.Commands.CommandIds.Read.Activate))
            {
                await Settle();
                await Task.Delay(500);
                foreach (Window w in Application.Current.Windows)
                    if (w.GetType().Name == "ReadAloudPanel" && w.IsVisible) await Shot(w, "read-aloud-panel-" + tag);
                registry.TryExecute(ZetPDF.Core.Commands.CommandIds.Read.Activate);
            }
        }

        var samples = new List<IPdfDocumentSession>();
        try
        {
            // Pliki z linii poleceń (np. przykładowy PDF z polską ścieżką) – otwierane przez te same akcje co zwykle.
            var files = App.ParseArgs(args).Files;
            if (files.Count > 0) await sp.GetRequiredService<DocumentActions>().OpenAsync(files);
            await Shot(window, "start");
            // Próbne dokumenty i panel, żeby zobaczyć karty, pasek stanu i pasek paneli.
            foreach (var name in new[] { "zażółć gęślą.pdf", "Raport roczny 2026.pdf" })
            {
                using var pdf = new PdfSharp.Pdf.PdfDocument();
                pdf.AddPage();
                pdf.AddPage();
                using var ms = new MemoryStream();
                pdf.Save(ms);
                samples.Add(await docs.CreateNewAsync(ms.ToArray(), name));
            }
            if (docs.Documents.FirstOrDefault(d => d.FilePath is not null) is { } real) docs.ActiveDocument = real;
            await Settle();
            foreach (var (lang, th) in new[] { ("pl", AppTheme.Light), ("pl", AppTheme.Dark), ("en", AppTheme.Dark), ("en", AppTheme.Light) })
            {
                loc.SetCulture(LocalizationService.ResolveCulture(lang));
                theme.SetTheme(th);
                var tag = $"{lang}-{th.ToString().ToLowerInvariant()}";
                await Shot(window, "main-" + tag);
                var vm = new SettingsViewModel(settings, loc, theme);
                var sw = new SettingsWindow(vm, sp.GetRequiredService<IDialogService>(), loc, sp.GetRequiredService<IAppPaths>(), () => { });
                await Dialog(sw, "settings-" + tag);
                await Dialog(new AboutWindow(loc, _ => { }), "about-" + tag);
                await Dialog(new ShortcutsWindow(sp.GetRequiredService<ICommandRegistry>(), loc, settings), "shortcuts-" + tag);
                await Dialog(new MessageWindow(loc["Shell.AppTitle"], loc.Format("Shell.Close.SavePrompt", "zażółć gęślą.pdf"), null, true,
                    new[] { new MessageWindow.ButtonSpec(loc["Core.Button.Save"], 1, true), new MessageWindow.ButtonSpec(loc["Shell.Close.DontSave"], 2), new MessageWindow.ButtonSpec(loc["Core.Button.Cancel"], 0, false, true) }, 0),
                    "message-" + tag);
                // DECYZJE D-23 – wybór ochrony przed zapisem (4 przyciski, układ pionowy) i okno błędu z kluczem (O-3)
                await Dialog(new MessageWindow(loc["Shell.OwnerOnly.Title"], loc.Format("Shell.OwnerOnly.Text", "zażółć gęślą.pdf"), MessageSeverity.Warning, false,
                    new[] { new MessageWindow.ButtonSpec(loc["Shell.OwnerOnly.EnterOwner"], 1, true), new MessageWindow.ButtonSpec(loc["Shell.OwnerOnly.NewPassword"], 2),
                            new MessageWindow.ButtonSpec(loc["Shell.OwnerOnly.NoProtection"], 3), new MessageWindow.ButtonSpec(loc["Core.Button.Cancel"], 0, false, true) }, 0),
                    "owner-only-" + tag);
                await Dialog(new MessageWindow(loc["Core.Error.Title"], loc.Format("Core.Error.PageRangeOutside", "2-9", 3), MessageSeverity.Error, false,
                    new[] { new MessageWindow.ButtonSpec(loc["Core.Button.Ok"], 1, true, true) }, 1), "error-" + tag);
                // każda widoczna karta wstążki
                var ribbon = window.Ribbon;
                for (int i = 0; i < ribbon.Tabs.Count; i++)
                {
                    if (ribbon.Tabs[i].Visibility != Visibility.Visible) continue;
                    ribbon.SelectedTabIndex = i;
                    await Shot(window, $"tab{i}-{tag}");
                }
                // etap 3: karta Ułatwienia dostępu z panelem wyników, panel wyszukiwania zaawansowanego, panel czytania
                await Stage3Async(tag);
                ribbon.SelectedTabIndex = 0;
            }
        }
        finally
        {
            // Próbne dokumenty zamykane bez pytania o zapis (wcześniej proces czekał na okno „Zapisać zmiany?”).
            foreach (var d in samples) docs.Close(d);
            theme.SetTheme(originalTheme);
            settings.Update(s => s.Language = originalLanguage);
            window.Close();
        }
    }
}

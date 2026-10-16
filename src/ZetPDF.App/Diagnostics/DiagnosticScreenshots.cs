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
    public static async Task RunAsync(IServiceProvider sp, MainWindow window, string folder)
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

        try
        {
            await Shot(window, "start");
            // Próbne dokumenty i panel, żeby zobaczyć karty, pasek stanu i pasek paneli.
            var docs = sp.GetRequiredService<IDocumentManager>();
            foreach (var name in new[] { "zażółć gęślą.pdf", "Raport roczny 2026.pdf" })
            {
                using var pdf = new PdfSharp.Pdf.PdfDocument();
                pdf.AddPage();
                pdf.AddPage();
                using var ms = new MemoryStream();
                pdf.Save(ms);
                await docs.CreateNewAsync(ms.ToArray(), name);
            }
            sp.GetRequiredService<IPanelRegistry>().Register(new PanelDescriptor(PanelIds.Bookmarks, "Cmd.View.PanelBookmarks", "", 20,
                _ => new System.Windows.Controls.TextBlock { Text = "…", Margin = new Thickness(12) }));
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
            }
        }
        finally
        {
            theme.SetTheme(originalTheme);
            settings.Update(s => s.Language = originalLanguage);
            window.Close();
        }
    }
}

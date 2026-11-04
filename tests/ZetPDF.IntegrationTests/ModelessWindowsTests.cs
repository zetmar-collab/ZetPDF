using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ZetPDF.App.Services;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.IntegrationTests.Infrastructure;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests;

/// <summary>Okna niemodalne przez IDialogService i kalkulator księgowy, który zostaje otwarty przy zaznaczaniu tekstu w dokumencie.</summary>
public class ModelessWindowsTests
{
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var n in Find<T>(child)) yield return n;
        }
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void Calculator_stays_open_and_adds_selection_from_the_active_document() => Sta.Run(async () =>
    {
        using var host = new TestHost(withShellFakes: true, initializeModules: true);
        var loc = host.Get<ILocalizationService>();
        var registry = host.Get<ICommandRegistry>();
        var pdf = SamplePdfFactory.Text(1);
        var session = await host.Documents.CreateNewAsync(pdf, "liczby.pdf");
        var view = host.Get<IDocumentViewFactory>().CreateView(session);
        try
        {
            Sta.Layout(view.Element);
            await Sta.WaitFor(() => view.GetPageTransform(0) is not null, 20000, "strona");
            var viewer = host.Get<IViewerService>();
            await Sta.WaitFor(() => viewer.ActiveView is not null, 20000, "widok aktywny");

            // bez zaznaczenia: okno otwiera się bez blokowania i bez okna modalnego
            Assert.True(registry.TryExecute(CommandIds.Measure.Calculator));
            var window = Assert.Single(host.Dialogs.ModelessShown);
            Assert.Equal("CalculatorWindow", window.GetType().Name);
            Assert.DoesNotContain(host.Dialogs.DialogsShown, t => t.Name == "CalculatorWindow");
            var add = Find<Button>(window).First(b => (string)b.Content == loc["Annot.Calc.FromSelection"]);
            Click(add);
            Assert.Contains(Find<TextBlock>(window), t => t.Text == loc["Annot.Calc.NoSelection"]);

            // zaznaczenie w dokumencie PRZY otwartym oknie, potem przycisk
            Assert.True(registry.TryExecute(CommandIds.Edit.SelectAll));
            await Sta.WaitFor(() => view.TextSelection is not null, 20000, "zaznaczenie");
            var selected = view.TextSelection!.Text;
            Click(add);
            var status = Find<TextBlock>(window).Select(t => t.Text).ToList();
            Assert.DoesNotContain(loc["Annot.Calc.NoSelection"], status);
            Assert.False(string.IsNullOrWhiteSpace(selected));

            // ponowne polecenie nie otwiera drugiego okna ani nie liczy tego samego zaznaczenia jeszcze raz
            var before = Find<ListBox>(window).First().Items.Count;
            Assert.True(registry.TryExecute(CommandIds.Measure.Calculator));
            Assert.Single(host.Dialogs.ModelessShown);
            Assert.Equal(before, Find<ListBox>(window).First().Items.Count);

            // po zamknięciu polecenie otwiera nowe okno z tą samą taśmą
            window.Close();
            host.Dialogs.ModelessShown.Clear();
            Assert.True(registry.TryExecute(CommandIds.Measure.Calculator));
            var second = Assert.Single(host.Dialogs.ModelessShown);
            Assert.NotSame(window, second);
            Assert.Equal(before, Find<ListBox>(second).First().Items.Count);
            second.Close();
            Assert.Empty(host.Dialogs.Errors);
        }
        finally
        {
            view.Dispose();
        }
    });

    [Fact]
    public void Real_dialog_service_shows_modeless_window_without_blocking() => Sta.Run(async () =>
    {
        using var host = new TestHost(); // ścieżki i dziennik; DialogService tworzony ręcznie, bez powłoki
        var service = new DialogService(host.Get<ILocalizationService>(), host.Get<IAppPaths>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<DialogService>.Instance);
        var window = new Window { Width = 100, Height = 100, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000, Top = -2000 };
        service.ShowModeless(window);          // wraca od razu; przy ShowDialog ten wiersz czekałby na zamknięcie
        Assert.True(window.IsVisible);
        Assert.False(System.Windows.Interop.ComponentDispatcher.IsThreadModal);
        await Dispatcher.Yield(DispatcherPriority.Background);
        window.Close();
        Assert.False(window.IsVisible);
    });

    /// <summary>Uzupełnienie testu „każde polecenie”: ten widzi tylko okna otwarte przy anulowanych dialogach, a źródła pokazują wszystkie wywołania.</summary>
    [Fact]
    public void Modules_show_windows_only_through_dialog_service()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ZetPDF.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var allowed = new[]
        {
            Path.Combine("ZetPDF.Annotations", "UI", "Ui.cs"),         // rezerwa bez IDialogService (jak Ui.Show)
            Path.Combine("ZetPDF.Security", "SecurityCommands.cs"),    // CompareWindow – OGRANICZENIA 27.5
            Path.Combine("ZetPDF.Viewer", "ViewerModule.cs"),          // panel czytania – OGRANICZENIA 27.5
        };
        var call = new Regex(@"\b(?!_?(?:menu|popup|tip|tooltip|overlay|adorner)\b)\w*(?:window|dialog|dlg|panel|w)\.Show\(\)", RegexOptions.IgnoreCase);
        var violations = new List<string>();
        foreach (var module in new[] { "ZetPDF.Annotations", "ZetPDF.Editing", "ZetPDF.Pages", "ZetPDF.Security", "ZetPDF.Viewer", "ZetPDF.Ocr", "ZetPDF.Rendering", "ZetPDF.Core" })
        {
            var root = Path.Combine(dir!.FullName, "src", module);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Path.Combine(dir.FullName, "src"), file);
                if (rel.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || rel.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || allowed.Contains(rel)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (call.IsMatch(lines[i])) violations.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }
}

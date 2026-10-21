using System.Runtime.ExceptionServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.Editing.Accessibility;
using ZetPDF.Editing.Indexing;
using ZetPDF.Editing.Ui;
using ZetPDF.Editing.Ui.Accessibility;
using ZetPDF.Editing.Ui.Indexing;
using ZetPDF.TestData;

namespace ZetPDF.Editing.Tests;

/// <summary>Okna i panele etapu 3 budują się i układają bez błędów (WPF na wątku STA, bez okna głównego).</summary>
public class Stage3UiTests
{
    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Layout(FrameworkElement e)
    {
        e.Measure(new Size(320, 700));
        e.Arrange(new Rect(0, 0, 320, 700));
        e.UpdateLayout();
    }

    [Fact]
    public void Accessibility_panel_properties_dialog_and_search_panel_build()
    {
        var services = new ServiceCollection();
        services.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        new Rendering.RenderingModule().ConfigureServices(services);
        new EditingModule().ConfigureServices(services);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ILocalizationService>().RegisterResources(EditingModule.Strings);
        Sta(() =>
        {
            var ctx = new EditingContext(sp);
            var controller = new AccessibilityController(ctx, sp.GetRequiredService<AccessibilityService>());
            var panel = new AccessibilityPanel(ctx, controller);
            Layout(panel);
            var dlg = new AccessPropertiesDialog(ctx.Loc, "Tytuł", "pl-PL", new[] { new AccessFigure(0, 0, "Logo", null), new AccessFigure(1, 2, null, null) }, true);
            Assert.NotNull(dlg.Content);
            dlg.Close();
            var search = new AdvancedSearchPanel(ctx.Loc, new IndexController(ctx, sp.GetRequiredService<FullTextIndex>()));
            Layout(search);
            search.FocusQuery("gęślą");
        });
    }
}

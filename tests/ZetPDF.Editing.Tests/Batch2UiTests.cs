using System.Runtime.ExceptionServices;
using System.Windows;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Editing.PdfA;
using ZetPDF.Editing.Scanning;
using ZetPDF.Editing.Ui.Dialogs;
using ZetPDF.Editing.Ui.PdfA;

namespace ZetPDF.Editing.Tests;

/// <summary>Okna partii 2 budują się i układają bez błędów (WPF na wątku STA); wartości domyślne opcji.</summary>
public class Batch2UiTests
{
    private static LocalizationService Loc()
    {
        var loc = new LocalizationService();
        loc.RegisterResources(EditingModule.Strings);
        return loc;
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    internal static void Layout(Window w)
    {
        var content = (FrameworkElement)w.Content;
        content.Measure(new Size(w.Width, 900));
        content.Arrange(new Rect(content.DesiredSize));
        Assert.True(content.ActualHeight > 0);
        w.Close();
    }

    [Fact]
    public void Export_dialogs_give_their_options() => Sta(() =>
    {
        var xlsx = new ExportXlsxDialog(Loc(), 0, 3);
        Assert.Equal(XlsxSheetMode.PerPage, xlsx.Options.SheetMode);
        Assert.Equal(string.Empty, xlsx.Options.PageRange);
        Layout(xlsx);
        var html = new ExportHtmlDialog(Loc(), 0, 3);
        Assert.False(html.Options.TextOnly);
        Layout(html);
    });

    [Fact]
    public void Pdfa_windows_show_results_and_plan() => Sta(() =>
    {
        var loc = Loc();
        var report = PdfAChecker.Report(new PdfAFacts());
        Layout(new PdfACheckWindow(loc, report, () => Task.CompletedTask));
        var plan = new PdfAConversionPlan
        {
            PagesToRasterize = new[] { 0, 2 }, FontsToEmbed = new[] { "Helvetica → Arial" }, Attachments = 1, AnnotationsToRemove = 2,
            ActionsToRemove = 3, HasXfa = true, IsSigned = true,
        };
        var dlg = new PdfAConvertDialog(loc, plan, encrypted: true);
        Assert.False(dlg.AllowRasterize); // zgoda nie jest domyślna
        Layout(dlg);
    });

    [Fact]
    public void Scanner_dialog_follows_the_feeder_capability() => Sta(() =>
    {
        var withFeeder = new ScannerDialog(Loc(), new[] { new ScannerInfo("a", "Skaner", true) });
        Assert.True(withFeeder.Settings.UseFeeder);
        Assert.Equal(300, withFeeder.Settings.Dpi);
        Assert.Equal(ScanColorMode.Color, withFeeder.Settings.Mode);
        Assert.False(withFeeder.RunOcr);
        Layout(withFeeder);
        var noFeeder = new ScannerDialog(Loc(), new[] { new ScannerInfo("a", "Skaner", false) });
        Assert.False(noFeeder.Settings.UseFeeder);
        Layout(noFeeder);
    });

    [Fact]
    public void Web_dialog_builds_and_has_no_source_until_confirmed() => Sta(() =>
    {
        var dlg = new WebToPdfDialog(Loc(), () => null);
        Assert.Null(dlg.Source);
        Layout(dlg);
    });
}

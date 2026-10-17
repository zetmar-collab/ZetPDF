using System.Windows;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Localization;
using ZetPDF.Pages.Ui;

namespace ZetPDF.Pages.Tests;

/// <summary>Każde okno modułu da się zbudować (wątek STA), a wszystkie teksty są w zasobach (brak "[Klucz]").</summary>
public class DialogSmokeTests
{
    [Fact]
    public void All_dialogs_build_and_have_localized_texts()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                foreach (var culture in new[] { "pl", "en" })
                {
                    var loc = new LocalizationService(null, new System.Globalization.CultureInfo(culture));
                    loc.RegisterResources(PagesModule.Strings);
                    loc.RegisterResources(new System.Resources.ResourceManager("ZetPDF.Core.Resources.Strings", typeof(LocalizationService).Assembly));
                    var d = new Dlg();
                    var ctx = new DocContext(5, 1, new[] { 1, 2 }, PdfSize.A4, "dok.pdf");
                    var windows = new Window[]
                    {
                        new InsertFromFileDialog(loc, d, ctx), new InsertBlankDialog(loc, d, ctx), new InsertBlankDialog(loc, d, null),
                        new RangeDialog(loc, d, ctx, "Pages.Delete.Title", "Pages.Delete.Hint"), new ExtractDialog(loc, d, ctx, null), new ReplaceDialog(loc, d, ctx),
                        new MoveDialog(loc, d, ctx), new SwapDialog(loc, d, ctx), new InterleaveDialog(loc, d), new RotateDialog(loc, d, ctx),
                        new CropDialog(loc, d, ctx), new ResizeDialog(loc, d, ctx), new FlattenDialog(loc, d),
                        new WatermarkDialog(loc, d, ctx, null, false), new WatermarkDialog(loc, d, ctx, new WatermarkOptions { Text = "X", PageRange = "1-2" }, true),
                        new BackgroundDialog(loc, d, ctx, null, false), new HeaderFooterDialog(loc, d, ctx, null, false), new PageNumbersDialog(loc, d, ctx, null, true),
                        new BatesDialog(loc, d, null), new SplitDialog(loc, d, null), new MergeDialog(loc, d, null),
                        new PageLabelsDialog(loc, d, 5, new[] { new PageLabelRange(0, PageLabelStyle.LowerRoman) }), new OptimizeDialog(loc, d, new OptimizeOptions()),
                        new GrayscaleDialog(loc, d), new SanitizeDialog(loc, d),
                    };
                    foreach (var w in windows)
                    {
                        Assert.False(string.IsNullOrEmpty(w.Title));
                        Assert.DoesNotContain("[Pages.", w.Title);
                        foreach (var text in Texts(w)) Assert.DoesNotContain("[Pages.", text);
                        w.Close();
                    }
                }
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is System.Windows.Controls.TextBlock tb && tb.Text is { } s) yield return s;
            if (child is System.Windows.Controls.ContentControl { Content: string c }) yield return c;
            foreach (var x in Texts(child)) yield return x;
        }
    }

    private sealed class Dlg : IDialogService
    {
        public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null) => null;
        public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null) => null;
        public string? SelectFolder(string title, string? initialDirectory = null) => null;
        public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) { }
        public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) => false;
        public YesNoCancel AskYesNoCancel(string text, string? title = null) => YesNoCancel.Cancel;
        public string? AskPassword(string documentName, bool isRetry) => null;
        public Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true) => Task.FromResult(false);
        public bool? ShowDialog(Window dialog) => false;
        public void ShowError(Exception exception, string? contextText = null) { }
    }
}

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Annotations.UI;
using ZetPDF.Annotations.UI.Panels;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>Zapis załącznika przez prawdziwy panel: ostrzeżenie, oczyszczona podpowiedź, zawartość pliku.</summary>
public class AttachmentPanelSaveTests
{
    private sealed class Dialogs : IDialogService
    {
        public string? SaveTo { get; set; }
        public string? Suggested { get; private set; }
        public bool Answer { get; set; } = true;
        public int Warnings { get; private set; }
        public List<Exception> Errors { get; } = new();
        public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null) { Suggested = defaultFileName; return SaveTo; }
        public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) { Warnings++; return Answer; }
        public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null) => null;
        public string? SelectFolder(string title, string? initialDirectory = null) => null;
        public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) { }
        public YesNoCancel AskYesNoCancel(string text, string? title = null) => YesNoCancel.Cancel;
        public string? AskPassword(string documentName, bool isRetry) => null;
        public Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true) => Task.FromResult(true);
        public bool? ShowDialog(Window dialog) => false;
        public void ShowModeless(Window window) { }
        public void ShowError(Exception exception, string? contextText = null) => Errors.Add(exception);
    }

    private static void Sta(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            body().ContinueWith(r => { error = r.Exception?.InnerException; frame.Continue = false; }, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void Executable_attachment_needs_confirmation_before_it_reaches_the_disk() => Sta(async () =>
    {
        var dialogs = new Dialogs();
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        s.AddSingleton<IDialogService>(dialogs);
        new AnnotationsModule().ConfigureServices(s);
        using var sp = s.BuildServiceProvider();
        new AnnotationsModule().Initialize(sp);

        var folder = TestPaths.NewDirectory();
        var source = Path.Combine(folder, "program.exe");
        var content = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 };
        await File.WriteAllBytesAsync(source, content);
        var pdf = await sp.GetRequiredService<IAttachmentService>().AddAsync(SamplePdfFactory.Text(1), source, string.Empty);
        var session = await sp.GetRequiredService<IDocumentManager>().CreateNewAsync(pdf, "z załącznikiem.pdf");

        var panel = new AttachmentsPanel(sp.GetRequiredService<AnnotContext>());
        var start = Environment.TickCount64;
        while (panel.AttachmentCount == 0 && Environment.TickCount64 - start < 20000) await Task.Delay(20);
        Assert.Equal(1, panel.AttachmentCount);
        panel.SelectAttachment(0);

        var target = Path.Combine(folder, "zapisany.exe");
        dialogs.SaveTo = target;
        dialogs.Answer = false;
        await panel.SaveAsync();
        Assert.Equal("program.exe", dialogs.Suggested);
        Assert.Equal(1, dialogs.Warnings);
        Assert.False(File.Exists(target));

        dialogs.Answer = true;
        await panel.SaveAsync();
        Assert.Equal(2, dialogs.Warnings);
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Empty(dialogs.Errors);
        GC.KeepAlive(session);
    });
}

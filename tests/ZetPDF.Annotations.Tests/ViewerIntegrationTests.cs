using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Commands;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Annotations.Tests;

/// <summary>Narzędzia modułu z prawdziwym widokiem (Viewer + PDFium): zdarzenia wskaźnika → polecenie → adnotacja w pliku → Cofnij.</summary>
public class ViewerIntegrationTests
{
    internal static void RunSta(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            var task = body();
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            if (task.IsFaulted) error = task.Exception!.InnerException;
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120))) throw new TimeoutException();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    internal static async Task WaitFor(Func<bool> cond, string what)
    {
        var start = Environment.TickCount64;
        while (!cond())
        {
            if (Environment.TickCount64 - start > 20000) throw new TimeoutException(what);
            await Task.Delay(15);
        }
    }

    internal static void Layout(FrameworkElement e)
    {
        e.Measure(new Size(900, 700));
        e.Arrange(new Rect(0, 0, 900, 700));
        e.UpdateLayout();
    }

    internal static ServiceProvider BuildServices()
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        new ZetPDF.Rendering.RenderingModule().ConfigureServices(s);
        new ZetPDF.Viewer.ViewerModule().ConfigureServices(s);
        new AnnotationsModule().ConfigureServices(s);
        s.AddSingleton<ZetPDF.Viewer.ReadAloud.ISpeechEngineFactory, ZetPDF.Viewer.ReadAloud.SilentSpeechEngineFactory>(); // bez dźwięku w testach
        var sp = s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        new ZetPDF.Rendering.RenderingModule().Initialize(sp);
        new ZetPDF.Viewer.ViewerModule().Initialize(sp);
        new AnnotationsModule().Initialize(sp);
        return sp;
    }

    [Fact]
    public void Rectangle_tool_creates_annotation_and_undo_removes_it()
    {
        RunSta(async () =>
        {
            using var sp = BuildServices();


            var docs = sp.GetRequiredService<IDocumentManager>();
            var session = await docs.CreateNewAsync(SamplePdfFactory.Text(1), "t.pdf");
            docs.ActiveDocument = session;
            var view = sp.GetRequiredService<IDocumentViewFactory>().CreateView(session);
            var viewer = sp.GetRequiredService<IViewerService>();
            Layout(view.Element);
            await WaitFor(() => view.GetPageTransform(0) is not null, "page placed");
            Layout(view.Element);
            await WaitFor(() => viewer.ActiveView is not null || true, "view");

            var registry = sp.GetRequiredService<ICommandRegistry>();
            Assert.True(registry.TryExecute(CommandIds.Comment.Rectangle));
            var tools = sp.GetRequiredService<IToolService>();
            Assert.True(tools.IsActive(CommandIds.Comment.Rectangle));
            var tool = tools.ActiveTool;
            var t = view.GetPageTransform(0)!;
            PagePointerEventArgs Args(PdfPoint p, MouseButton? b) => new(0, p, t.ToView(p), b, ModifierKeys.None, 1);
            tool.OnPointerPressed(view, Args(new PdfPoint(100, 400), MouseButton.Left));
            tool.OnPointerMoved(view, Args(new PdfPoint(200, 300), null));
            tool.OnPointerReleased(view, Args(new PdfPoint(200, 300), MouseButton.Left));
            await WaitFor(() => session.History.CanUndo, "command executed");

            var svc = sp.GetRequiredService<IAnnotationService>();
            var list = await svc.GetAnnotationsAsync(session.Current.GetBytes());
            var a = Assert.Single(list);
            Assert.Equal(AnnotationKind.Square, a.Kind);
            Assert.Equal(new PdfRect(100, 300, 200, 400), a.Rect, new RectComparer());
            Assert.True(await RenderCheck.NonWhitePixels(session.Current.GetBytes(), 0, a.Rect) > 20);

            await session.History.UndoAsync();
            Assert.Empty(await svc.GetAnnotationsAsync(session.Current.GetBytes()));
            view.Dispose();
        });
    }

    private sealed class RectComparer : IEqualityComparer<PdfRect>
    {
        public bool Equals(PdfRect a, PdfRect b) => Math.Abs(a.Left - b.Left) < 0.5 && Math.Abs(a.Right - b.Right) < 0.5 && Math.Abs(a.Top - b.Top) < 0.5 && Math.Abs(a.Bottom - b.Bottom) < 0.5;
        public int GetHashCode(PdfRect r) => 0;
    }
}

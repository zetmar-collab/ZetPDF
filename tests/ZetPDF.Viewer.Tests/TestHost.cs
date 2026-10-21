using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.Viewer.Tests;

/// <summary>Uruchamianie testów WPF na wątku STA z pompą Dispatchera (await wraca na ten wątek).</summary>
internal static class Sta
{
    public static void Run(Func<Task> body, int timeoutSeconds = 120)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Task task;
            try { task = body(); }
            catch (Exception ex) { error = ex; return; }
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            if (task.IsFaulted) error = task.Exception!.InnerExceptions.Count == 1 ? task.Exception.InnerException : task.Exception;
            else if (task.IsCanceled) error = new TaskCanceledException();
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(timeoutSeconds))) throw new TimeoutException("STA test timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Czeka (pompując Dispatcher), aż warunek będzie spełniony.</summary>
    public static async Task WaitFor(Func<bool> condition, int timeoutMs = 20000, string? what = null)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met: " + (what ?? "?"));
            await Task.Delay(15);
        }
    }

    /// <summary>Wymusza układ elementu bez okna.</summary>
    public static void Layout(FrameworkElement element, double width = 900, double height = 700)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }
}

/// <summary>Kontener usług jak w aplikacji: Core + Rendering + Viewer (+ opcjonalnie podmiany).</summary>
internal sealed class ViewerHost : IDisposable
{
    public ViewerHost(Action<IServiceCollection>? extra = null, bool initialize = true)
    {
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(TestPaths.NewDirectory()));
        new ZetPDF.Rendering.RenderingModule().ConfigureServices(s);
        new ViewerModule().ConfigureServices(s);
        s.AddSingleton<ZetPDF.Viewer.ReadAloud.ISpeechEngineFactory, ZetPDF.Viewer.ReadAloud.SilentSpeechEngineFactory>(); // bez dźwięku w testach
        extra?.Invoke(s);
        Services = s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        if (initialize)
        {
            new ZetPDF.Rendering.RenderingModule().Initialize(Services);
            new ViewerModule().Initialize(Services);
        }
    }

    public ServiceProvider Services { get; }
    public IDocumentManager Documents => Services.GetRequiredService<IDocumentManager>();

    public async Task<(IPdfDocumentSession Session, View.DocumentView View)> OpenAsync(byte[] pdf, string name = "test.pdf", double width = 900, double height = 700)
    {
        var session = await Documents.CreateNewAsync(pdf, name);
        Documents.ActiveDocument = session;
        var view = (View.DocumentView)Services.GetRequiredService<IDocumentViewFactory>().CreateView(session);
        Sta.Layout(view.Element, width, height);
        await Sta.WaitFor(() => view.IsDocumentLoaded, what: "document loaded");
        Sta.Layout(view.Element, width, height);
        return (session, view);
    }

    public void Dispose() => Services.Dispose();
}

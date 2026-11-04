using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Services;
using ZetPDF.TestData;

namespace ZetPDF.IntegrationTests.Infrastructure;

/// <summary>
/// Kontener usług jak w aplikacji: Core + wszystkie moduły z <see cref="ZetPDF.App.ModuleCatalog"/> (prawdziwe
/// implementacje, bez atrap modułów). Opcjonalnie atrapy powłoki (dialogi, powłoka) i Initialize modułów
/// (rejestracja poleceń wstążki) – to drugie tylko na wątku STA.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public TestHost(bool withShellFakes = false, bool initializeModules = false)
    {
        Home = TestPaths.NewDirectory();
        var s = new ServiceCollection();
        s.AddZetPdfCore(new AppPaths(Home));
        if (withShellFakes || initializeModules)
        {
            s.AddSingleton(Dialogs);
            s.AddSingleton<IDialogService>(Dialogs);
            s.AddSingleton<IShellService>(Shell);
        }
        foreach (var m in ZetPDF.App.ModuleCatalog.CreateAll()) m.ConfigureServices(s);
        s.AddSingleton<ZetPDF.Viewer.ReadAloud.ISpeechEngineFactory, ZetPDF.Viewer.ReadAloud.SilentSpeechEngineFactory>(); // bez dźwięku w testach
        Services = s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        if (initializeModules)
            foreach (var m in ZetPDF.App.ModuleCatalog.CreateAll()) m.Initialize(Services);
    }

    public string Home { get; }
    public ServiceProvider Services { get; }
    public FakeDialogService Dialogs { get; } = new();
    public FakeShellService Shell { get; } = new();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public IDocumentManager Documents => Get<IDocumentManager>();

    public void Dispose() => Services.Dispose();
}

/// <summary>Uruchamianie na wątku STA z pompą Dispatchera (await wraca na ten wątek, jak w aplikacji).</summary>
internal static class Sta
{
    public static void Run(Func<Task> body, int timeoutSeconds = 240)
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

    public static async Task WaitFor(Func<bool> condition, int timeoutMs = 60000, string? what = null)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met: " + (what ?? "?"));
            await Task.Delay(20);
        }
    }

    public static void Layout(FrameworkElement element, double width = 900, double height = 700)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }
}

/// <summary>Atrapa okien dialogowych: potwierdza, zapisuje komunikaty i błędy, okna modułów „anuluje”.</summary>
internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public Queue<string?> Passwords { get; } = new();
    public List<string> Messages { get; } = new();
    public List<string> Confirmations { get; } = new();
    public List<string> Questions { get; } = new();
    public List<Exception> Errors { get; } = new();
    public List<Type> DialogsShown { get; } = new();
    public IReadOnlyList<string>? FilesToOpen { get; set; }
    public string? FileToSave { get; set; }
    public string? FolderToSelect { get; set; }

    public IReadOnlyList<string>? OpenFiles(string title, IReadOnlyList<FileFilter> filters, bool multiselect = false, string? initialDirectory = null) => FilesToOpen;
    public string? SaveFile(string title, IReadOnlyList<FileFilter> filters, string? defaultFileName = null, string? initialDirectory = null) => FileToSave;
    public string? SelectFolder(string title, string? initialDirectory = null) => FolderToSelect;
    public void ShowMessage(string text, string? title = null, MessageSeverity severity = MessageSeverity.Info) => Messages.Add(text);
    public bool Confirm(string text, string? title = null, MessageSeverity severity = MessageSeverity.Warning) { Confirmations.Add(text); return ConfirmResult; }
    public YesNoCancel AskYesNoCancel(string text, string? title = null) { Questions.Add(text); return YesNoCancel.No; }
    public string? AskPassword(string documentName, bool isRetry) => Passwords.Count > 0 ? Passwords.Dequeue() : null;

    public async Task<bool> RunWithProgressAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work, bool cancellable = true)
    {
        await work(new Progress<OperationProgress>(), CancellationToken.None);
        return true;
    }

    public bool? ShowDialog(Window dialog) { DialogsShown.Add(dialog.GetType()); return false; }
    public List<Window> ModelessShown { get; } = new();
    public void ShowModeless(Window window) => ModelessShown.Add(window);
    public void ShowError(Exception exception, string? contextText = null) => Errors.Add(exception);
}

/// <summary>Atrapa powłoki (bez okna głównego).</summary>
internal sealed class FakeShellService : IShellService
{
    public Window MainWindow => null!;
    public bool IsReadingMode { get; set; }
    public bool IsFullScreen { get; set; }
    public bool IsSidePanelVisible { get; set; }
    public bool IsRibbonMinimized { get; set; }
    public event EventHandler? ShellStateChanged { add { } remove { } }
    public List<string> Statuses { get; } = new();
    public void ShowPanel(string panelId) { }
    public void SetContextualTabVisible(string tabId, bool visible) { }
    public void SetStatus(string? text) { if (text is not null) Statuses.Add(text); }
}

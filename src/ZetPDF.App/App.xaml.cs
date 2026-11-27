using System.Resources;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZetPDF.App.Services;
using ZetPDF.App.Shell;
using ZetPDF.Core.Contracts;
using ZetPDF.Core.Documents;
using ZetPDF.Core.Localization;
using ZetPDF.Core.Services;

namespace ZetPDF.App;

/// <summary>
/// Start aplikacji: kontener DI (Microsoft.Extensions.DependencyInjection), usługi rdzenia, moduły z ModuleCatalog,
/// usługi powłoki, okno główne, pliki z linii poleceń. Globalna obsługa wyjątków: komunikat + zapis do lokalnego
/// dziennika (nic nie jest wysyłane). Własność: ui-shell (rejestracji modułów nie usuwać).
/// </summary>
public partial class App : Application
{
    public static ResourceManager Strings { get; } = new("ZetPDF.App.Resources.Strings", typeof(App).Assembly);

    private ServiceProvider? _services;
    private ILogger<App>? _log;
    private AppPaths? _paths;
    private int _unhandledShown;
    private SingleInstanceChannel? _channel;
    private List<ForwardedOpen>? _pendingForwarded = new();
    private bool _sessionEnding;

    /// <summary>Kontener (dla okien tworzonych z XAML bez wstrzykiwania). Preferuj wstrzykiwanie przez konstruktor.</summary>
    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ZetPDF.Core.Documents.PdfFileProbes.Current = new ZetPDF.Core.Documents.ProcessPdfProbe(Environment.ProcessPath!);
        if (TryForwardToRunningInstance(e.Args)) return;
        _paths = new AppPaths();
        var startupPaths = _paths;
        _ = Task.Run(startupPaths.CleanupOrphanedTemp);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => _log?.LogCritical(a.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", a.IsTerminating);

        WindowThemer.Register();
        PdfSharpEdit.EnsureInitialized();
        var modules = ModuleCatalog.CreateAll();
        var services = new ServiceCollection();
        services.AddZetPdfCore(_paths);
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<ShellService>();
        services.AddSingleton<IShellService>(sp => sp.GetRequiredService<ShellService>());
        services.AddSingleton<IOwnerProtectionPrompts>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<OwnerProtectionGuard>();
        services.AddSingleton<DocumentActions>();
        services.AddSingleton<RecoveryFlow>();
        services.AddSingleton<SessionRestore>();
        services.AddSingleton<MainWindow>();
        foreach (var m in modules) m.ConfigureServices(services);
        // tryby diagnostyczne (test dymny, zrzuty): czytanie na głos przez prawdziwy System.Speech, ale bez dźwięku
        if (e.Args.Any(a => a.StartsWith("--diag-", StringComparison.OrdinalIgnoreCase)))
            services.AddSingleton<ZetPDF.Viewer.ReadAloud.ISpeechEngineFactory, ZetPDF.Viewer.ReadAloud.SilentSpeechEngineFactory>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        _log = _services.GetRequiredService<ILogger<App>>();
        _log.LogInformation("ZetPDF {Version} starting; modules: {Modules}", Licenses.ThirdPartyLibraries.AppVersion, string.Join(", ", modules.Select(m => m.Id)));

        var loc = _services.GetRequiredService<ILocalizationService>();
        loc.RegisterResources(Strings);
        var settings = _services.GetRequiredService<ISettingsService>();
        loc.SetCulture(LocalizationService.ResolveCulture(settings.Current.Language));
        _services.GetRequiredService<IThemeService>().SetTheme(settings.Current.Theme);

        foreach (var m in modules)
        {
            try { m.Initialize(_services); }
            catch (Exception ex) { _log.LogError(ex, "Module {Module} failed to initialize", m.Id); }
        }
        ShellCommands.Register(_services, () => _services.GetRequiredService<MainWindow>());

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        var diag = Array.FindIndex(e.Args, a => string.Equals(a, "--diag-screenshots", StringComparison.OrdinalIgnoreCase));
        if (diag >= 0 && diag + 1 < e.Args.Length)
        {
            var rest = e.Args.Where((_, i) => i != diag && i != diag + 1).ToArray();
            _ = Diagnostics.DiagnosticScreenshots.RunAsync(_services, window, e.Args[diag + 1], rest);
            return;
        }
        var smoke = Array.FindIndex(e.Args, a => string.Equals(a, "--diag-smoke", StringComparison.OrdinalIgnoreCase));
        if (smoke >= 0 && smoke + 1 < e.Args.Length)
        {
            var rest = e.Args.Where((_, i) => i != smoke && i != smoke + 1).ToArray();
            _ = Diagnostics.DiagnosticSmokeTest.RunAsync(_services, window, e.Args[smoke + 1], rest);
            return;
        }
        _ = FinishStartupAsync(e.Args);
    }

    /// <summary>Druga kopia przekazuje pliki działającemu oknu i się kończy; pierwsza zaczyna nasłuch od razu, jeszcze przed zbudowaniem okna.</summary>
    private bool TryForwardToRunningInstance(string[] args)
    {
        var testPipe = Environment.GetEnvironmentVariable("ZETPDF_TEST_PIPE");
        if (!SingleInstanceChannel.ShouldUse(args, Environment.GetEnvironmentVariable("ZETPDF_HOME"), testPipe)) return false;
        var name = SingleInstanceChannel.PipeName(testPipe);
        _channel = SingleInstanceChannel.TryCreateServer(name);
        if (_channel is not null)
        {
            _channel.Start(m => Dispatcher.BeginInvoke(() => OnForwarded(m)), ex => _log?.LogInformation("Pipe client failed: {Type}", ex.GetType().Name));
            return false;
        }
        if (!SingleInstanceChannel.TrySend(name, SingleInstanceChannel.PrepareMessage(args))) return false; // nie odpowiada: osobna kopia, żeby pliki się otworzyły
        Shutdown(0);
        return true;
    }

    private async void OnForwarded(ForwardedOpen message)
    {
        if (_pendingForwarded is not null)
        {
            _pendingForwarded.Add(message); // start programu jeszcze trwa (np. okno odzyskiwania)
            return;
        }
        try
        {
            BringToFront();
            if (message.Files.Count > 0) await Services.GetRequiredService<DocumentActions>().OpenAsync(message.Files, message.Password);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Opening forwarded files failed");
        }
    }

    private void BringToFront()
    {
        if (MainWindow is not { } main) return;
        if (main.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(main);
        // Otwarte okno modalne blokuje główne, więc to ono dostaje aktywację.
        var modal = Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && ReferenceEquals(w.Owner, main));
        (modal ?? main).Activate();
    }

    /// <summary>Po pokazaniu okna: autozapis, okno odzyskiwania (kopie po awarii), potem pliki z linii poleceń.</summary>
    private async Task FinishStartupAsync(string[] args)
    {
        await Dispatcher.Yield(DispatcherPriority.Background); // najpierw okno się rysuje
        try
        {
            Services.GetRequiredService<AutoSaveService>().Start();
            await Services.GetRequiredService<RecoveryFlow>().RunAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Recovery on startup failed");
        }
        await OpenFromCommandLineAsync(args);
        var pending = _pendingForwarded;
        _pendingForwarded = null;
        if (pending is not null) foreach (var m in pending) OnForwarded(m);
    }

    /// <summary>Argumenty: pliki PDF (wiele) i opcjonalnie -pwd&lt;hasło&gt; (dla wszystkich plików).</summary>
    internal static (IReadOnlyList<string> Files, string? Password) ParseArgs(IEnumerable<string> args)
    {
        string? password = null;
        var files = new List<string>();
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (a.StartsWith("-pwd", StringComparison.OrdinalIgnoreCase)) password = a[4..];
            else files.Add(a);
        }
        return (files, password);
    }

    private async Task OpenFromCommandLineAsync(string[] args)
    {
        var (files, password) = ParseArgs(args);
        var actions = Services.GetRequiredService<DocumentActions>();
        if (files.Count == 0)
        {
            // Bez plików w linii poleceń: ostatnia sesja (opcja w Ustawieniach).
            try { await Services.GetRequiredService<SessionRestore>().RestoreAsync(paths => actions.OpenAsync(paths)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log?.LogError(ex, "Session restore failed"); }
            return;
        }
        await actions.OpenAsync(files, password);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("ZetPDF exiting");
        _channel?.Dispose();
        // Normalne wyjście usuwa kopie odzyskiwania; zamykanie Windows ich nie usuwa (niezapisane zmiany zostają do odzyskania).
        _services?.GetService<AutoSaveService>()?.Stop(deleteFiles: !_sessionEnding);
        _services?.Dispose();
        _paths?.CleanupTemp();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _sessionEnding = true;
        base.OnSessionEnding(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        // Nie zalewamy użytkownika kaskadą okien przy powtarzających się błędach.
        if (Interlocked.Exchange(ref _unhandledShown, 1) == 1) return;
        try { _services?.GetService<DialogService>()?.ShowUnhandled(e.Exception); }
        catch (Exception inner) { _log?.LogError(inner, "Error while showing error"); }
        finally { Interlocked.Exchange(ref _unhandledShown, 0); }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}

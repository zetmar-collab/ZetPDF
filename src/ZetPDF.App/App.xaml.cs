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

    /// <summary>Kontener (dla okien tworzonych z XAML bez wstrzykiwania). Preferuj wstrzykiwanie przez konstruktor.</summary>
    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _paths = new AppPaths();
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
        services.AddSingleton<DocumentActions>();
        services.AddSingleton<MainWindow>();
        foreach (var m in modules) m.ConfigureServices(services);
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
            _ = Diagnostics.DiagnosticScreenshots.RunAsync(_services, window, e.Args[diag + 1]);
            return;
        }
        _ = OpenFromCommandLineAsync(e.Args);
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
        if (files.Count == 0) return;
        await Dispatcher.Yield(DispatcherPriority.Background); // najpierw okno się rysuje
        await Services.GetRequiredService<DocumentActions>().OpenAsync(files, password);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("ZetPDF exiting");
        _services?.Dispose();
        _paths?.CleanupTemp();
        base.OnExit(e);
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

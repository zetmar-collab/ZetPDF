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
/// usługi powłoki, okno główne. Własność: ui-shell (rejestracji modułów nie usuwać).
/// </summary>
public partial class App : Application
{
    public static ResourceManager Strings { get; } = new("ZetPDF.App.Resources.Strings", typeof(App).Assembly);

    private ServiceProvider? _services;
    private ILogger<App>? _log;
    private AppPaths? _paths;

    /// <summary>Kontener (dla okien tworzonych z XAML bez wstrzykiwania). Preferuj wstrzykiwanie przez konstruktor.</summary>
    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _paths = new AppPaths();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => _log?.LogCritical(a.ExceptionObject as Exception, "Unhandled exception");

        PdfSharpEdit.EnsureInitialized();
        var modules = ModuleCatalog.CreateAll();
        var services = new ServiceCollection();
        services.AddZetPdfCore(_paths);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<IShellService>(sp => sp.GetRequiredService<ShellService>());
        services.AddSingleton<MainWindow>();
        foreach (var m in modules) m.ConfigureServices(services);
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        _log = _services.GetRequiredService<ILogger<App>>();
        _log.LogInformation("ZetPDF 0.1.0-test starting; modules: {Modules}", string.Join(", ", modules.Select(m => m.Id)));

        var loc = _services.GetRequiredService<ILocalizationService>();
        loc.RegisterResources(Strings);
        var settings = _services.GetRequiredService<ISettingsService>();
        loc.SetCulture(LocalizationService.ResolveCulture(settings.Current.Language));
        _services.GetRequiredService<IThemeService>().SetTheme(settings.Current.Theme);

        foreach (var m in modules) m.Initialize(_services);
        ShellCommands.Register(_services);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        _ = OpenFromCommandLineAsync(e.Args);
    }

    private async Task OpenFromCommandLineAsync(string[] args)
    {
        string? password = null;
        var files = new List<string>();
        foreach (var a in args)
        {
            if (a.StartsWith("-pwd", StringComparison.OrdinalIgnoreCase)) password = a[4..];
            else files.Add(a);
        }
        var documents = Services.GetRequiredService<IDocumentManager>();
        foreach (var f in files)
        {
            try { await documents.OpenAsync(f, password); }
            catch (Exception ex) { Services.GetRequiredService<IDialogService>().ShowError(ex); }
        }
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
        try { _services?.GetService<IDialogService>()?.ShowError(e.Exception); }
        catch (Exception inner) { _log?.LogError(inner, "Error while showing error"); }
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}

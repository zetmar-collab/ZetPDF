using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Core.Services;

/// <summary>Ścieżki aplikacji. ZETPDF_HOME przekierowuje wszystkie katalogi (testy, wersja przenośna).</summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? homeOverride = null, string? tempRootOverride = null)
    {
        var home = homeOverride ?? Environment.GetEnvironmentVariable("ZETPDF_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            AppDataDirectory = Path.Combine(home, "AppData");
            LocalDataDirectory = Path.Combine(home, "Local");
            TempRoot = Path.Combine(home, "Temp");
        }
        else
        {
            AppDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZetPDF");
            LocalDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZetPDF");
            TempRoot = Path.Combine(Path.GetTempPath(), "ZetPDF");
        }
        if (!string.IsNullOrWhiteSpace(tempRootOverride)) TempRoot = tempRootOverride;
        TempDirectory = Path.Combine(TempRoot, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private string TempRoot { get; }
    public string AppDataDirectory { get; }
    public string LocalDataDirectory { get; }
    public string LogDirectory => Path.Combine(LocalDataDirectory, "logs");
    public string TempDirectory { get; }
    public string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");
    public string SignaturesDirectory => Path.Combine(AppDataDirectory, "signatures");
    public string StampsDirectory => Path.Combine(AppDataDirectory, "stamps");
    public string ActionsDirectory => Path.Combine(AppDataDirectory, "actions");
    public string DigitalIdsDirectory => Path.Combine(AppDataDirectory, "digital-ids");

    /// <summary>Usuwa folder tymczasowy procesu (wołane przy zamknięciu aplikacji).</summary>
    public void CleanupTemp()
    {
        try { if (Directory.Exists(TempDirectory)) Directory.Delete(TempDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Bezpieczny zapis: plik tymczasowy w tym samym folderze + File.Replace/File.Move.</summary>
public sealed class FileService : IFileService
{
    private readonly IAppPaths _paths;

    public FileService(IAppPaths paths) => _paths = paths;

    public async Task WriteAllBytesSafeAsync(string path, byte[] data, bool keepBackup = false, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new ArgumentException("Invalid path.", nameof(path));
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await fs.WriteAsync(data, ct);
                await fs.FlushAsync(ct);
            }
            if (File.Exists(full))
            {
                var backup = full + ".bak";
                File.Replace(temp, full, backup, ignoreMetadataErrors: true);
                if (!keepBackup)
                {
                    try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            else
            {
                File.Move(temp, full);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public string GetTempFilePath(string extension = ".tmp")
    {
        Directory.CreateDirectory(_paths.TempDirectory);
        if (!extension.StartsWith('.')) extension = "." + extension;
        return Path.Combine(_paths.TempDirectory, Guid.NewGuid().ToString("N") + extension);
    }

    public string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "_" : s;
    }
}

/// <summary>Ustawienia w JSON: { "General": AppSettings, "Modules": { klucz: obiekt } }. Zapis atomowy.</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly ILogger<SettingsService>? _log;
    private readonly JsonObject _modules;

    public SettingsService(IAppPaths paths, ILogger<SettingsService>? log = null)
    {
        _file = paths.SettingsFile;
        _log = log;
        (Current, _modules) = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public T GetModuleSettings<T>(string key) where T : class, new()
    {
        lock (_gate)
        {
            if (_modules.TryGetPropertyValue(key, out var node) && node is not null)
            {
                try { return node.Deserialize<T>(Json) ?? new T(); }
                catch (JsonException ex) { _log?.LogWarning(ex, "Invalid module settings {Key}", key); }
            }
            return new T();
        }
    }

    public void SetModuleSettings<T>(string key, T value) where T : class, new()
    {
        lock (_gate)
        {
            _modules[key] = JsonSerializer.SerializeToNode(value, Json);
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private (AppSettings, JsonObject) Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var root = JsonNode.Parse(File.ReadAllText(_file)) as JsonObject;
                var general = root?["General"]?.Deserialize<AppSettings>(Json) ?? new AppSettings();
                var modules = root?["Modules"] as JsonObject ?? new JsonObject();
                root?.Remove("Modules");
                return (general, (JsonObject)(modules.DeepClone()));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.LogWarning(ex, "Settings file is invalid – using defaults");
            try { File.Copy(_file, _file + ".invalid", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return (new AppSettings(), new JsonObject());
    }

    private void SaveLocked()
    {
        try
        {
            var root = new JsonObject
            {
                ["General"] = JsonSerializer.SerializeToNode(Current, Json),
                ["Modules"] = _modules.DeepClone(),
            };
            var dir = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(dir);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(Json));
            if (File.Exists(_file)) File.Replace(tmp, _file, null, true);
            else File.Move(tmp, _file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogError(ex, "Cannot save settings");
        }
    }
}

/// <summary>Ostatnie pliki (lista w AppSettings.RecentFiles).</summary>
public sealed class RecentFilesService : IRecentFilesService
{
    private readonly ISettingsService _settings;

    public RecentFilesService(ISettingsService settings) => _settings = settings;

    public IReadOnlyList<string> Files => _settings.Current.RecentFiles.ToArray();

    public event EventHandler? Changed;

    public void Add(string path)
    {
        var full = Path.GetFullPath(path);
        _settings.Update(s =>
        {
            s.RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            s.RecentFiles.Insert(0, full);
            if (s.RecentFiles.Count > Math.Max(1, s.MaxRecentFiles)) s.RecentFiles.RemoveRange(s.MaxRecentFiles, s.RecentFiles.Count - s.MaxRecentFiles);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string path)
    {
        _settings.Update(s => s.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _settings.Update(s => s.RecentFiles.Clear());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Rejestr paneli bocznych.</summary>
public sealed class PanelRegistry : IPanelRegistry
{
    private readonly List<PanelDescriptor> _panels = new();
    private readonly object _gate = new();

    public IReadOnlyList<PanelDescriptor> Panels
    {
        get { lock (_gate) return _panels.OrderBy(p => p.Order).ToArray(); }
    }

    public event EventHandler? Changed;

    public void Register(PanelDescriptor panel)
    {
        lock (_gate)
        {
            if (_panels.Any(p => p.Id == panel.Id)) throw new InvalidOperationException($"Panel '{panel.Id}' already registered.");
            _panels.Add(panel);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

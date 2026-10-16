using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ZetPDF.Core.Services;

/// <summary>
/// Dziennik do pliku lokalnego %LOCALAPPDATA%\ZetPDF\logs\zetpdf-yyyyMMdd.log. Nic nie jest wysyłane w sieć.
/// Zapis synchroniczny pod blokadą (mały ruch); pliki starsze niż 14 dni usuwane przy starcie.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var f in Directory.EnumerateFiles(directory, "zetpdf-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string CurrentFile => Path.Combine(_directory, $"zetpdf-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(this, n));

    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
          .Append(level.ToString().ToUpperInvariant()[..4]).Append(' ')
          .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ")
          .Append(category).Append(": ").Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        sb.AppendLine();
        lock (_gate)
        {
            try { File.AppendAllText(CurrentFile, sb.ToString(), Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= _owner._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}

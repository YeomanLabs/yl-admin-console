using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AdminConsole.App.Logging;

/// <summary>
/// Doc 03: a ~40-line file sink, rolling daily in %LOCALAPPDATA%\AdminConsole\logs\, plus Debug output.
/// No Serilog in Phase 1. Level filtering is done by the logger factory; this only formats and writes.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLoggerProvider(string directory) => _directory = directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || today != _day)
                {
                    _writer?.Dispose();
                    _writer = Open(today);
                    _day = today;
                }

                _writer?.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down: a write that fails is dropped, and the next one tries again.
                _writer?.Dispose();
                _writer = null;
            }
        }

        Debug.WriteLine(line);
    }

    /// <summary>
    /// The day's log, or this process's own when another copy of the console already holds it (0.3.3): opening a
    /// second copy — the zip beside an installed MSI, say — used to crash at start with "the file is being used by
    /// another process". Null when neither can be opened; the app then logs to Debug only.
    /// </summary>
    private StreamWriter? Open(DateOnly day)
    {
        Directory.CreateDirectory(_directory);
        foreach (var name in new[] { $"app-{day:yyyyMMdd}.log", $"app-{day:yyyyMMdd}-{Environment.ProcessId}.log" })
        {
            try
            {
                var stream = new FileStream(Path.Combine(_directory, name), FileMode.Append, FileAccess.Write, FileShare.Read);
                return new StreamWriter(stream) { AutoFlush = true };
            }
            catch (IOException)
            {
                // Held by another copy of the console: try this process's own file.
            }
        }

        return null;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCategory = category.LastIndexOf('.') is var i && i >= 0 ? category[(i + 1)..] : category;
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Abbrev(logLevel)}] {shortCategory}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Write(line);
        }

        private static string Abbrev(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}

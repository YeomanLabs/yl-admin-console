using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace YeomanLabs.AdminConsole.App.Logging;

/// <summary>
/// Doc 03: a ~40-line file sink, rolling daily in %LOCALAPPDATA%\YeomanLabs\AdminConsole\logs\, plus Debug output.
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
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer is null || today != _day)
            {
                _writer?.Dispose();
                Directory.CreateDirectory(_directory);
                _writer = new StreamWriter(Path.Combine(_directory, $"app-{today:yyyyMMdd}.log"), append: true) { AutoFlush = true };
                _day = today;
            }

            _writer.WriteLine(line);
        }

        Debug.WriteLine(line);
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

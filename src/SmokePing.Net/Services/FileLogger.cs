using System.Text;
using Microsoft.Extensions.Logging;

namespace SmokePing.Net.Services;

/// <summary>
/// A small rolling file logger.
///
/// When the process runs as a Windows service there is no console to write to, and
/// the Event Log providers live in NuGet packages this project deliberately avoids.
/// Writes are serialised and flushed immediately: a monitoring tool that crashes is
/// exactly when the last few lines matter most.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;

    public FileLoggerProvider(string path, long maximumBytes = 8 * 1024 * 1024, int retainedFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Roll();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Losing a log line must never take the daemon down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Renames the current file out of the way once it grows past the limit.</summary>
    private void Roll()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < _maximumBytes)
        {
            return;
        }

        var oldest = $"{_path}.{_retainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _retainedFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_path}.{i + 1}", overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            builder.Append(' ');
            builder.Append(Abbreviate(logLevel));
            builder.Append(' ');
            builder.Append(_category);
            builder.Append(": ");
            builder.AppendLine(formatter(state, exception));

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            _provider.Write(builder.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Tether.Core.Logging;

/// <summary>
/// Writes one log file per day (tether-yyyyMMdd.log) and deletes files older than the retention.
/// Callers must never log secrets; nothing in Tether passes the token to a logger.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private volatile int _minimum;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _currentDay;

    public RollingFileLoggerProvider(string directory, int retentionDays = 14, LogLevel minimum = LogLevel.Information, TimeProvider? clock = null)
    {
        _directory = directory;
        _retentionDays = retentionDays;
        _minimum = (int)minimum;
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
    }

    /// <summary>The lowest level written; Debug mode lowers it to <see cref="LogLevel.Debug"/> while the app runs.</summary>
    public LogLevel Minimum
    {
        get => (LogLevel)_minimum;
        set => _minimum = (int)value;
    }

    public string CurrentFile => FileFor(DateOnly.FromDateTime(_clock.GetLocalNow().DateTime));

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private string FileFor(DateOnly day) => Path.Combine(_directory, $"tether-{day:yyyyMMdd}.log");

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = _clock.GetLocalNow();
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Short(level)}] {category}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;
        lock (_gate)
        {
            try
            {
                var day = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || day != _currentDay)
                {
                    _writer?.Dispose();
                    _currentDay = day;
                    _writer = new StreamWriter(new FileStream(FileFor(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                    PurgeOld(day);
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Logging must never break syncing.
            }
        }
    }

    private void PurgeOld(DateOnly today)
    {
        var cutoff = today.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "tether-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name["tether-".Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.Minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

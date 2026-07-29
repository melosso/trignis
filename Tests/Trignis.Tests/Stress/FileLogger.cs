using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Trignis.Tests.Stress;

// TEMPORARY debug sink for the stress harness, enabled by TRIGNIS_STRESS_LOG
internal sealed class FileLogger<T> : ILogger<T>
{
    private static readonly object Gate = new();
    private readonly string _path;

    public FileLogger(string path) => _path = path;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{logLevel}] {typeof(T).Name}: {formatter(state, exception)}";
        if (exception is not null) line += $"{Environment.NewLine}    {exception}";

        lock (Gate) File.AppendAllText(_path, line + Environment.NewLine);
    }
}

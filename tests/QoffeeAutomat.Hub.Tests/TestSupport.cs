using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Tests;

/// <summary>A temporary directory that is removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "qoffeeautomat-hub-tests-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A path below the directory that does not exist yet.</summary>
    public string Below(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a file may still be open on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>A clock that only moves when a test says so.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }
}

/// <summary>Collects the formatted log messages of a host.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(_messages);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(formatter(state, exception));
        }
    }
}

/// <summary>Waits for a condition that another thread makes true.</summary>
internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMilliseconds = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + because);
            await Task.Delay(20);
        }
    }
}

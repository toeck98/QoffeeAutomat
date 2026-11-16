using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Writes log entries as one JSON object per line to rolling files in a directory
/// (<c>agent-yyyyMMdd.log</c>, PLAN.md T201 "structured logging to console and rolling files").
/// A new file starts every UTC day and when a file reaches the size limit; old files beyond the
/// retention count are deleted. Entries are queued and written by one background task, so logging never
/// blocks the caller, and a directory that cannot be written to never throws into the application.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    /// <summary>The default number of files that are kept.</summary>
    public const int DefaultRetainedFiles = 14;

    /// <summary>The default size at which a file is rolled (10 MiB).</summary>
    public const long DefaultMaxFileBytes = 10L * 1024 * 1024;

    private const string FilePrefix = "agent-";
    private const string FileExtension = ".log";
    private const int QueueCapacity = 10_000;
    private const int BatchBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly int _retainedFiles;
    private readonly long _maxFileBytes;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly Task _writer;
    private string? _lastCleanupDay;

    /// <summary>Creates the provider and starts its writer.</summary>
    /// <param name="directory">The log directory; created when missing.</param>
    /// <param name="time">The clock for timestamps and file names.</param>
    /// <param name="retainedFiles">How many files to keep.</param>
    /// <param name="maxFileBytes">The size at which a file is rolled.</param>
    public RollingFileLoggerProvider(
        string directory,
        TimeProvider time,
        int retainedFiles = DefaultRetainedFiles,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1L);

        _directory = directory;
        _time = time;
        _retainedFiles = retainedFiles;
        _maxFileBytes = maxFileBytes;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, this);
    }

    /// <summary>Writes everything that is queued and stops the writer.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(5));
    }

    private void Enqueue(string line)
    {
        _queue.Writer.TryWrite(line);
    }

    private async Task WriteLoopAsync()
    {
        ChannelReader<string> reader = _queue.Reader;
        var batch = new StringBuilder();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Length < BatchBytes && reader.TryRead(out string? line))
            {
                batch.Append(line).Append('\n');
            }

            await AppendAsync(batch.ToString()).ConfigureAwait(false);
        }
    }

    private async Task AppendAsync(string text)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            DateTime today = _time.GetUtcNow().UtcDateTime;
            string path = CurrentFile(today);
            await File.AppendAllTextAsync(path, text, Utf8).ConfigureAwait(false);
            Cleanup(today);
        }
#pragma warning disable CA1031 // Logging must never take the agent down; the entries of this batch are dropped.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing sensible can be done about a log directory that cannot be written.
        }
    }

    /// <summary>The file for today; a full file is renamed out of the way so the base name always receives new entries.</summary>
    private string CurrentFile(DateTime day)
    {
        string baseName = FilePrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string path = Path.Combine(_directory, baseName + FileExtension);
        if (File.Exists(path) && new FileInfo(path).Length >= _maxFileBytes)
        {
            for (int index = 1; ; index++)
            {
                string rolled = Path.Combine(_directory, baseName + "." + index.ToString(CultureInfo.InvariantCulture) + FileExtension);
                if (!File.Exists(rolled))
                {
                    File.Move(path, rolled);
                    break;
                }
            }
        }

        return path;
    }

    /// <summary>Deletes the oldest files beyond the retention count; runs once per day.</summary>
    private void Cleanup(DateTime day)
    {
        string today = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (string.Equals(_lastCleanupDay, today, StringComparison.Ordinal))
        {
            return;
        }

        _lastCleanupDay = today;
        var files = Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension)
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .ToList();
        foreach (string file in files.Skip(_retainedFiles))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Try again tomorrow.
            }
        }
    }

    private string Format<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("level", level.ToString());
            writer.WriteString("category", category);
            if (eventId.Id != 0)
            {
                writer.WriteNumber("eventId", eventId.Id);
            }

            writer.WriteString("message", message);
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                WriteProperties(writer, properties);
            }

            if (exception is not null)
            {
                writer.WriteString("exception", exception.ToString());
            }

            writer.WriteEndObject();
        }

        return Utf8.GetString(stream.ToArray());
    }

    private static void WriteProperties(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> properties)
    {
        bool started = false;
        foreach ((string key, object? value) in properties)
        {
            if (key == "{OriginalFormat}")
            {
                continue;
            }

            if (!started)
            {
                writer.WriteStartObject("props");
                started = true;
            }

            writer.WritePropertyName(key);
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case int whole:
                    writer.WriteNumberValue(whole);
                    break;
                case long wide:
                    writer.WriteNumberValue(wide);
                    break;
                case double real when double.IsFinite(real):
                    writer.WriteNumberValue(real);
                    break;
                default:
                    writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        if (started)
        {
            writer.WriteEndObject();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly RollingFileLoggerProvider _provider;

        public FileLogger(string category, RollingFileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            string message = formatter(state, exception);
            _provider.Enqueue(_provider.Format(_category, logLevel, eventId, state, exception, message));
        }
    }
}

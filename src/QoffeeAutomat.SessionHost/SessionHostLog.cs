using System.Globalization;

namespace QoffeeAutomat.SessionHost;

/// <summary>Where the SessionHost writes its diagnostics. It has no console, so a file is the only place.</summary>
public interface ISessionHostLog
{
    /// <summary>Writes an informational line.</summary>
    /// <param name="message">The text; never contains variable values.</param>
    void Info(string message);

    /// <summary>Writes a warning line.</summary>
    /// <param name="message">The text; never contains variable values.</param>
    void Warning(string message);
}

/// <summary>A log that discards everything.</summary>
public sealed class NullSessionHostLog : ISessionHostLog
{
    /// <summary>The shared instance.</summary>
    public static NullSessionHostLog Instance { get; } = new();

    /// <inheritdoc />
    public void Info(string message)
    {
    }

    /// <inheritdoc />
    public void Warning(string message)
    {
    }
}

/// <summary>
/// Appends lines to <c>%LOCALAPPDATA%\QoffeeAutomat\logs\sessionhost.log</c> and moves the file to
/// <c>sessionhost.log.1</c> when it grows beyond 1 MiB. A log that cannot be written is silently dropped:
/// diagnostics must never stop the SessionHost.
/// </summary>
public sealed class FileSessionHostLog : ISessionHostLog
{
    private const long MaxBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;

    /// <summary>Creates the log.</summary>
    /// <param name="path">The log file.</param>
    /// <param name="time">The clock for the time stamps.</param>
    public FileSessionHostLog(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The default location below the local application data of the user.</summary>
    /// <returns>The file path.</returns>
    public static string DefaultPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "QoffeeAutomat", "logs", "sessionhost.log");
    }

    /// <inheritdoc />
    public void Info(string message) => Write("INF", message);

    /// <inheritdoc />
    public void Warning(string message) => Write("WRN", message);

    private void Write(string level, string message)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}{Environment.NewLine}");
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                File.AppendAllText(_path, line);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Dropped on purpose.
            }
        }
    }
}

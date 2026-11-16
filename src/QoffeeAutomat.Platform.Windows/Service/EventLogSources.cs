using System.Diagnostics;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>Registers and removes Event Log sources; registering needs administrator rights.</summary>
public interface IEventLogSources
{
    /// <summary>Tells whether a source is registered.</summary>
    /// <param name="source">The source name.</param>
    /// <returns>True when it exists.</returns>
    bool Exists(string source);

    /// <summary>Registers a source in a log.</summary>
    /// <param name="source">The source name.</param>
    /// <param name="logName">The log, for example <c>Application</c>.</param>
    void Create(string source, string logName);

    /// <summary>Removes a source.</summary>
    /// <param name="source">The source name.</param>
    void Delete(string source);
}

/// <summary>The Windows Event Log.</summary>
public sealed class WindowsEventLogSources : IEventLogSources
{
    /// <inheritdoc />
    public bool Exists(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return EventLog.SourceExists(source);
    }

    /// <inheritdoc />
    public void Create(string source, string logName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(logName);

        if (!EventLog.SourceExists(source))
        {
            EventLog.CreateEventSource(new EventSourceCreationData(source, logName));
        }
    }

    /// <inheritdoc />
    public void Delete(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (EventLog.SourceExists(source))
        {
            EventLog.DeleteEventSource(source);
        }
    }
}

namespace QoffeeAutomat.Platform.Linux.Metrics.Session;

/// <summary>
/// Reads the sessions with <c>loginctl</c> (PLAN.md S17, T801): <c>list-sessions</c> for the ids, then <c>show-session</c> for the
/// properties of each. This is the only source that knows the <c>LockedHint</c>, which the desktop sets through logind when the
/// screen is locked.
/// </summary>
public sealed class LoginctlSessionSource : ILogindSessionSource
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] ListArguments = ["list-sessions", "--no-legend", "--no-pager"];

    private static readonly string[] PropertyArguments =
    [
        "--property=Id",
        "--property=Name",
        "--property=User",
        "--property=Seat",
        "--property=Class",
        "--property=Type",
        "--property=State",
        "--property=Active",
        "--property=Remote",
        "--property=LockedHint",
    ];

    private readonly IProcessOutputReader _reader;
    private readonly string _fileName;

    /// <summary>Creates the source.</summary>
    /// <param name="reader">Runs the command.</param>
    /// <param name="fileName">The program; <c>loginctl</c> is looked up in <c>PATH</c>.</param>
    public LoginctlSessionSource(IProcessOutputReader reader, string fileName = "loginctl")
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        _reader = reader;
        _fileName = fileName;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LogindSession>?> ListSessionsAsync(CancellationToken cancellationToken)
    {
        string? list = await _reader.ReadStandardOutputAsync(_fileName, ListArguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (list is null)
        {
            return null;
        }

        var sessions = new List<LogindSession>();
        foreach (string id in LogindSessionParser.ParseSessionIds(list))
        {
            string[] arguments = ["show-session", id, "--no-pager", .. PropertyArguments];
            string? properties = await _reader.ReadStandardOutputAsync(_fileName, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);

            // A session that ended between the two calls is gone, which is not an error.
            if (properties is not null && LogindSessionParser.ParseShowSession(id, properties) is { } session)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }
}

/// <summary>
/// Reads the sessions from the files that logind keeps in <c>/run/systemd/sessions</c> (PLAN.md S17, T801). It needs no process and
/// no bus, which makes it the fallback when <c>loginctl</c> is not there. The files do not say whether a session is locked.
/// </summary>
public sealed class SessionFilesLogindSource : ILogindSessionSource
{
    private readonly string _directory;

    /// <summary>Creates the source.</summary>
    /// <param name="rootDirectory">The directory that holds <c>run</c>; null or empty means <c>/</c>. Tests use a directory with synthetic files.</param>
    public SessionFilesLogindSource(string? rootDirectory = null)
    {
        _directory = Path.Combine(string.IsNullOrEmpty(rootDirectory) ? "/" : rootDirectory, "run/systemd/sessions");
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LogindSession>?> ListSessionsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            return null;
        }

        string[] paths;
        try
        {
            paths = [.. Directory.EnumerateFiles(_directory).Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var sessions = new List<LogindSession>();
        foreach (string path in paths)
        {
            // The directory also holds the pipes "<id>.ref"; only plain ids are sessions.
            string id = Path.GetFileName(path);
            if (!LogindSessionParser.IsSessionId(id))
            {
                continue;
            }

            try
            {
                string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (LogindSessionParser.ParseSessionFile(id, content) is { } session)
                {
                    sessions.Add(session);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The session ended while the directory was read.
            }
        }

        return sessions;
    }
}

/// <summary>Asks several sources in order and takes the answer of the first one that can answer at all (an empty list is an answer).</summary>
public sealed class FallbackLogindSessionSource : ILogindSessionSource
{
    private readonly ILogindSessionSource[] _sources;

    /// <summary>Creates the source.</summary>
    /// <param name="sources">The sources, the preferred one first.</param>
    public FallbackLogindSessionSource(IEnumerable<ILogindSessionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LogindSession>?> ListSessionsAsync(CancellationToken cancellationToken)
    {
        foreach (ILogindSessionSource source in _sources)
        {
            IReadOnlyList<LogindSession>? sessions = await source.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            if (sessions is not null)
            {
                return sessions;
            }
        }

        return null;
    }
}

/// <summary>Creates the session source that production uses.</summary>
public static class LogindSessionSources
{
    /// <summary><c>loginctl</c> first, the session files of logind when it is not available.</summary>
    /// <param name="reader">Runs <c>loginctl</c>; null starts a real process.</param>
    /// <param name="rootDirectory">The root for the session files; null or empty means <c>/</c>.</param>
    /// <returns>The source.</returns>
    public static ILogindSessionSource CreateDefault(IProcessOutputReader? reader = null, string? rootDirectory = null)
    {
        return new FallbackLogindSessionSource(
        [
            new LoginctlSessionSource(reader ?? new ProcessOutputReader()),
            new SessionFilesLogindSource(rootDirectory),
        ]);
    }
}

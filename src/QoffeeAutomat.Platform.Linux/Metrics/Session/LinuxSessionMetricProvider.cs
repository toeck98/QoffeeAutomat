using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Metrics.Session;

/// <summary>Chooses the session that counts as "the logged-in user" among the sessions of a computer (PLAN.md S11, S17, T801).</summary>
public static class LogindSessionSelector
{
    /// <summary>
    /// The interactive session of the computer: an ordinary user session (class <c>user</c>, not a login screen or a service) on a seat
    /// (a local login, not ssh) that is not closing. With several, the active one wins, then a graphical one over a text console, then
    /// the lowest id. A computer that only has ssh sessions has no interactive user.
    /// </summary>
    /// <param name="sessions">The sessions that logind reports.</param>
    /// <returns>The session; null when nobody is logged in at the computer.</returns>
    public static LogindSession? SelectInteractive(IEnumerable<LogindSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        return sessions
            .Where(session => string.Equals(session.SessionClass, "user", StringComparison.Ordinal)
                && !session.IsRemote
                && session.Seat is not null
                && !string.Equals(session.State, "closing", StringComparison.Ordinal))
            .OrderByDescending(session => session.IsActive)
            .ThenByDescending(session => session.SessionType is "x11" or "wayland" or "mir")
            .ThenBy(session => session.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}

/// <summary>
/// The session metrics that Linux can deliver without a helper in the user's session (PLAN.md S06, S17, T801): <c>session.user</c>,
/// the logged-in user, and <c>session.locked</c>, whether the session is locked, both read from logind. The ids and descriptors are
/// those of <see cref="SessionMetricCatalog"/>, so they are the ones of Windows. Without a login both are unavailable (null);
/// <c>session.locked</c> is also unavailable when the desktop does not tell logind (no <c>LockedHint</c>).
/// <para>
/// The metric <c>session.ready</c> and the user-context metrics (idle time, audio, display) need the session helper of T803 and are
/// not part of this provider. The session bridge of that task reuses <see cref="ReadSessionAsync"/> rather than registering this
/// provider next to itself, because both would announce the same ids.
/// </para>
/// </summary>
public sealed class LinuxSessionMetricProvider : IMetricProvider
{
    private static readonly MetricDescriptor[] Descriptors =
    [
        .. SessionMetricCatalog.AgentSide.Where(descriptor =>
            descriptor.Id is SessionMetricIds.Locked or SessionMetricIds.User),
    ];

    private readonly ILogindSessionSource _sessions;

    /// <summary>Creates the provider.</summary>
    /// <param name="sessions">Where the sessions come from; null uses <c>loginctl</c> with the session files of logind as fallback.</param>
    public LinuxSessionMetricProvider(ILogindSessionSource? sessions = null)
    {
        _sessions = sessions ?? LogindSessionSources.CreateDefault();
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Describe() => Descriptors;

    /// <summary>Reads the interactive session of the computer.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The session; null when nobody is logged in at the computer or logind cannot be asked.</returns>
    public async ValueTask<LogindSession?> ReadSessionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LogindSession>? sessions = await _sessions.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        return sessions is null ? null : LogindSessionSelector.SelectInteractive(sessions);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        bool wantsUser = metricIds.Contains(SessionMetricIds.User, StringComparer.Ordinal);
        bool wantsLocked = metricIds.Contains(SessionMetricIds.Locked, StringComparer.Ordinal);
        if (!wantsUser && !wantsLocked)
        {
            return values;
        }

        LogindSession? session = await ReadSessionAsync(cancellationToken).ConfigureAwait(false);
        if (wantsUser)
        {
            values[SessionMetricIds.User] = session?.UserName;
        }

        if (wantsLocked)
        {
            values[SessionMetricIds.Locked] = session?.IsLocked;
        }

        return values;
    }
}

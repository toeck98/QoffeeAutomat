namespace QoffeeAutomat.Hub.Security;

/// <summary>Tunable limits of the hub authentication (PLAN.md S09 item 1, S20 "Hub security"). The defaults are the product values.</summary>
public sealed record HubAuthOptions
{
    /// <summary>A session ends after this long without a request.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(8);

    /// <summary>A session ends this long after sign-in, however active it is.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The most sessions kept at once; the oldest is dropped beyond that.</summary>
    public int MaxSessions { get; init; } = 256;

    /// <summary>Failed attempts from one address within <see cref="FailureWindow"/> that lock that address out.</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>Failed attempts from all addresses together within <see cref="FailureWindow"/> that lock sign-in for everybody.</summary>
    public int MaxFailedAttemptsOverall { get; init; } = 30;

    /// <summary>The period in which failed attempts are counted.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a lockout lasts.</summary>
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The shortest accepted password (PLAN.md S09).</summary>
    public int MinPasswordLength { get; init; } = 10;

    /// <summary>The longest accepted password; it bounds the work of the hash function.</summary>
    public int MaxPasswordLength { get; init; } = 256;

    /// <summary>The audit log keeps at most this many entries; the oldest are removed first.</summary>
    public int AuditMaxEntries { get; init; } = 200_000;

    /// <summary>Audit entries older than this are removed.</summary>
    public TimeSpan AuditRetention { get; init; } = TimeSpan.FromDays(365);

    /// <summary>The audit log is pruned after this many new entries.</summary>
    public int AuditPruneEvery { get; init; } = 100;

    /// <summary>The time of use of an API token is written at most this often, so that a busy token does not write on every request.</summary>
    public TimeSpan TokenUseGranularity { get; init; } = TimeSpan.FromMinutes(1);
}

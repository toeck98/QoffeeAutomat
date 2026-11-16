namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>Tunable limits of the management API authentication (PLAN.md S09 item 1). The defaults are the product values.</summary>
public sealed record AuthOptions
{
    /// <summary>A session ends after this long without a request.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(8);

    /// <summary>A session ends this long after sign-in, however active it is.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The most sessions kept at once; the oldest is dropped beyond that.</summary>
    public int MaxSessions { get; init; } = 32;

    /// <summary>Failed attempts from one address within <see cref="FailureWindow"/> that cause a lockout of that address.</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>Failed attempts from all addresses together within <see cref="FailureWindow"/> that lock sign-in for everybody.</summary>
    public int MaxFailedAttemptsOverall { get; init; } = 30;

    /// <summary>The period in which failed attempts are counted.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a lockout lasts.</summary>
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The shortest accepted password.</summary>
    public int MinPasswordLength { get; init; } = 10;

    /// <summary>The longest accepted password; it bounds the work of the hash function.</summary>
    public int MaxPasswordLength { get; init; } = 256;
}

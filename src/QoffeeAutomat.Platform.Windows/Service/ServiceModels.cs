namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>The fixed facts of the Windows service (PLAN.md S11).</summary>
public static class ServiceDefaults
{
    /// <summary>The service name, which is also the name of the Event Log source.</summary>
    public const string Name = "QoffeeAutomat";

    /// <summary>The name shown in the Services console.</summary>
    public const string DisplayName = "QoffeeAutomat Agent";

    /// <summary>The description shown in the Services console.</summary>
    public const string Description = "Lets Home Assistant monitor this PC and run commands on it (QoffeeLink).";

    /// <summary>The account the service runs under.</summary>
    public const string Account = "LocalSystem";

    /// <summary>The Event Log that the agent writes to.</summary>
    public const string EventLogName = "Application";

    /// <summary>Seconds after which the failure count is reset.</summary>
    public const int FailureResetSeconds = 86400;

    /// <summary>How long the control manager waits before it restarts the service after the first and the second failure.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long it waits after the third and later failures.</summary>
    public static readonly TimeSpan LongRestartDelay = TimeSpan.FromSeconds(60);

    /// <summary>How long <c>service start</c> and <c>service stop</c> wait for the service to reach its state.</summary>
    public static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(45);
}

/// <summary>What the service control manager is told when the service is created.</summary>
/// <param name="Name">The service name.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="CommandLine">The complete command line: the quoted executable and its arguments.</param>
public sealed record ServiceDefinition(string Name, string DisplayName, string Description, string CommandLine);

/// <summary>The states of a service as reported by the control manager.</summary>
public enum ServiceRunState
{
    /// <summary>The service does not run.</summary>
    Stopped,

    /// <summary>The service is starting.</summary>
    StartPending,

    /// <summary>The service is stopping.</summary>
    StopPending,

    /// <summary>The service runs.</summary>
    Running,

    /// <summary>Another state (paused, or a state change in progress).</summary>
    Other,
}

/// <summary>An installed service.</summary>
/// <param name="Name">The service name.</param>
/// <param name="State">The current state.</param>
/// <param name="Account">The account, for example <c>LocalSystem</c>; null when unknown.</param>
/// <param name="StartsAutomatically">True when the service starts at boot.</param>
/// <param name="DelayedStart">True when the automatic start is delayed.</param>
/// <param name="CommandLine">The command line registered for the service; null when unknown.</param>
public sealed record ServiceInfo(
    string Name,
    ServiceRunState State,
    string? Account,
    bool StartsAutomatically,
    bool DelayedStart,
    string? CommandLine);

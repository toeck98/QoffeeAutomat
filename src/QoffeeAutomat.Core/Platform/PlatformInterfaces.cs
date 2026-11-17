using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Core.Platform;

/// <summary>
/// Supplies metrics (PLAN.md S06). A provider describes the metrics it knows and samples the ones
/// that are asked for. Values are <see cref="double"/>, <see cref="bool"/> or <see cref="string"/>;
/// <c>null</c> means "unavailable". Timing is the caller's business: the metric engine decides when
/// to sample, and providers use the injected <see cref="TimeProvider"/> where they need a clock.
/// </summary>
public interface IMetricProvider
{
    /// <summary>Describes every metric of the provider, including those that are not available right now.</summary>
    /// <returns>The descriptors, with stable ids.</returns>
    IReadOnlyList<MetricDescriptor> Describe();

    /// <summary>Samples metrics.</summary>
    /// <param name="metricIds">The ids to sample; ids that the provider does not know are ignored.</param>
    /// <param name="cancellationToken">Cancels the sampling.</param>
    /// <returns>The values by metric id; a known id with value null is unavailable.</returns>
    ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// A metric provider whose set of metrics changes while the agent runs, for example one metric set per drive letter
/// (PLAN.md S06, T304). The metric engine asks it to <see cref="IMetricProvider.Describe"/> again whenever
/// <see cref="DescriptorsChanged"/> is raised and rebuilds its catalog, so that the manifest revision follows.
/// </summary>
public interface IDynamicMetricProvider : IMetricProvider
{
    /// <summary>Raised after the result of <see cref="IMetricProvider.Describe"/> changed. Handlers return quickly; <c>Describe</c> must not block.</summary>
    event EventHandler? DescriptorsChanged;
}

/// <summary>A built-in action as offered by a platform (PLAN.md S11), for example <c>power.shutdown</c>.</summary>
/// <param name="Name">The action name used in <c>action.name</c> of a command, for example <c>power.shutdown</c>.</param>
/// <param name="Title">A short English title for the UI.</param>
/// <param name="RunAs">Where the action must run: <c>system</c> or <c>user</c> (see <see cref="RunAsValues"/>).</param>
/// <param name="Parameters">The variables of the action; the engine validates values against them.</param>
/// <param name="Description">An optional longer description.</param>
public sealed record BuiltinActionDescriptor(
    string Name,
    string Title,
    string RunAs,
    IReadOnlyList<ParameterDefinition> Parameters,
    string? Description = null);

/// <summary>Lists and executes the built-in actions of a platform.</summary>
public interface IBuiltinActionProvider
{
    /// <summary>The actions that this platform offers.</summary>
    IReadOnlyList<BuiltinActionDescriptor> Actions { get; }

    /// <summary>Executes an action.</summary>
    /// <param name="name">The action name; one of <see cref="Actions"/>.</param>
    /// <param name="variables">The validated variables as produced by the variable binder (string, long, double or bool).</param>
    /// <param name="cancellationToken">Cancels the action.</param>
    /// <returns>The outcome; an unknown name yields <see cref="ErrorCodes.BadRequest"/>.</returns>
    ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken);
}

/// <summary>
/// The link to the interactive user's session (PLAN.md D-009, S11): runs commands in the user's
/// context, reports the session state and provides the metrics that only the session can see.
/// On Windows this is the named pipe to the SessionHost.
/// </summary>
public interface ISessionBridge
{
    /// <summary>The current session state.</summary>
    SessionState State { get; }

    /// <summary>Raised after <see cref="State"/> changed.</summary>
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised after the result of <see cref="DescribeUserMetrics"/> changed, for example because the audio devices of the session
    /// changed and with them the options of the device metrics (T713). The metric engine describes the metrics again and the
    /// manifest revision follows. Handlers return quickly. A bridge whose descriptors never change need not implement it.
    /// </summary>
    event EventHandler? UserMetricsChanged
    {
        add { }
        remove { }
    }

    /// <summary>Describes the metrics that come from the session (context <c>user</c>).</summary>
    /// <returns>The descriptors.</returns>
    IReadOnlyList<MetricDescriptor> DescribeUserMetrics();

    /// <summary>Samples user metrics; all of them are unavailable (null) while no session is ready.</summary>
    /// <param name="metricIds">The ids to sample.</param>
    /// <param name="cancellationToken">Cancels the sampling.</param>
    /// <returns>The values by metric id.</returns>
    ValueTask<IReadOnlyDictionary<string, object?>> SampleUserMetricsAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken);

    /// <summary>Runs a process in the user's context (<c>runAs: user</c>).</summary>
    /// <param name="request">What to start.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The outcome; <see cref="ErrorCodes.NoSession"/> while no session is ready.</returns>
    ValueTask<ExecutionResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>Power state changes of the computer (suspend, resume, shutdown).</summary>
public interface IPowerEvents
{
    /// <summary>Raised when the power state changes. Handlers must return quickly.</summary>
    event EventHandler<PowerEventArgs>? PowerChanged;
}

/// <summary>Announces the agent on the local network so Home Assistant can discover it (zeroconf).</summary>
public interface IServiceAdvertiser
{
    /// <summary>Starts or replaces the advertisement.</summary>
    /// <param name="advertisement">What to announce.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the announcement is active.</returns>
    ValueTask StartAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken);

    /// <summary>Withdraws the advertisement; nothing happens when none is active.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the announcement is gone.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Starts a process on the user's desktop with the elevated token of an administrator (<c>runAs: user_elevated</c>, D-036).</summary>
public interface IElevatedLauncher
{
    /// <summary>Starts the process.</summary>
    /// <param name="request">What to start.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    /// The outcome; <see cref="ErrorCodes.NoSession"/> when nobody is logged in and
    /// <see cref="ErrorCodes.ElevationUnavailable"/> when the user is not an administrator.
    /// </returns>
    ValueTask<ExecutionResult> StartAsync(ProcessLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>Facts about this computer and the identity of the agent on it.</summary>
public interface IPlatformInfo
{
    /// <summary>The host name.</summary>
    string Hostname { get; }

    /// <summary>The MAC addresses of the physical adapters, upper case with colons.</summary>
    IReadOnlyList<string> MacAddresses { get; }

    /// <summary>The operating system.</summary>
    OsInfo Os { get; }

    /// <summary>
    /// An id that changes with every operating system start and stays the same when only the agent
    /// restarts (<c>hello.boot_id</c>, PLAN.md S07).
    /// </summary>
    string BootId { get; }

    /// <summary>Returns the persistent agent id, creating and storing it on first use.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent id; stable for the life of the installation.</returns>
    ValueTask<string> GetOrCreateAgentIdAsync(CancellationToken cancellationToken);
}

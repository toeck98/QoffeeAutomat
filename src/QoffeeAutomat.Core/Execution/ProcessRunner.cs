using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Execution;

/// <summary>Settings of <see cref="ProcessRunner"/>.</summary>
public sealed class ProcessRunnerOptions
{
    /// <summary>The cap per stream: 64 KiB (PLAN.md S06, <c>captureOutput</c>).</summary>
    public const int DefaultOutputLimitBytes = 64 * 1024;

    /// <summary>The most bytes of standard output, and separately of standard error, that are kept; the excess is dropped and flagged.</summary>
    public int OutputLimitBytes { get; init; } = DefaultOutputLimitBytes;

    /// <summary>
    /// After the process ended, how long to wait for its output to reach the end. A descendant that
    /// keeps the pipes open longer cannot hold the result back beyond this.
    /// </summary>
    public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>After the kill, how long to wait for the process to be gone before the result is reported anyway.</summary>
    public TimeSpan KillWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How the process tree is killed; null uses <see cref="ProcessContainments.CreateDefault"/>. The Windows platform supplies a job object.</summary>
    public IProcessContainment? Containment { get; init; }
}

/// <summary>
/// The default <see cref="IProcessRunner"/> (PLAN.md T206): starts the executable directly, never
/// through a shell, so every argument arrives as one argv entry whatever characters it contains
/// (D-012). Standard input is closed at once, so a child that reads it sees the end of input. On
/// timeout or cancellation the whole process tree is killed through the
/// <see cref="IProcessContainment"/>. All timing uses the <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// A process that exits on its own leaves its descendants alone, like a shell that starts a
/// background job; only a timeout or a cancellation kills the tree. With <c>WaitForExit = false</c>
/// the process is started without containment, timeout or output capture and keeps running.
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(40);

    private readonly TimeProvider _time;
    private readonly ProcessRunnerOptions _options;
    private readonly IProcessContainment _containment;

    /// <summary>Creates the runner.</summary>
    /// <param name="time">The clock for the timeout and the duration; null uses the system clock.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    public ProcessRunner(TimeProvider? time = null, ProcessRunnerOptions? options = null)
    {
        _time = time ?? TimeProvider.System;
        _options = options ?? new ProcessRunnerOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.OutputLimitBytes, nameof(options));
        _containment = _options.Containment ?? ProcessContainments.CreateDefault();
    }

    /// <summary>The containment in use.</summary>
    public IProcessContainment Containment => _containment;

    /// <inheritdoc />
    public IProcessRun Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentNullException.ThrowIfNull(request.EnvironmentVariables);
        if (request.WaitForExit && (request.Timeout <= TimeSpan.Zero || request.Timeout > MaxTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The timeout must be positive and at most 40 days.");
        }

        return new ProcessRun(request, _time, _options, _containment, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        await using IProcessRun run = Start(request, cancellationToken);
        return await run.Completion;
    }
}

using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Metrics.Custom;

/// <summary>Settings of <see cref="CustomSensorProvider"/>.</summary>
public sealed class CustomSensorOptions
{
    /// <summary>
    /// The longest a script sensor may run. The effective timeout is the sensor's interval, but never more than
    /// this; the default stays below the 30 seconds that the metric engine grants a provider call.
    /// </summary>
    public TimeSpan MaxScriptTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>The longest error text that is kept for the UI.</summary>
    public int MaxErrorLength { get; init; } = 300;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxScriptTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxErrorLength, 10);
    }
}

/// <summary>What the UI shows about one custom sensor: whether it delivers values and why not.</summary>
/// <param name="SensorId">The sensor document id.</param>
/// <param name="MetricId">The metric id, <c>custom.&lt;id&gt;</c>.</param>
/// <param name="IsAvailable">True when the last sample produced a value.</param>
/// <param name="LastSampleAt">When the sensor was last sampled; null when never.</param>
/// <param name="LastSuccessAt">When the last value was produced; null when never.</param>
/// <param name="LastError">Why the last sample failed; null when it succeeded.</param>
/// <param name="LastErrorAt">When the last sample failed; null when it succeeded.</param>
/// <param name="ConsecutiveFailures">How many samples in a row failed.</param>
/// <param name="LastDuration">How long the last sample took.</param>
public sealed record CustomSensorStatus(
    string SensorId,
    string MetricId,
    bool IsAvailable,
    DateTimeOffset? LastSampleAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    int ConsecutiveFailures,
    TimeSpan? LastDuration);

/// <summary>The outcome of a dry run of a sensor (<see cref="CustomSensorProvider.TestAsync"/>).</summary>
/// <param name="Succeeded">True when the sensor produced a value.</param>
/// <param name="Value">The value (a <see cref="double"/>, <see cref="bool"/> or <see cref="string"/>); null when the sensor failed.</param>
/// <param name="Error">Why the sensor failed; null when it succeeded.</param>
/// <param name="Duration">How long the sample took.</param>
public sealed record SensorTestOutcome(bool Succeeded, object? Value, string? Error, TimeSpan Duration);

/// <summary>
/// Samples the custom sensors of the config store that need no operating-system API (PLAN.md S06, T215):
/// <c>powershell</c> sensors run through the PowerShell action (<see cref="PowerShellRunner"/>) and
/// <c>process_running</c> sensors look at the process list. Pass it to the <see cref="MetricEngine"/> as its custom
/// sensor provider. The engine only calls it for sensors somebody is subscribed to (PLAN.md D-007), so a script
/// never runs without a subscriber.
/// <list type="bullet">
/// <item>The documents are read from the effective configuration at every sample, so edits apply without a restart.</item>
/// <item>A script gets a timeout no longer than the sensor's interval (and no longer than <see cref="CustomSensorOptions.MaxScriptTimeout"/>); its process tree is killed when the time is up. The interval of a script sensor is at least <see cref="MetricEngineOptions.MinimumScriptIntervalSeconds"/>.</item>
/// <item>The last non-empty output line is parsed according to the value type, see <see cref="SensorOutputParser"/>.</item>
/// <item>A sensor that fails (non-zero exit code, timeout, unusable output, missing engine) is unavailable (<c>null</c>) and its error is recorded in <see cref="GetStatus"/>; the other sensors of the same sample are unaffected, and scripts of one sample run concurrently.</item>
/// </list>
/// Sensor types that need the operating system (<c>perf_counter</c>, <c>cim_query</c>, <c>service_state</c>) are sampled
/// by the <see cref="ISensorSourceHandler"/>s that the platform passes in (T307); without a handler, and for the
/// externally set <c>state</c> type, those sensors are unavailable with an explanation, and <see cref="TypeSupport"/>
/// refuses them when they are saved or tested.
/// </summary>
public sealed class CustomSensorProvider : IMetricProvider
{
    private static readonly Dictionary<string, object> NoVariables = [];

    private readonly object _gate = new();
    private readonly IConfigStore _config;
    private readonly PowerShellRunner _powerShell;
    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly IProcessNameSource _processes;
    private readonly CustomSensorOptions _options;
    private readonly MetricEngineOptions _engineOptions;
    private readonly SensorTypeSupport _support;
    private readonly Dictionary<string, CustomSensorStatus> _status = new(StringComparer.Ordinal);

    /// <summary>Creates the provider.</summary>
    /// <param name="config">The configuration store; its effective view lists the sensors.</param>
    /// <param name="powerShell">Runs the scripts of <c>powershell</c> sensors.</param>
    /// <param name="launcher">Starts the engine process, as for a command that runs as <c>system</c>.</param>
    /// <param name="time">The clock for timeouts, durations and timestamps.</param>
    /// <param name="processes">The process list; null reads the real one.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    /// <param name="engineOptions">The options of the metric engine, so that intervals are computed the same way; null uses the defaults.</param>
    /// <param name="handlers">The sensor types that the platform adds (<c>perf_counter</c>, <c>cim_query</c>, <c>service_state</c> on Windows); null for none.</param>
    public CustomSensorProvider(
        IConfigStore config,
        PowerShellRunner powerShell,
        IProcessLauncher launcher,
        TimeProvider time,
        IProcessNameSource? processes = null,
        CustomSensorOptions? options = null,
        MetricEngineOptions? engineOptions = null,
        IEnumerable<ISensorSourceHandler>? handlers = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(powerShell);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(time);

        _config = config;
        _powerShell = powerShell;
        _launcher = launcher;
        _time = time;
        _processes = processes ?? new SystemProcessNameSource();
        _options = options ?? new CustomSensorOptions();
        _options.Validate();
        _engineOptions = engineOptions ?? new MetricEngineOptions();
        _support = new SensorTypeSupport(handlers);
    }

    /// <summary>
    /// Which sensor types this agent can sample. The agent validates sensors against it when they are saved and tested, so
    /// that a Windows-only type on another platform is refused with <see cref="QoffeeAutomat.Core.Validation.ValidationCodes.Unsupported"/>.
    /// </summary>
    public SensorTypeSupport TypeSupport => _support;

    /// <summary>Describes the metrics of the sensors that exist now (<c>custom.&lt;id&gt;</c>), with the interval that really applies.</summary>
    /// <returns>The descriptors, ordered by id.</returns>
    public IReadOnlyList<MetricDescriptor> Describe()
    {
        return
        [
            .. _config.Effective.List<SensorDocument>()
                .Select(sensor => MetricCatalogBuilder.FromSensor(MetricCatalogBuilder.CustomPrefix + sensor.Id, sensor, _engineOptions).Descriptor),
        ];
    }

    /// <summary>
    /// Samples custom sensors. Ids that are not <c>custom.&lt;id&gt;</c> of an existing sensor are ignored. A sensor that
    /// fails is reported as <c>null</c> (unavailable); this method does not throw for a failing sensor.
    /// </summary>
    /// <param name="metricIds">The metric ids to sample.</param>
    /// <param name="cancellationToken">Cancels the samples; running scripts are killed.</param>
    /// <returns>The values by metric id.</returns>
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        IConfigView view = _config.Effective;
        var sensors = new List<(string MetricId, SensorDocument Sensor)>();
        foreach (string metricId in metricIds)
        {
            if (metricId.StartsWith(MetricCatalogBuilder.CustomPrefix, StringComparison.Ordinal)
                && view.GetDocument<SensorDocument>(metricId[MetricCatalogBuilder.CustomPrefix.Length..]) is { } sensor)
            {
                sensors.Add((metricId, sensor));
            }
        }

        IReadOnlySet<string>? processNames = null;
        string? processListFailure = null;
        if (sensors.Any(entry => entry.Sensor.Source is ProcessRunningSensorSource))
        {
            (processNames, processListFailure) = ReadProcessList();
        }

        SensorOutcome[] outcomes = await Task.WhenAll(
            sensors.Select(entry => EvaluateAsync(entry.MetricId, entry.Sensor, processNames, processListFailure, cancellationToken)));

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (SensorOutcome outcome in outcomes)
        {
            values[outcome.MetricId] = outcome.Value;
        }

        return values;
    }

    /// <summary>
    /// Samples a sensor document that is not saved once (the dry run of the editor, <c>POST /sensors/test</c>). The
    /// status of the stored sensors is not touched. The caller has validated the document.
    /// </summary>
    /// <param name="sensor">The draft.</param>
    /// <param name="cancellationToken">Cancels the sample; a running script is killed.</param>
    /// <returns>What the sensor reported; a failing sensor is a result with an error, not an exception.</returns>
    public async Task<SensorTestOutcome> TestAsync(SensorDocument sensor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        DateTimeOffset started = _time.GetUtcNow();
        SampleResult result;
        try
        {
            IReadOnlySet<string>? processNames = null;
            string? processListFailure = null;
            if (sensor.Source is ProcessRunningSensorSource)
            {
                (processNames, processListFailure) = ReadProcessList();
            }

            result = sensor.Source switch
            {
                PowerShellSensorSource script => await RunScriptAsync(sensor, script, cancellationToken),
                ProcessRunningSensorSource process => CheckProcess(process, processNames, processListFailure),
                _ => await RunHandlerAsync(sensor, cancellationToken),
            };
        }
#pragma warning disable CA1031 // A failing sensor is reported to the editor, whatever it throws.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            result = SampleResult.Fail("The sensor failed unexpectedly: " + exception.Message);
        }

        TimeSpan duration = _time.GetUtcNow() - started;
        object? value = result.Error is null ? MetricValues.Normalize(result.Value, sensor.Precision) : null;
        return new SensorTestOutcome(result.Error is null, value, result.Error, duration < TimeSpan.Zero ? TimeSpan.Zero : duration);
    }

    /// <summary>The status of one sensor.</summary>
    /// <param name="sensorId">The sensor document id.</param>
    /// <returns>The status, or null when the sensor does not exist or has not been sampled yet.</returns>
    public CustomSensorStatus? GetStatus(string sensorId)
    {
        ArgumentNullException.ThrowIfNull(sensorId);

        if (_config.Effective.GetDocument<SensorDocument>(sensorId) is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _status.GetValueOrDefault(sensorId);
        }
    }

    /// <summary>The status of every sensor that exists and was sampled, ordered by sensor id.</summary>
    /// <returns>The statuses.</returns>
    public IReadOnlyList<CustomSensorStatus> GetStatuses()
    {
        HashSet<string> existing = [.. _config.Effective.List<SensorDocument>().Select(sensor => sensor.Id)];
        lock (_gate)
        {
            foreach (string gone in _status.Keys.Where(id => !existing.Contains(id)).ToList())
            {
                _status.Remove(gone);
            }

            return [.. _status.Values.OrderBy(status => status.SensorId, StringComparer.Ordinal)];
        }
    }

    private (IReadOnlySet<string>? Names, string? Failure) ReadProcessList()
    {
        try
        {
            return (_processes.GetRunningProcessNames(), null);
        }
#pragma warning disable CA1031 // Whatever goes wrong with the process list only makes the process sensors unavailable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return (null, "The process list could not be read: " + exception.Message);
        }
    }

    private async Task<SensorOutcome> EvaluateAsync(
        string metricId,
        SensorDocument sensor,
        IReadOnlySet<string>? processNames,
        string? processListFailure,
        CancellationToken cancellationToken)
    {
        DateTimeOffset started = _time.GetUtcNow();
        SampleResult result;
        try
        {
            result = sensor.Source switch
            {
                PowerShellSensorSource script => await RunScriptAsync(sensor, script, cancellationToken),
                ProcessRunningSensorSource process => CheckProcess(process, processNames, processListFailure),
                _ => await RunHandlerAsync(sensor, cancellationToken),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down, or the metric engine gave up on this call: this is not a failure of the sensor.
            return new SensorOutcome(metricId, null);
        }
#pragma warning disable CA1031 // A failing sensor must only make itself unavailable, whatever it throws.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            result = SampleResult.Fail("The sensor failed unexpectedly: " + exception.Message);
        }

        Record(sensor, metricId, result, _time.GetUtcNow() - started);
        return new SensorOutcome(metricId, result.Value);
    }

    private static SampleResult CheckProcess(ProcessRunningSensorSource source, IReadOnlySet<string>? names, string? listFailure)
    {
        return names is null
            ? SampleResult.Fail(listFailure ?? "The process list is not available.")
            : SampleResult.Success(ProcessNames.IsRunning(names, source.Process));
    }

    /// <summary>Samples a sensor of a platform type through its handler; a type without handler is unavailable with an explanation.</summary>
    private async Task<SampleResult> RunHandlerAsync(SensorDocument sensor, CancellationToken cancellationToken)
    {
        ISensorSourceHandler? handler = _support.HandlerFor(sensor.Source.Type);
        if (handler is null)
        {
            return SampleResult.Fail($"The sensor type '{sensor.Source.Type}' is not supported by this agent.");
        }

        TimeSpan timeout = _options.MaxScriptTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        SensorParseResult reading;
        try
        {
            reading = await handler.SampleAsync(sensor, linked.Token).AsTask().WaitAsync(timeout, _time, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException || (exception is OperationCanceledException && timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
        {
            return SampleResult.Fail($"The sensor did not finish within {Math.Ceiling(timeout.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)} seconds.");
        }

        return reading.IsValid ? SampleResult.Success(reading.Value!) : SampleResult.Fail(reading.Failure!);
    }

    private async Task<SampleResult> RunScriptAsync(SensorDocument sensor, PowerShellSensorSource source, CancellationToken cancellationToken)
    {
        TimeSpan timeout = ScriptTimeout(sensor);
        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var invocation = new PowerShellInvocation(
            source.Source,
            source.Code,
            source.Script,
            source.Engine,
            NoVariables,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["QA_SENSOR_ID"] = sensor.Id },
            timeout,
            RunAsValues.System,
            CaptureOutput: true);

        ExecutionResult execution;
        try
        {
            execution = await _powerShell.RunAsync(invocation, _launcher, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return SampleResult.Fail(TimeoutMessage(timeout));
        }

        return Interpret(sensor, execution, timeout);
    }

    private SampleResult Interpret(SensorDocument sensor, ExecutionResult execution, TimeSpan timeout)
    {
        switch (execution.ErrorCode)
        {
            case null:
                break;
            case ErrorCodes.Timeout:
                return SampleResult.Fail(TimeoutMessage(timeout));
            case ErrorCodes.Cancelled:
                return SampleResult.Fail("The sample was cancelled.");
            default:
                return SampleResult.Fail(FirstLine(execution.StandardError) ?? $"The script could not be run ({execution.ErrorCode}).");
        }

        if (execution.ExitCode != 0)
        {
            string? detail = FirstLine(execution.StandardError);
            string exit = $"The script exited with code {execution.ExitCode.ToString(CultureInfo.InvariantCulture)}";
            return SampleResult.Fail(detail is null ? exit + "." : exit + ": " + detail);
        }

        if (execution.OutputTruncated)
        {
            return SampleResult.Fail("The script wrote more output than is kept (64 KiB), so its last line cannot be trusted.");
        }

        SensorParseResult parsed = SensorOutputParser.Parse(execution.StandardOutput, sensor.ValueType, sensor.Options);
        return parsed.IsValid ? SampleResult.Success(parsed.Value!) : SampleResult.Fail(parsed.Failure!);
    }

    private TimeSpan ScriptTimeout(SensorDocument sensor)
    {
        double interval = MetricCatalogBuilder.EffectiveSensorInterval(sensor, _engineOptions);
        TimeSpan limit = _options.MaxScriptTimeout;
        return interval > 0 && TimeSpan.FromSeconds(interval) < limit ? TimeSpan.FromSeconds(interval) : limit;
    }

    private static string TimeoutMessage(TimeSpan timeout)
    {
        return $"The script did not finish within {Math.Ceiling(timeout.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)} seconds.";
    }

    private string? FirstLine(string text)
    {
        string? line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null)
        {
            return null;
        }

        return line.Length <= _options.MaxErrorLength ? line : line[.._options.MaxErrorLength] + "...";
    }

    private void Record(SensorDocument sensor, string metricId, SampleResult result, TimeSpan duration)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            _status.TryGetValue(sensor.Id, out CustomSensorStatus? previous);
            _status[sensor.Id] = result.Error is null
                ? new CustomSensorStatus(sensor.Id, metricId, true, now, now, null, null, 0, duration)
                : new CustomSensorStatus(
                    sensor.Id,
                    metricId,
                    false,
                    now,
                    previous?.LastSuccessAt,
                    result.Error,
                    now,
                    (previous?.ConsecutiveFailures ?? 0) + 1,
                    duration);
        }
    }

    private sealed record SensorOutcome(string MetricId, object? Value);

    private sealed class SampleResult
    {
        private SampleResult(object? value, string? error)
        {
            Value = value;
            Error = error;
        }

        public object? Value { get; }

        public string? Error { get; }

        public static SampleResult Success(object value) => new(value, null);

        public static SampleResult Fail(string error) => new(null, error);
    }
}

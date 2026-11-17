using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;

/// <summary>Settings of <see cref="HardwareMetricProvider"/>.</summary>
public sealed class HardwareSensorOptions
{
    /// <summary>
    /// How often the provider looks at the sensors to find out whether the set changed (a drive was plugged in), and how often it tries
    /// again after a failure or while the driver is missing; <see cref="Timeout.InfiniteTimeSpan"/> turns the check off.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The optional hardware metrics (PLAN.md D-023, D-045, ADR 0002, T707): temperatures, fan speeds and power draw of the processor,
/// graphics cards, mainboard and drives.
/// <list type="bullet">
/// <item>It follows the setting <c>hardwareSensors.enabled</c>, which is off by default. While it is off the provider describes no metric, owns no <see cref="IHardwareSource"/> and has never called <see cref="IHardwareSourceFactory.Open"/>, so neither the hardware library nor the kernel driver is loaded.</item>
/// <item>Turning the setting on opens the source in the background; turning it off disposes the source, which closes the library and releases the driver handle, and removes the metrics from the manifest. The driver itself is installed by the user and is never installed or removed by the agent.</item>
/// <item>The metrics are not enabled by default in Home Assistant: a PC has dozens of sensors, and a disabled entity costs nothing (D-007).</item>
/// <item>A failure (library cannot be opened, a read throws) is a state, not a crash: the metrics disappear, the status says why, and the provider tries again on the refresh interval.</item>
/// </list>
/// </summary>
public sealed class HardwareMetricProvider : IDynamicMetricProvider, IHardwareSensorStatusSource, IHostedService, IDisposable
{
    private const string Measurement = "measurement";
    private const string Diagnostic = "diagnostic";
    private const double IntervalSeconds = 5;

    private readonly IConfigStore _config;
    private readonly IHardwareSourceFactory _factory;
    private readonly IHardwareDriverProbe _probe;
    private readonly ILogger _logger;
    private readonly HardwareSensorOptions _options;
    private readonly TimeProvider _time;
    private readonly object _sourceGate = new();
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _stop = new();
    private IHardwareSource? _source;
    private Snapshot _snapshot = Snapshot.Off;
    private ConfigSubscription? _subscription;
    private Task _pump = Task.CompletedTask;
    private ITimer? _timer;
    private int _tickRunning;
    private bool _closed;

    /// <summary>Creates the provider. Nothing is loaded: the provider only reads the setting once it is started.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="factory">Opens the hardware; called only while the setting is on.</param>
    /// <param name="probe">Looks for the kernel driver; called only while the setting is on.</param>
    /// <param name="time">The clock for the refresh timer.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    public HardwareMetricProvider(
        IConfigStore config,
        IHardwareSourceFactory factory,
        IHardwareDriverProbe probe,
        TimeProvider time,
        ILogger logger,
        HardwareSensorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _factory = factory;
        _probe = probe;
        _time = time;
        _logger = logger;
        _options = options ?? new HardwareSensorOptions();
    }

    /// <inheritdoc />
    public event EventHandler? DescriptorsChanged;

    /// <inheritdoc />
    public IReadOnlyList<MetricDescriptor> Describe()
    {
        lock (_stateGate)
        {
            return _snapshot.Descriptors;
        }
    }

    /// <inheritdoc />
    public HardwareSensorStatus GetStatus()
    {
        bool enabled = ReadEnabled();
        Snapshot snapshot;
        lock (_stateGate)
        {
            snapshot = _snapshot;
        }

        if (!enabled)
        {
            return new HardwareSensorStatus(true, false, HardwareSensorStates.Off, _probe.DriverName, null, 0);
        }

        string state = snapshot.State == HardwareSensorStates.Off ? HardwareSensorStates.Starting : snapshot.State;
        return new HardwareSensorStatus(true, true, state, _probe.DriverName, snapshot.DriverInstalled, snapshot.Descriptors.Length, snapshot.Message);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _config.Subscribe();
        ConfigSubscription subscription = _subscription;
        _pump = Task.Run(() => PumpAsync(subscription, _stop.Token), CancellationToken.None);
        if (_options.RefreshInterval != Timeout.InfiniteTimeSpan)
        {
            _timer = _time.CreateTimer(_ => Tick(), null, _options.RefreshInterval, _options.RefreshInterval);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        _subscription?.Dispose();
        await _pump.ConfigureAwait(false);

        bool changed;
        lock (_sourceGate)
        {
            _closed = true;
            changed = CloseSource(logIt: false);
        }

        if (changed)
        {
            DescriptorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Brings the provider in line with the setting now: opens the source when it is on, closes it when it is off, and looks at the
    /// sensors again. The provider calls this when the settings change and on the refresh interval; tests call it directly.
    /// </summary>
    public void Reconcile()
    {
        bool enabled = ReadEnabled();
        bool changed;
        lock (_sourceGate)
        {
            if (_closed)
            {
                return;
            }

            changed = enabled ? OpenOrRefresh() : CloseSource(logIt: true);
        }

        if (changed)
        {
            DescriptorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        Snapshot snapshot;
        lock (_stateGate)
        {
            snapshot = _snapshot;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string id in metricIds.Where(id => snapshot.PathById.ContainsKey(id)))
        {
            values[id] = null;
        }

        if (values.Count == 0)
        {
            return values;
        }

        // The library is not thread safe and talks to hardware: one reader at a time, off the thread of the metric engine.
        IReadOnlyList<HardwareSensorReading> readings = await Task.Run(ReadNow, cancellationToken).ConfigureAwait(false);
        foreach (HardwareSensorReading reading in readings)
        {
            if (snapshot.IdByPath.TryGetValue(reading.Path, out string? id) && values.ContainsKey(id))
            {
                values[id] = Clean(reading.Value);
            }
        }

        return values;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _stop.Dispose();
        lock (_sourceGate)
        {
            _closed = true;
            CloseSource(logIt: false);
        }
    }

    private static double? Clean(double? value)
    {
        return value is { } number && double.IsFinite(number) ? Math.Round(number, 2) : null;
    }

    private static MetricDescriptor ToDescriptor(HardwareSensorReading reading, string id)
    {
        (string unit, string? deviceClass, string icon, int precision, double deadband) = reading.Kind switch
        {
            HardwareSensorKind.Temperature => ("°C", "temperature", "mdi:thermometer", 1, 0.5),
            HardwareSensorKind.Fan => ("rpm", null, "mdi:fan", 0, 20.0),
            _ => ("W", "power", "mdi:flash", 1, 0.5),
        };

        return new MetricDescriptor(
            id,
            HardwareSensorIds.DisplayName(reading),
            "sensor",
            "number",
            Unit: unit,
            DeviceClass: deviceClass,
            StateClass: Measurement,
            EntityCategory: Diagnostic,
            Icon: icon,
            Precision: precision,
            EnabledDefault: false,
            IntervalSeconds: IntervalSeconds,
            Deadband: deadband,
            Context: "system");
    }

    private bool ReadEnabled()
    {
        return _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.EffectiveHardwareSensorsEnabled ?? false;
    }

    private IReadOnlyList<HardwareSensorReading> ReadNow()
    {
        lock (_sourceGate)
        {
            if (_source is null)
            {
                return [];
            }

            try
            {
                return _source.Read();
            }
#pragma warning disable CA1031 // A failing read is a state of the provider, not a crash; the refresh looks again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                HardwareLog.Failed(_logger, exception.Message);
                return [];
            }
        }
    }

    /// <summary>Opens the source when there is none, picks up a driver that was installed in the meantime, and looks at the sensors. Called under <see cref="_sourceGate"/>.</summary>
    private bool OpenOrRefresh()
    {
        bool driverInstalled = _probe.IsInstalled();
        string? failure = null;

        if (_source is not null && driverInstalled && _snapshot.DriverInstalled == false)
        {
            // The driver appeared after the library was opened without it: open again so that the library uses it.
            _source.Dispose();
            _source = null;
        }

        if (_source is null)
        {
            HardwareLog.Opening(_logger, _probe.DriverName, driverInstalled);
            try
            {
                _source = _factory.Open();
            }
#pragma warning disable CA1031 // Whatever the library throws, the agent keeps running and the status says why.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failure = exception.Message;
            }
        }

        IReadOnlyList<HardwareSensorReading> readings = [];
        if (_source is not null)
        {
            try
            {
                readings = _source.Read();
            }
#pragma warning disable CA1031 // As above.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failure = exception.Message;
                _source.Dispose();
                _source = null;
            }
        }

        if (failure is not null)
        {
            // Said once per outcome, not on every retry.
            if (_snapshot.State != HardwareSensorStates.Failed || !string.Equals(_snapshot.Message, failure, StringComparison.Ordinal))
            {
                HardwareLog.Failed(_logger, failure);
            }

            return Publish(Snapshot.Broken(failure, driverInstalled));
        }

        return Publish(Build(readings, driverInstalled));
    }

    /// <summary>Disposes the source, if any, and publishes the empty state. Called under <see cref="_sourceGate"/>.</summary>
    private bool CloseSource(bool logIt)
    {
        bool wasOpen = _source is not null;
        if (_source is not null)
        {
            try
            {
                _source.Dispose();
            }
#pragma warning disable CA1031 // Closing must not throw: the setting is off either way.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                HardwareLog.Failed(_logger, exception.Message);
            }

            _source = null;
        }

        if (wasOpen && logIt)
        {
            HardwareLog.Closed(_logger);
        }

        return Publish(Snapshot.Off);
    }

    private Snapshot Build(IReadOnlyList<HardwareSensorReading> readings, bool driverInstalled)
    {
        IReadOnlyList<string> ids = HardwareSensorIds.Assign(readings);
        var descriptors = new MetricDescriptor[readings.Count];
        var idByPath = new Dictionary<string, string>(readings.Count, StringComparer.Ordinal);
        for (int index = 0; index < readings.Count; index++)
        {
            descriptors[index] = ToDescriptor(readings[index], ids[index]);
            idByPath[readings[index].Path] = ids[index];
        }

        string? message = null;
        string state = HardwareSensorStates.Running;
        if (!driverInstalled)
        {
            state = HardwareSensorStates.DriverMissing;
            message = string.Create(CultureInfo.InvariantCulture, $"The kernel driver {_probe.DriverName} is not installed, so processor and mainboard sensors are missing.");
        }

        // The refresh looks at the sensors every minute; only a change of state is worth a log line.
        if (_snapshot.State != state)
        {
            if (!driverInstalled)
            {
                HardwareLog.DriverMissing(_logger, _probe.DriverName);
            }

            HardwareLog.Opened(_logger, descriptors.Length);
        }

        return new Snapshot(state, driverInstalled, message, descriptors, idByPath.ToFrozenDictionary(StringComparer.Ordinal), idByPath.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal));
    }

    /// <summary>Stores the new state; true when the set of metrics changed.</summary>
    private bool Publish(Snapshot next)
    {
        lock (_stateGate)
        {
            bool changed = !string.Equals(_snapshot.Signature, next.Signature, StringComparison.Ordinal);
            _snapshot = next;
            return changed;
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _tickRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // Cheap while the setting is off (a read of the setting and nothing else); it also covers a settings document that was
                // loaded after the start of this service.
                Reconcile();
            }
#pragma warning disable CA1031 // A timer callback must not throw; the next tick looks again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                HardwareLog.Failed(_logger, exception.Message);
            }
            finally
            {
                Volatile.Write(ref _tickRunning, 0);
            }
        });
    }

    private async Task PumpAsync(ConfigSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            // The state at start: the setting may already be on.
            await Task.Run(Reconcile, cancellationToken).ConfigureAwait(false);
            await foreach (ConfigChange change in subscription.Changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (change.Kind == ConfigKind.Settings)
                {
                    await Task.Run(Reconcile, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The agent is stopping.
        }
    }

    /// <summary>What the provider currently offers; replaced as a whole under <see cref="_stateGate"/>.</summary>
    private sealed class Snapshot
    {
        public Snapshot(
            string state,
            bool? driverInstalled,
            string? message,
            MetricDescriptor[] descriptors,
            FrozenDictionary<string, string> idByPath,
            FrozenDictionary<string, string> pathById)
        {
            State = state;
            DriverInstalled = driverInstalled;
            Message = message;
            Descriptors = descriptors;
            IdByPath = idByPath;
            PathById = pathById;
            Signature = string.Join('\n', descriptors.Select(descriptor => descriptor.Id + "|" + descriptor.Name));
        }

        public static Snapshot Off { get; } = new(
            HardwareSensorStates.Off,
            null,
            null,
            [],
            FrozenDictionary<string, string>.Empty,
            FrozenDictionary<string, string>.Empty);

        public string State { get; }

        public bool? DriverInstalled { get; }

        public string? Message { get; }

        public MetricDescriptor[] Descriptors { get; }

        public FrozenDictionary<string, string> IdByPath { get; }

        public FrozenDictionary<string, string> PathById { get; }

        public string Signature { get; }

        public static Snapshot Broken(string reason, bool driverInstalled) => new(
            HardwareSensorStates.Failed,
            driverInstalled,
            reason,
            [],
            FrozenDictionary<string, string>.Empty,
            FrozenDictionary<string, string>.Empty);
    }
}

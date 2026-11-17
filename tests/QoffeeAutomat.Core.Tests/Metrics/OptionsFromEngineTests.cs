using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Manifest;
using QoffeeAutomat.Platform.Simulated;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Tests.Metrics;

/// <summary>A session bridge whose device metric changes its options while the engine runs, like the audio devices of a PC do.</summary>
internal sealed class DeviceSessionBridge : ISessionBridge
{
    public const string MetricId = "audio.output_device";

    private readonly object _gate = new();
    private string[] _devices;

    public DeviceSessionBridge(params string[] devices)
    {
        _devices = devices;
    }

    public SessionState State { get; } = new(true, "user", false, false);

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? UserMetricsChanged;

    /// <summary>Replaces the devices and tells the engine, the way the Windows bridge does when the session helper reports them.</summary>
    public void Change(params string[] devices)
    {
        lock (_gate)
        {
            _devices = devices;
        }

        UserMetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MetricDescriptor> DescribeUserMetrics()
    {
        lock (_gate)
        {
            return
            [
                new MetricDescriptor(
                    MetricId,
                    "Audio output device",
                    "sensor",
                    "enum",
                    Options: _devices.Length == 0 ? null : _devices,
                    EnabledDefault: true,
                    IntervalSeconds: 30,
                    Context: "user"),
            ];
        }
    }

    public ValueTask<IReadOnlyDictionary<string, object?>> SampleUserMetricsAsync(IReadOnlyCollection<string> metricIds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string id in metricIds)
            {
                values[id] = id == MetricId ? (_devices.Length > 0 ? _devices[0] : null) : null;
            }

            return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
        }
    }

    public ValueTask<ExecutionResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.NoSession, "No process runs in this test."));
    }
}

/// <summary>
/// Dynamic options of a session metric reach the manifest (PLAN.md S06, S11, T713): the bridge reports that the options changed,
/// the engine describes the metrics again and raises <c>CatalogChanged</c>, and a variable with <c>optionsFrom</c> follows, so that
/// the manifest revision changes when a device appears or disappears.
/// </summary>
[Trait("Category", "OptionsFrom")]
public sealed class OptionsFromEngineTests
{
    private static CommandDocument DeviceCommand() => new(
        DocumentSchema.CurrentVersion,
        "audio_output_device",
        "Audio output device",
        true,
        true,
        RunAsValues.User,
        new BuiltinAction("audio.set_default_output"),
        Parameters: [new ParameterDefinition("Device", ParameterTypes.Choice, Required: true, OptionsFrom: DeviceSessionBridge.MetricId)],
        HaControl: new HaControl(HaControlTypes.Select, "Device", DeviceSessionBridge.MetricId));

    private static AgentInfo Agent() => new(
        "5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77",
        "Gaming-PC",
        "1.0.0",
        "windows",
        "x64",
        "GAMING-PC",
        ["A4:B1:C1:00:11:22"],
        "10.0.26200",
        new Uri("https://gaming-pc.local:8481/"));

    private static LinkManifest BuildManifest(MetricEngine engine) =>
        ManifestBuilder.Build(Agent(), engine.Catalog, [DeviceCommand()], EventCatalog.Types);

    private static IReadOnlyList<string>? OptionsInCatalog(MetricEngine engine) =>
        engine.Catalog.Single(entry => entry.Id == DeviceSessionBridge.MetricId).Descriptor.Options;

    [Fact]
    public async Task The_options_of_a_session_metric_are_in_the_catalog()
    {
        var bridge = new DeviceSessionBridge("Speakers");
        await using var harness = await MetricHarness.CreateAsync(new SimulatedClock(), [], session: bridge);

        Assert.Equal(["Speakers"], OptionsInCatalog(harness.Engine));
    }

    [Fact]
    public async Task A_device_that_appears_changes_the_options_in_the_catalog_and_raises_catalog_changed()
    {
        var bridge = new DeviceSessionBridge("Speakers");
        await using var harness = await MetricHarness.CreateAsync(new SimulatedClock(), [], session: bridge);
        int changes = 0;
        harness.Engine.CatalogChanged += (_, _) => Interlocked.Increment(ref changes);

        bridge.Change("Headphones", "Speakers");
        await harness.PollAsync();

        Assert.Equal(["Headphones", "Speakers"], OptionsInCatalog(harness.Engine));
        Assert.True(changes >= 1, "The catalog change must be announced so that the manifest is built again.");
    }

    [Fact]
    public async Task The_manifest_variable_and_its_revision_follow_the_devices()
    {
        var bridge = new DeviceSessionBridge("Speakers");
        await using var harness = await MetricHarness.CreateAsync(new SimulatedClock(), [], session: bridge);
        LinkManifest before = BuildManifest(harness.Engine);

        bridge.Change("Headphones", "Speakers");
        await harness.PollAsync();
        LinkManifest plugged = BuildManifest(harness.Engine);

        bridge.Change("Speakers");
        await harness.PollAsync();
        LinkManifest unplugged = BuildManifest(harness.Engine);

        Assert.Equal(["Speakers"], before.Commands.Single().Vars![0].Options);
        Assert.Equal(["Headphones", "Speakers"], plugged.Commands.Single().Vars![0].Options);
        Assert.NotEqual(before.Rev, plugged.Rev);
        Assert.Equal(before.Rev, unplugged.Rev);
        Assert.Equal(["Speakers"], before.Metrics.Single(metric => metric.Id == DeviceSessionBridge.MetricId).Options);
        Assert.Equal(["Headphones", "Speakers"], plugged.Metrics.Single(metric => metric.Id == DeviceSessionBridge.MetricId).Options);
    }

    [Fact]
    public async Task Without_any_device_the_metric_and_the_variable_have_no_options_and_the_manifest_stays_valid()
    {
        var bridge = new DeviceSessionBridge();
        await using var harness = await MetricHarness.CreateAsync(new SimulatedClock(), [], session: bridge);

        LinkManifest manifest = BuildManifest(harness.Engine);

        Assert.Null(manifest.Metrics.Single(metric => metric.Id == DeviceSessionBridge.MetricId).Options);
        Assert.Null(manifest.Commands.Single().Vars![0].Options);
        Assert.Empty(ManifestTestSupport.SchemaErrors(manifest));
    }

    [Fact]
    public async Task A_bridge_that_never_reports_changes_keeps_working_as_before()
    {
        // The simulated bridge does not implement UserMetricsChanged (the default of the interface does nothing).
        var bridge = new SimulatedSessionBridge(new SimulatedClock());
        await using var harness = await MetricHarness.CreateAsync(new SimulatedClock(), [], session: bridge);

        Assert.NotEmpty(harness.Engine.Catalog);
    }

    [Fact]
    public async Task Disposing_the_engine_stops_listening_to_the_bridge()
    {
        var bridge = new DeviceSessionBridge("Speakers");
        MetricHarness harness = await MetricHarness.CreateAsync(new SimulatedClock(), [], session: bridge);
        MetricEngine engine = harness.Engine;
        await harness.DisposeAsync();

        Exception? thrown = Record.Exception(() => bridge.Change("Headphones"));

        Assert.Null(thrown);
        Assert.Equal(["Speakers"], OptionsInCatalog(engine));
    }
}

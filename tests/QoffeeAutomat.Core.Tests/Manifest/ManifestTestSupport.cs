using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Tests.Schema;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Metrics;
using QoffeeAutomat.Platform.Simulated;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Tests.Manifest;

/// <summary>Builders and helpers shared by the manifest tests.</summary>
internal static class ManifestTestSupport
{
    private const string SchemaBase = "https://github.com/toeck98/QoffeeAutomat/protocol/schema/";

    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<SchemaSet> Schemas = new(() => SchemaSet.Load(Path.Combine(Root.Value, "protocol", "schema")));

    public static AgentInfo Agent(string name = "Gaming-PC") => new(
        "5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77",
        name,
        "1.0.0",
        "windows",
        "x64",
        "GAMING-PC",
        ["A4:B1:C1:00:11:22"],
        "10.0.26200",
        new Uri("https://gaming-pc.local:8481/"));

    /// <summary>The errors that protocol/schema/manifest.schema.json reports for a manifest; empty when it is valid.</summary>
    public static IReadOnlyList<string> SchemaErrors(LinkManifest manifest)
    {
        JsonElement element = LinkCodec.ToElement(manifest);
        return Schemas.Value.Validate(element, SchemaBase + "manifest.schema.json");
    }

    public static MetricCatalogEntry Metric(
        string id,
        bool exposeToHa = true,
        bool enabled = true,
        double interval = 5,
        string valueType = "number",
        string kind = "sensor") => new(
            new MetricDescriptor(id, id.Replace('.', ' '), kind, valueType, IntervalSeconds: interval, Context: "system"),
            enabled,
            exposeToHa,
            interval,
            Deadband: null,
            IsCustom: false);

    public static CommandDocument Command(
        string id,
        bool enabled = true,
        bool exposeToHa = true,
        CommandAction? action = null,
        IReadOnlyList<ParameterDefinition>? parameters = null,
        HaControl? control = null,
        bool captureOutput = false,
        string runAs = RunAsValues.System) => new(
            DocumentSchema.CurrentVersion,
            id,
            "Command " + id,
            enabled,
            exposeToHa,
            runAs,
            action ?? new PowerShellAction(PowerShellSources.Inline, Code: "'" + id + "'"),
            Parameters: parameters,
            HaControl: control,
            CaptureOutput: captureOutput);

    public static CommandDocument WithCode(this CommandDocument command, string code) =>
        command with { Action = new PowerShellAction(PowerShellSources.Inline, Code: code) };

    public static ParameterDefinition Choice(string name, params string[] options) =>
        new(name, ParameterTypes.Choice, Required: true, Options: options);

    public static HaControl Select(string parameter) => new(HaControlTypes.Select, parameter);

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "global.json")) && Directory.Exists(Path.Combine(directory, "protocol")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("The repository root (global.json and protocol/) was not found.");
    }
}

/// <summary>
/// A manifest service on real parts: a config store in a temporary directory, the command registry, the metric
/// engine with a stub provider (and optionally the simulated platform), driven by a fake clock.
/// </summary>
internal sealed class ManifestHarness : IAsyncDisposable
{
    private readonly MetricHarness _metrics;
    private readonly SimulatedPlatform? _platform;

    private ManifestHarness(MetricHarness metrics, SimulatedPlatform? platform, CommandRegistry registry, ManifestService service)
    {
        _metrics = metrics;
        _platform = platform;
        Registry = registry;
        Service = service;
    }

    public ConfigStore Store => _metrics.Store;

    public MetricEngine Engine => _metrics.Engine;

    public CommandRegistry Registry { get; }

    public ManifestService Service { get; }

    public SimulatedClock Clock => _metrics.Clock;

    public List<ManifestChangedEventArgs> Changes { get; } = [];

    public static async Task<ManifestHarness> CreateAsync(bool simulatedMetrics = false, bool watchSettings = false)
    {
        var clock = new SimulatedClock();
        var stub = new StubMetricProvider(clock, MetricHarness.Number("test.value"));
        SimulatedPlatform? platform = simulatedMetrics ? new SimulatedPlatform(clock) : null;
        List<IMetricProvider> providers = [stub];
        if (platform is not null)
        {
            providers.Add(platform.Metrics);
        }

        MetricHarness metrics = await MetricHarness.CreateAsync(clock, providers);
        var registry = new CommandRegistry(metrics.Store);
        ConfigStore store = metrics.Store;
        var service = new ManifestService(
            registry,
            metrics.Engine,
            () => ManifestTestSupport.Agent(store.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.Name ?? "Gaming-PC"),
            () => ["notify"],
            watchSettings ? store : null);
        var harness = new ManifestHarness(metrics, platform, registry, service);
        service.Changed += (_, change) =>
        {
            lock (harness.Changes)
            {
                harness.Changes.Add(change);
            }
        };
        return harness;
    }

    public async Task SaveAsync(CommandDocument command)
    {
        SaveResult<CommandDocument> result = await Store.Local.SaveAsync(command, null, CancellationToken.None);
        if (!result.IsApplied)
        {
            throw new InvalidOperationException(
                $"Saving '{command.Id}' failed: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} [{error.Code}] {error.Message}")));
        }
    }

    public async Task SaveSensorAsync(SensorDocument sensor)
    {
        SaveResult<SensorDocument> result = await Store.Local.SaveAsync(sensor, null, CancellationToken.None);
        if (!result.IsApplied)
        {
            throw new InvalidOperationException(
                $"Saving '{sensor.Id}' failed: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} [{error.Code}] {error.Message}")));
        }
    }

    public int ChangeCount
    {
        get
        {
            lock (Changes)
            {
                return Changes.Count;
            }
        }
    }

    /// <summary>Completes with the next change after this call; fails after ten seconds.</summary>
    public Task<ManifestChangedEventArgs> NextChangeAsync()
    {
        var source = new TaskCompletionSource<ManifestChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, ManifestChangedEventArgs change)
        {
            Service.Changed -= Handler;
            source.TrySetResult(change);
        }

        Service.Changed += Handler;
        return source.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Completes after the registry reported the next change; the service, which subscribed first, has handled it by then.</summary>
    public Task NextRegistryChangeAsync()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, EventArgs e)
        {
            Registry.Changed -= Handler;
            source.TrySetResult();
        }

        Registry.Changed += Handler;
        return source.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        Service.Dispose();
        Registry.Dispose();
        await _metrics.DisposeAsync();
        _platform?.Dispose();
    }
}

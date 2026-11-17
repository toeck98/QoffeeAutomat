using QoffeeAutomat.Agent.Api.Prometheus;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Api.Prometheus;

/// <summary>
/// The exporter of the Prometheus endpoint on a metric engine with the simulated provider and a simulated clock (T709): a scrape is a
/// subscriber with a keep-alive window, so that the sampling stops when the scraping stops (PLAN.md D-007).
/// </summary>
[Trait("Category", "Prometheus")]
public sealed class PrometheusExporterTests
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static readonly string[] AllSimulated =
    [
        SimulatedMetricProvider.CpuId,
        SimulatedMetricProvider.CounterId,
        SimulatedMetricProvider.ToggleId,
        SimulatedMetricProvider.ModeId,
    ];

    [Fact]
    public async Task The_first_scrape_subscribes_and_exports_numbers_and_booleans_but_not_text()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated);
        Assert.False(harness.Exporter.IsSampling);

        string text = await harness.ScrapeAsync();

        IReadOnlyList<ExpositionFamily> families = ExpositionParser.Parse(text);
        Assert.Equal(["qoffeeautomat_sim_counter", "qoffeeautomat_sim_cpu", "qoffeeautomat_sim_toggle"], families.Select(family => family.Name));
        DateTimeOffset now = harness.Clock.GetUtcNow();
        Assert.Equal(SimulatedMetricProvider.CounterAt(now), Value(families, "qoffeeautomat_sim_counter"));
        Assert.Equal(Math.Round(SimulatedMetricProvider.CpuAt(now), 1, MidpointRounding.AwayFromZero), Value(families, "qoffeeautomat_sim_cpu"));
        Assert.Equal(SimulatedMetricProvider.ToggleAt(now) ? 1 : 0, Value(families, "qoffeeautomat_sim_toggle"));
        Assert.All(families, family => Assert.Equal("Test-PC", Assert.Single(family.Samples).Labels["agent"]));
        Assert.True(harness.Exporter.IsSampling);
    }

    [Fact]
    public async Task A_metric_that_does_not_exist_or_is_not_selected_is_not_exported()
    {
        await using Harness harness = await Harness.CreateAsync([SimulatedMetricProvider.CpuId, "no.such.metric"]);

        string text = await harness.ScrapeAsync();

        Assert.Equal(["qoffeeautomat_sim_cpu"], ExpositionParser.Parse(text).Select(family => family.Name));
    }

    [Fact]
    public async Task Sampling_stops_when_the_scraping_stops()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated);
        await harness.ScrapeAsync();
        int calls = harness.Provider.SampleCallCount;
        Assert.True(calls > 0);

        // While somebody scrapes, the engine samples on its interval ...
        await harness.TickAsync(OneSecond, 5);
        Assert.True(harness.Provider.SampleCallCount > calls);
        Assert.True(harness.Exporter.IsSampling);

        // ... and when the keep-alive window has run out without a scrape, the subscription ends and nothing is sampled any more.
        harness.Clock.Advance(KeepAlive);
        Assert.False(harness.Exporter.IsSampling);
        int stopped = harness.Provider.SampleCallCount;
        await harness.TickAsync(OneSecond, 60);
        Assert.Equal(stopped, harness.Provider.SampleCallCount);
    }

    [Fact]
    public async Task A_scrape_renews_the_keep_alive_window()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated);
        await harness.ScrapeAsync();

        harness.Clock.Advance(TimeSpan.FromSeconds(100));
        await harness.ScrapeAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(100));
        Assert.True(harness.Exporter.IsSampling);

        // 100 seconds after the second scrape the window of two minutes is still open; 20 more close it.
        harness.Clock.Advance(TimeSpan.FromSeconds(19));
        Assert.True(harness.Exporter.IsSampling);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(harness.Exporter.IsSampling);
    }

    [Fact]
    public async Task A_scrape_after_the_window_ran_out_samples_again_and_exports_current_values()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated);
        await harness.ScrapeAsync();
        harness.Clock.Advance(KeepAlive);
        Assert.False(harness.Exporter.IsSampling);
        harness.Clock.Advance(TimeSpan.FromSeconds(7));

        string text = await harness.ScrapeAsync();

        Assert.True(harness.Exporter.IsSampling);
        Assert.Equal(
            SimulatedMetricProvider.CounterAt(harness.Clock.GetUtcNow()),
            Value(ExpositionParser.Parse(text), "qoffeeautomat_sim_counter"));
    }

    [Fact]
    public async Task The_values_follow_the_engine_while_the_scraping_goes_on()
    {
        await using Harness harness = await Harness.CreateAsync([SimulatedMetricProvider.CounterId]);
        string first = await harness.ScrapeAsync();

        await harness.TickAsync(OneSecond, 10);
        string second = await harness.ScrapeAsync();

        double before = Value(ExpositionParser.Parse(first), "qoffeeautomat_sim_counter");
        double after = Value(ExpositionParser.Parse(second), "qoffeeautomat_sim_counter");
        Assert.Equal(before + 10, after);
    }

    [Fact]
    public async Task A_change_of_the_selection_in_the_settings_is_followed_by_the_next_scrape()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated);
        await harness.ScrapeAsync();

        await harness.SaveSettingsAsync(enabled: true, [SimulatedMetricProvider.ToggleId]);
        string text = await harness.ScrapeAsync();

        Assert.Equal(["qoffeeautomat_sim_toggle"], ExpositionParser.Parse(text).Select(family => family.Name));
    }

    [Fact]
    public async Task Nothing_selected_means_nothing_is_sampled_and_nothing_is_exported()
    {
        await using Harness harness = await Harness.CreateAsync([]);

        string text = await harness.ScrapeAsync();

        Assert.Empty(text);
        Assert.False(harness.Exporter.IsSampling);
        await harness.TickAsync(OneSecond, 10);
        Assert.Equal(0, harness.Provider.SampleCallCount);
    }

    [Fact]
    public async Task Emptying_the_selection_ends_the_subscription_with_the_next_scrape()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated);
        await harness.ScrapeAsync();
        Assert.True(harness.Exporter.IsSampling);

        await harness.SaveSettingsAsync(enabled: true, []);
        string text = await harness.ScrapeAsync();

        Assert.Empty(text);
        Assert.False(harness.Exporter.IsSampling);
    }

    [Fact]
    public async Task The_endpoint_is_off_unless_the_settings_switch_it_on()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated, enabled: false);
        Assert.False(harness.Exporter.IsEnabled);

        await harness.SaveSettingsAsync(enabled: true, AllSimulated);

        Assert.True(harness.Exporter.IsEnabled);
    }

    [Fact]
    public async Task Disposing_the_exporter_ends_the_subscription_and_further_scrapes_fail()
    {
        await using Harness harness = await Harness.CreateAsync(AllSimulated);
        await harness.ScrapeAsync();

        harness.Exporter.Dispose();

        Assert.False(harness.Exporter.IsSampling);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Exporter.ScrapeAsync(CancellationToken.None));
    }

    private static double Value(IReadOnlyList<ExpositionFamily> families, string name)
    {
        return Assert.Single(Assert.Single(families, family => family.Name == name).Samples).Value;
    }

    /// <summary>An engine with the simulated provider on a simulated clock, the exporter on top, and a real configuration store in a temporary directory.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDataDirectory _temp;
        private readonly SimulatedPlatform _platform;

        private Harness(TempDataDirectory temp, SimulatedClock clock, ConfigStore store, SimulatedPlatform platform, MetricEngine engine, PrometheusExporter exporter)
        {
            _temp = temp;
            Clock = clock;
            Store = store;
            _platform = platform;
            Engine = engine;
            Exporter = exporter;
        }

        public SimulatedClock Clock { get; }

        public ConfigStore Store { get; }

        public MetricEngine Engine { get; }

        public PrometheusExporter Exporter { get; }

        public SimulatedMetricProvider Provider => _platform.Metrics;

        public static async Task<Harness> CreateAsync(IReadOnlyList<string> metrics, bool enabled = true)
        {
            var temp = new TempDataDirectory();
            var clock = new SimulatedClock();
            var store = new ConfigStore(
                new ConfigStoreOptions
                {
                    ConfigDirectory = Path.Combine(temp.Path, "config"),
                    ScriptsDirectory = Path.Combine(temp.Path, "scripts"),
                    EnableWatcher = false,
                },
                clock);
            await store.LoadAsync(CancellationToken.None);
            var platform = new SimulatedPlatform(clock);
            var engine = new MetricEngine(store, [platform.Metrics], clock);
            var identity = new AgentIdentity(platform.Info, store, new AgentOptions());
            var exporter = new PrometheusExporter(engine, store, identity, clock, new PrometheusOptions { KeepAlive = KeepAlive });
            var harness = new Harness(temp, clock, store, platform, engine, exporter);
            await harness.SaveSettingsAsync(enabled, metrics);
            return harness;
        }

        public async Task SaveSettingsAsync(bool enabled, IReadOnlyList<string> metrics)
        {
            var settings = new AgentSettingsDocument(
                DocumentSchema.CurrentVersion,
                "Test-PC",
                Prometheus: new PrometheusSettings(enabled, metrics));
            SaveResult<AgentSettingsDocument> saved = await Store.Local.SaveAsync(settings, null, CancellationToken.None);
            if (!saved.IsApplied)
            {
                throw new InvalidOperationException("The settings were not saved: " + string.Join("; ", saved.Errors.Select(error => error.Message)));
            }
        }

        /// <summary>
        /// A scrape on the simulated clock: the engine only samples when it is polled, so the scrape is started, the engine is polled,
        /// and then the scrape completes with the first snapshot.
        /// </summary>
        public async Task<string> ScrapeAsync()
        {
            Task<string> scrape = Exporter.ScrapeAsync(CancellationToken.None);
            await Engine.PollAsync(CancellationToken.None);
            return await TestWait.WithTimeoutAsync(scrape, "the scrape to complete");
        }

        public async Task TickAsync(TimeSpan step, int count)
        {
            for (int index = 0; index < count; index++)
            {
                Clock.Advance(step);
                await Engine.PollAsync(CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Exporter.Dispose();
            await Engine.DisposeAsync();
            Store.Dispose();
            _platform.Dispose();
            _temp.Dispose();
        }
    }
}

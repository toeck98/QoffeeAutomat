using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using Xunit.Abstractions;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>
/// The load of a fleet (T910): 50 agents with 30 metrics each at the default interval of 60 seconds, run through the collector and the SQLite store with the jobs of the hub.
/// Time is simulated: the clock moves one second per step and the collector flushes every 5 seconds and runs its roll-up job every minute, as it does in the hub. The CPU time of
/// the process is divided by the simulated time, which is the share of one core that the hub would use; the frames are built before the measurement so that the test's own
/// work is not counted. The test writes its measurements to the test output (run with <c>--logger "console;verbosity=detailed"</c>); <c>docs/user/hub-sizing.md</c> explains them.
/// </summary>
[Trait("Category", "Metrics")]
public sealed class MetricLoadTests(ITestOutputHelper output)
{
    private const int Agents = 50;
    private const int NumberMetrics = 24;
    private const int BoolMetrics = 3;
    private const int TextMetrics = 3;
    private const int IntervalSeconds = 60;
    private const int Seconds = 2 * 60 * 60;
    private const double MaxCoreShare = 0.05;

    private static readonly DateTimeOffset T0 = MetricKit.T0;

    private static string AgentId(int index) => "agent-" + index.ToString("00", CultureInfo.InvariantCulture);

    private static string NumberId(int index) => "load.n" + index.ToString("00", CultureInfo.InvariantCulture);

    private static string FlagId(int index) => "flag.b" + index.ToString(CultureInfo.InvariantCulture);

    private static string ModeId(int index) => "mode.e" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The manifest metrics of every agent: 24 numbers, 3 booleans and 3 enums.</summary>
    private static MetricDescriptor[] Metrics()
    {
        return
        [
            .. Enumerable.Range(0, NumberMetrics).Select(index => MetricKit.Metric(NumberId(index), MetricValueTypes.Number, "%")),
            .. Enumerable.Range(0, BoolMetrics).Select(index => MetricKit.Metric(FlagId(index), MetricValueTypes.Bool)),
            .. Enumerable.Range(0, TextMetrics).Select(index => MetricKit.Metric(ModeId(index), MetricValueTypes.Choice)),
        ];
    }

    /// <summary>A few JSON values that every frame shares; a <see cref="JsonElement"/> is immutable.</summary>
    private sealed class ValuePool
    {
        public JsonElement[] Numbers { get; } = [.. Enumerable.Range(0, 100).Select(value => MetricKit.Json(value + 0.25))];

        public JsonElement True { get; } = MetricKit.Json(true);

        public JsonElement False { get; } = MetricKit.Json(false);

        public JsonElement[] Modes { get; } = [.. Enumerable.Range(0, 4).Select(value => MetricKit.Json("mode-" + value.ToString(CultureInfo.InvariantCulture)))];
    }

    private static AgentStateUpdate Frame(ValuePool pool, int agent, long ts, int step, bool snapshot, long modeSeconds)
    {
        var values = new Dictionary<string, JsonElement>(NumberMetrics + BoolMetrics + TextMetrics, StringComparer.Ordinal);
        for (int index = 0; index < NumberMetrics; index++)
        {
            values[NumberId(index)] = pool.Numbers[((agent * 7) + (index * 3) + step) % pool.Numbers.Length];
        }

        for (int index = 0; index < BoolMetrics; index++)
        {
            values[FlagId(index)] = (agent + step + index) % 4 != 0 ? pool.True : pool.False;
        }

        for (int index = 0; index < TextMetrics; index++)
        {
            values[ModeId(index)] = pool.Modes[(int)((modeSeconds / 1800) % pool.Modes.Length)];
        }

        return new AgentStateUpdate(AgentId(agent), ts, values, snapshot);
    }

    /// <summary>The reports of one agent, one per interval, each with a value for every metric; the first is the snapshot that follows the subscribe.</summary>
    private static AgentStateUpdate[] Reports(ValuePool pool, MetricDescriptor[] metrics, int agent, int count)
    {
        var reports = new AgentStateUpdate[count];
        for (int report = 0; report < count; report++)
        {
            var values = new Dictionary<string, JsonElement>(metrics.Length, StringComparer.Ordinal);
            foreach (MetricDescriptor metric in metrics)
            {
                values[metric.Id] = pool.Numbers[(agent + report) % pool.Numbers.Length];
            }

            reports[report] = new AgentStateUpdate(AgentId(agent), T0.AddSeconds(report * IntervalSeconds).ToUnixTimeMilliseconds(), values, report == 0);
        }

        return reports;
    }

    [Fact]
    public async Task Fifty_agents_with_thirty_metrics_at_a_60_second_interval_use_less_than_five_percent_of_one_core()
    {
        using var harness = new CollectorHarness();
        string[] agentIds = [.. Enumerable.Range(0, Agents).Select(AgentId)];
        harness.Store.AddAgents(agentIds);
        MetricDescriptor[] metrics = Metrics();
        foreach (string agent in agentIds)
        {
            harness.Collector.OnManifestChanged(agent, MetricKit.Manifest(agent, metrics));
        }

        // The hub asked every agent for every metric at 60 seconds.
        Assert.Equal(Agents, harness.Gateway.Subscriptions.Count);
        Assert.All(harness.Gateway.Subscriptions, subscription => Assert.Equal(NumberMetrics + BoolMetrics + TextMetrics, subscription.Demand.Metrics.Count));
        Assert.All(harness.Gateway.Subscriptions, subscription => Assert.All(subscription.Demand.Metrics.Values, interval => Assert.Equal(TimeSpan.FromSeconds(IntervalSeconds), interval)));

        // The agents report once a minute each, spread over the minute (the first frame of an agent is the snapshot that follows the subscribe).
        var pool = new ValuePool();
        var frames = new List<AgentStateUpdate>[Seconds];
        for (int second = 0; second < Seconds; second++)
        {
            frames[second] = [];
        }

        int framesBuilt = 0;
        for (int agent = 0; agent < Agents; agent++)
        {
            int offset = agent * IntervalSeconds / Agents;
            for (int second = offset; second < Seconds; second += IntervalSeconds)
            {
                long ts = T0.AddSeconds(second).ToUnixTimeMilliseconds();
                frames[second].Add(Frame(pool, agent, ts, second / IntervalSeconds, snapshot: second < IntervalSeconds, second));
                framesBuilt++;
            }
        }

        MetricStoreStats baseline = await harness.Store.Store.GetStatsAsync(CancellationToken.None);

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        for (int second = 0; second < Seconds; second++)
        {
            foreach (AgentStateUpdate frame in frames[second])
            {
                harness.Collector.OnStateChanged(frame);
            }

            if (second % 5 == 4)
            {
                _ = await harness.Collector.FlushAsync(CancellationToken.None);
            }

            if (second % IntervalSeconds == IntervalSeconds - 1)
            {
                await harness.Collector.RunMaintenanceAsync(CancellationToken.None);
            }

            harness.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        _ = await harness.Collector.FlushAsync(CancellationToken.None);
        wall.Stop();
        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
        MetricStoreStats stats = await harness.Store.Store.GetStatsAsync(CancellationToken.None);

        // Everything arrived and was stored; nothing was dropped.
        int numericSeries = Agents * (NumberMetrics + BoolMetrics);
        int framesPerAgent = Seconds / IntervalSeconds;
        Assert.Equal(Agents * framesPerAgent, framesBuilt);
        Assert.Equal(0, harness.Collector.DroppedSamples);
        Assert.Equal(Agents * (NumberMetrics + BoolMetrics + TextMetrics), stats.Series);
        Assert.Equal((long)numericSeries * framesPerAgent, stats.RawRows);

        // The enums are a change log: a new value every 30 minutes, so four entries in two hours, and not one entry per report.
        Assert.Equal((long)Agents * TextMetrics * 4, stats.StateRows);

        // The roll-up job kept up: the hour that ended is rolled up and most five minute buckets of the time that ended too.
        Assert.InRange(stats.Rollup1hRows, numericSeries, numericSeries * 2L);
        Assert.InRange(stats.Rollup5mRows, numericSeries * 20L, numericSeries * 24L);

        // The newest values are there, and a read of the last hour finds a point per report.
        IReadOnlyList<LatestMetric> latest = await harness.Store.Store.GetLatestAsync(AgentId(0), CancellationToken.None);
        Assert.Equal(NumberMetrics + BoolMetrics + TextMetrics, latest.Count);
        IReadOnlyList<MetricSeriesData> hour = await harness.Store.QueryAsync(MetricKit.Query([AgentId(0)], ["load.*"], T0.AddHours(1), T0.AddHours(2)));
        Assert.Equal(NumberMetrics, hour.Count);
        Assert.All(hour, series => Assert.Equal(framesPerAgent / 2, series.Points.Count));

        // The result: the share of one core that the hub needed for the simulated time.
        double share = cpu.TotalSeconds / Seconds;
        long growth = stats.DatabaseBytes - baseline.DatabaseBytes;
        long rows = stats.RawRows + stats.StateRows + stats.Rollup5mRows + stats.Rollup1hRows;
        long samples = stats.RawRows + stats.StateRows;
        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Simulated {Seconds} s of {Agents} agents x {NumberMetrics + BoolMetrics + TextMetrics} metrics at {IntervalSeconds} s: {framesBuilt} frames, {samples} samples stored."));
        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"CPU {cpu.TotalSeconds:F2} s in {wall.Elapsed.TotalSeconds:F2} s of wall time = {share * 100:F3} % of one core of the simulated time (limit {MaxCoreShare * 100:F0} %)."));
        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Database growth {growth} bytes for {stats.RawRows} raw, {stats.StateRows} state, {stats.Rollup5mRows} 5m and {stats.Rollup1hRows} 1h rows ({stats.Series} series)."));
        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"= {(double)growth / rows:F1} bytes per row of any table, {(double)growth / samples:F1} bytes per stored sample with its roll-ups so far."));
        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Raw alone: {(double)stats.RawRows / (numericSeries * 1.0):F0} rows per series in two hours."));

        Assert.True(
            share < MaxCoreShare,
            string.Create(CultureInfo.InvariantCulture, $"The hub used {share * 100:F2} % of one core for {Agents} agents; the limit is {MaxCoreShare * 100:F0} %."));
    }

    [Fact]
    public async Task All_agents_reporting_everything_at_once_after_a_restart_are_written_in_one_go()
    {
        // The hub restarts: every agent connects again and sends the full snapshot of its subscription within the same moment.
        using var harness = new CollectorHarness();
        string[] agentIds = [.. Enumerable.Range(0, Agents).Select(AgentId)];
        harness.Store.AddAgents(agentIds);
        MetricDescriptor[] metrics = Metrics();
        var pool = new ValuePool();
        foreach (string agent in agentIds)
        {
            harness.Collector.OnManifestChanged(agent, MetricKit.Manifest(agent, metrics));
        }

        long ts = T0.ToUnixTimeMilliseconds();
        var wall = Stopwatch.StartNew();
        for (int agent = 0; agent < Agents; agent++)
        {
            harness.Collector.OnStateChanged(Frame(pool, agent, ts, 0, snapshot: true, 0));
        }

        int written = await harness.Collector.FlushAsync(CancellationToken.None);
        wall.Stop();

        Assert.Equal(Agents * (NumberMetrics + BoolMetrics + TextMetrics), written);
        Assert.Equal(0, harness.Collector.DroppedSamples);
        Assert.True(wall.Elapsed < TimeSpan.FromSeconds(5), "Writing one snapshot of the fleet took " + wall.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s.");
    }

    [Fact]
    public async Task Agents_that_report_at_the_same_time_from_many_threads_lose_nothing_and_the_store_is_fast_enough()
    {
        // 50 producers (the link handlers of the agents) post 100 reports each while the writer flushes. The clock tolerance is wide because the reports carry the times of a
        // longer period than the clock of this test moves.
        const int reports = 100;
        var options = new HubMetricsOptions { MaxClockSkew = TimeSpan.FromDays(1) };
        using var harness = new CollectorHarness(options);
        string[] agentIds = [.. Enumerable.Range(0, Agents).Select(AgentId)];
        harness.Store.AddAgents(agentIds);
        MetricDescriptor[] metrics = [.. Enumerable.Range(0, NumberMetrics + BoolMetrics + TextMetrics).Select(index => MetricKit.Metric("m" + index.ToString("00", CultureInfo.InvariantCulture)))];
        var pool = new ValuePool();
        foreach (string agent in agentIds)
        {
            harness.Collector.OnManifestChanged(agent, MetricKit.Manifest(agent, metrics));
        }

        AgentStateUpdate[][] frames = [.. Enumerable.Range(0, Agents).Select(agent => Reports(pool, metrics, agent, reports))];

        using var producing = new CancellationTokenSource();
        var wall = Stopwatch.StartNew();
        Task writer = Task.Run(async () =>
        {
            while (!producing.IsCancellationRequested)
            {
                _ = await harness.Collector.FlushAsync(CancellationToken.None);
                await Task.Delay(10, CancellationToken.None);
            }
        });
        await Task.WhenAll(frames.Select(agentFrames => Task.Run(() =>
        {
            foreach (AgentStateUpdate frame in agentFrames)
            {
                harness.Collector.OnStateChanged(frame);
            }
        })));
        await producing.CancelAsync();
        await writer;
        _ = await harness.Collector.FlushAsync(CancellationToken.None);
        wall.Stop();

        MetricStoreStats stats = await harness.Store.Store.GetStatsAsync(CancellationToken.None);
        long expected = (long)Agents * reports * metrics.Length;
        double perSecond = expected / Math.Max(wall.Elapsed.TotalSeconds, 0.001);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{expected} samples from {Agents} threads in {wall.Elapsed.TotalSeconds:F2} s = {perSecond:F0} samples per second."));

        Assert.Equal(0, harness.Collector.DroppedSamples);
        Assert.Equal(expected, stats.RawRows);

        // The fleet of the test needs 25 samples per second; the store must be far above that on any runner.
        Assert.True(perSecond > 5_000, string.Create(CultureInfo.InvariantCulture, $"Only {perSecond:F0} samples per second were written."));
    }
}

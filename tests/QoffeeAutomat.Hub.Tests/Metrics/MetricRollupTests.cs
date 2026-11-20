using QoffeeAutomat.Hub.Metrics;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>
/// The five minute and one hour roll-ups (T910). The property: for every series and every bucket, minimum, maximum and count of the roll-up equal those of the raw values in
/// the bucket, and the average equals their average (also for the hour, which is built from the five minute buckets). The property is checked on random series from a fixed
/// list of seeds, so a failure reproduces, against a model that is written independently of the SQL.
/// </summary>
[Trait("Category", "Metrics")]
public sealed class MetricRollupTests
{
    private static readonly DateTimeOffset T0 = MetricKit.T0;

    private static readonly TimeSpan Grace = new HubMetricsOptions().RollupGrace;

    /// <summary>The seeds of the random trials.</summary>
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 25).Select(seed => new object[] { seed });

    private sealed record Bucket(long Start, double Min, double Avg, double Max, long Count);

    private static List<Bucket> Model(IEnumerable<(long Ts, double Value)> samples, long width)
    {
        return [.. samples
            .GroupBy(sample => MetricResolutions.Floor(sample.Ts, width))
            .OrderBy(group => group.Key)
            .Select(group => new Bucket(group.Key, group.Min(sample => sample.Value), group.Average(sample => sample.Value), group.Max(sample => sample.Value), group.Count()))];
    }

    private static void AssertBuckets(IReadOnlyList<Bucket> expected, MetricSeriesData actual, string label)
    {
        Assert.Equal(expected.Count, actual.Points.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Bucket bucket = expected[index];
            MetricPoint point = actual.Points[index];
            string where = label + " bucket " + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(DateTimeOffset.FromUnixTimeMilliseconds(bucket.Start) == point.Ts, where + ": start");
            Assert.True(bucket.Min == point.Min, where + ": min");
            Assert.True(bucket.Max == point.Max, where + ": max");
            Assert.True(bucket.Count == point.Count, where + ": count");
            Assert.InRange(point.Avg!.Value, bucket.Avg - Tolerance(bucket.Avg), bucket.Avg + Tolerance(bucket.Avg));
            Assert.Null(point.Value);
            Assert.Null(point.Text);
        }
    }

    private static double Tolerance(double value) => 1e-9 * Math.Max(1, Math.Abs(value));

    private static List<(string Metric, List<(long Ts, double Value)> Samples)> RandomSeries(Random random, TimeSpan span)
    {
        var series = new List<(string Metric, List<(long Ts, double Value)> Samples)>();
        int metrics = random.Next(1, 5);
        for (int metric = 0; metric < metrics; metric++)
        {
            int count = random.Next(1, 400);
            var seen = new HashSet<long>();
            var samples = new List<(long Ts, double Value)>();
            for (int index = 0; index < count; index++)
            {
                // Some samples sit exactly on a bucket border: the start of a bucket belongs to it, the end does not.
                long seconds = random.Next(4) == 0
                    ? random.Next(0, (int)(span.TotalSeconds / 300)) * 300L
                    : random.NextInt64(0, (long)span.TotalSeconds);
                long ts = T0.AddSeconds(seconds).ToUnixTimeMilliseconds() + (random.Next(3) == 0 ? random.Next(0, 1000) : 0);
                if (seen.Add(ts))
                {
                    double value = random.Next(5) == 0 ? random.Next(-50, 50) : (random.NextDouble() * 2000) - 1000;
                    samples.Add((ts, value));
                }
            }

            series.Add(("m" + metric.ToString(System.Globalization.CultureInfo.InvariantCulture), samples));
        }

        return series;
    }

    private static MetricSample Sample(string agent, string metric, long ts, double value)
    {
        return new MetricSample(agent, metric, MetricValueTypes.Number, null, ts, value);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Roll_ups_equal_the_minimum_average_and_maximum_of_the_raw_values(int seed)
    {
        var random = new Random(seed);
        using var harness = new MetricStoreHarness();
        string[] agents = ["a1", "a2"];
        harness.AddAgents(agents);
        var model = new Dictionary<(string Agent, string Metric), List<(long Ts, double Value)>>();
        foreach (string agent in agents)
        {
            foreach ((string metric, List<(long Ts, double Value)> samples) in RandomSeries(random, TimeSpan.FromHours(3)))
            {
                model[(agent, metric)] = samples;
                _ = await harness.AppendAsync([.. samples.OrderBy(_ => random.Next()).Select(sample => Sample(agent, metric, sample.Ts, sample.Value))]);
            }
        }

        MetricRollupResult rolled = await harness.Store.RollUpAsync(T0.AddHours(4), CancellationToken.None);

        long expected5m = model.Values.Sum(samples => (long)Model(samples, MetricResolutions.FiveMinutesMilliseconds).Count);
        long expected1h = model.Values.Sum(samples => (long)Model(samples, MetricResolutions.OneHourMilliseconds).Count);
        Assert.Equal(expected5m, rolled.Buckets5m);
        Assert.Equal(expected1h, rolled.Buckets1h);

        foreach (MetricResolution resolution in new[] { MetricResolution.FiveMinutes, MetricResolution.OneHour })
        {
            long width = MetricResolutions.BucketMilliseconds(resolution);
            IReadOnlyList<MetricSeriesData> series = await harness.QueryAsync(MetricKit.Query(agents, ["*"], T0.AddHours(-1), T0.AddHours(5), resolution));
            Assert.Equal(model.Count, series.Count);
            foreach (MetricSeriesData actual in series)
            {
                Assert.Equal(MetricResolutions.ToText(resolution), actual.Resolution);
                AssertBuckets(Model(model[(actual.AgentId, actual.MetricId)], width), actual, actual.AgentId + "/" + actual.MetricId + " " + actual.Resolution + " seed " + seed);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Rolling_up_step_by_step_gives_the_same_buckets_as_one_run(int seed)
    {
        var random = new Random(seed * 7919);
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        (string Metric, List<(long Ts, double Value)> Samples)[] series = [.. RandomSeries(random, TimeSpan.FromHours(3))];

        // The agent reports while time passes; the roll-up job runs at every step, a little after the bucket ended.
        const int step = 5;
        for (int index = 0; index <= 3 * 60 / step; index++)
        {
            long from = T0.AddMinutes(index * step).ToUnixTimeMilliseconds();
            long to = T0.AddMinutes((index + 1) * step).ToUnixTimeMilliseconds();
            MetricSample[] batch =
            [
                .. series.SelectMany(item => item.Samples
                    .Where(sample => sample.Ts >= from && sample.Ts < to)
                    .Select(sample => Sample("a1", item.Metric, sample.Ts, sample.Value))),
            ];
            _ = await harness.Store.AppendAsync(batch, CancellationToken.None);
            _ = await harness.Store.RollUpAsync(T0.AddMinutes((index + 1) * step).Add(Grace).AddSeconds(1), CancellationToken.None);
        }

        foreach (MetricResolution resolution in new[] { MetricResolution.FiveMinutes, MetricResolution.OneHour })
        {
            long width = MetricResolutions.BucketMilliseconds(resolution);
            IReadOnlyList<MetricSeriesData> actual = await harness.QueryAsync(MetricKit.Query(["a1"], ["*"], T0.AddHours(-1), T0.AddHours(5), resolution));
            Assert.Equal(series.Length, actual.Count);
            foreach ((string metric, List<(long Ts, double Value)> samples) in series)
            {
                MetricSeriesData one = actual.Single(item => item.MetricId == metric);
                AssertBuckets(Model(samples, width), one, metric + " " + one.Resolution + " seed " + seed);
            }
        }
    }

    [Fact]
    public async Task A_boolean_is_rolled_up_to_the_share_of_time_it_was_true()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(
            MetricKit.Flag("a1", "network.up", T0, true),
            MetricKit.Flag("a1", "network.up", T0.AddMinutes(1), true),
            MetricKit.Flag("a1", "network.up", T0.AddMinutes(2), false),
            MetricKit.Flag("a1", "network.up", T0.AddMinutes(3), true));
        _ = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["network.up"], T0, T0.AddHours(1), MetricResolution.FiveMinutes)));

        MetricPoint bucket = Assert.Single(series.Points);
        Assert.Equal(0d, bucket.Min);
        Assert.Equal(1d, bucket.Max);
        Assert.Equal(0.75, bucket.Avg);
        Assert.Equal(4, bucket.Count);
    }

    [Fact]
    public async Task A_bucket_is_rolled_up_only_after_it_has_ended_and_the_grace_period_has_passed()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10), MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(4), 20));

        // The bucket 09:00 to 09:05 ended 29 seconds ago: the last samples may still be on their way to the database.
        MetricRollupResult early = await harness.Store.RollUpAsync(T0.AddMinutes(5).Add(Grace).AddSeconds(-1), CancellationToken.None);
        Assert.Equal(new MetricRollupResult(0, 0), early);

        MetricRollupResult due = await harness.Store.RollUpAsync(T0.AddMinutes(5).Add(Grace), CancellationToken.None);
        Assert.Equal(new MetricRollupResult(1, 0), due);

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], T0, T0.AddHours(1), MetricResolution.FiveMinutes)));
        MetricPoint bucket = Assert.Single(series.Points);
        Assert.Equal([10d, 15d, 20d], MetricKit.Seq(bucket.Min!.Value, bucket.Avg!.Value, bucket.Max!.Value));
        Assert.Equal(2, bucket.Count);
    }

    [Fact]
    public async Task An_hour_is_rolled_up_when_all_of_its_five_minute_buckets_are_complete()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10), MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(59), 30));

        MetricRollupResult early = await harness.Store.RollUpAsync(T0.AddHours(1).Add(Grace).AddSeconds(-1), CancellationToken.None);
        Assert.Equal(0, early.Buckets1h);

        MetricRollupResult due = await harness.Store.RollUpAsync(T0.AddHours(1).Add(Grace), CancellationToken.None);
        Assert.Equal(1, due.Buckets1h);

        MetricSeriesData hour = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], T0, T0.AddHours(2), MetricResolution.OneHour)));
        MetricPoint bucket = Assert.Single(hour.Points);
        Assert.Equal(T0, bucket.Ts);
        Assert.Equal([10d, 20d, 30d], MetricKit.Seq(bucket.Min!.Value, bucket.Avg!.Value, bucket.Max!.Value));
        Assert.Equal(2, bucket.Count);
    }

    [Fact]
    public async Task The_hour_average_weights_the_five_minute_buckets_by_their_sample_count()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");

        // One sample of 0 in the first bucket, nine samples of 100 in the second: the average of the hour is 90, not 50.
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 0));
        _ = await harness.AppendAsync([.. Enumerable.Range(0, 9).Select(index => MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(5).AddSeconds(index * 10), 100))]);
        _ = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);

        MetricSeriesData hour = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], T0, T0.AddHours(1), MetricResolution.OneHour)));
        MetricPoint bucket = Assert.Single(hour.Points);
        Assert.Equal(90d, bucket.Avg!.Value, 9);
        Assert.Equal(10, bucket.Count);
    }

    [Fact]
    public async Task Rolling_up_again_changes_nothing_and_a_late_sample_behind_the_watermark_stays_raw()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10));
        MetricRollupResult first = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);
        Assert.Equal(new MetricRollupResult(1, 1), first);

        MetricRollupResult repeated = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);
        Assert.Equal(new MetricRollupResult(0, 0), repeated);

        // An agent that was offline reports an old value: its bucket is closed, so the sample is kept raw only.
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(2), 500));
        _ = await harness.Store.RollUpAsync(T0.AddHours(3), CancellationToken.None);

        MetricSeriesData rolled = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], T0, T0.AddHours(1), MetricResolution.FiveMinutes)));
        Assert.Equal(1, Assert.Single(rolled.Points).Count);
        MetricSeriesData raw = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], T0, T0.AddHours(1))));
        Assert.Equal([10d, 500d], raw.Points.Select(point => point.Value!.Value));
    }

    [Fact]
    public async Task A_range_that_starts_inside_a_bucket_includes_that_bucket()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10), MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(7), 20));
        _ = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], T0.AddMinutes(3), T0.AddMinutes(8), MetricResolution.FiveMinutes)));

        Assert.Equal([T0, T0.AddMinutes(5)], series.Points.Select(point => point.Ts));
    }

    [Fact]
    public async Task Text_series_are_not_rolled_up_and_stay_a_change_log_at_every_resolution()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Txt("a1", "session.state", T0, "active"));

        MetricRollupResult rolled = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);

        Assert.Equal(new MetricRollupResult(0, 0), rolled);
        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["session.state"], T0, T0.AddHours(1), MetricResolution.OneHour)));
        Assert.Equal("changes", series.Resolution);
        Assert.Equal("active", Assert.Single(series.Points).Text);
    }

    [Fact]
    public async Task Rolling_up_an_empty_store_does_nothing()
    {
        using var harness = new MetricStoreHarness();

        MetricRollupResult rolled = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);

        Assert.Equal(new MetricRollupResult(0, 0), rolled);
    }
}

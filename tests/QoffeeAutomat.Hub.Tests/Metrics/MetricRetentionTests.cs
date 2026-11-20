using QoffeeAutomat.Hub.Metrics;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>Retention of the metric tables (T910): every table has its own window, nothing is deleted that a job has not processed yet, and the database does not grow without bound.</summary>
[Trait("Category", "Metrics")]
public sealed class MetricRetentionTests
{
    private static readonly DateTimeOffset T0 = MetricKit.T0;

    private static async Task<long[]> RowsAsync(MetricStoreHarness harness)
    {
        MetricStoreStats stats = await harness.Store.GetStatsAsync(CancellationToken.None);
        return [stats.RawRows, stats.StateRows, stats.Rollup5mRows, stats.Rollup1hRows];
    }

    [Fact]
    public async Task Every_table_is_cleaned_after_its_own_retention()
    {
        // The defaults of the specification: raw 48 hours, states 90 days, five minute roll-ups 30 days, one hour roll-ups 2 years.
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10),
            MetricKit.Txt("a1", "session.state", T0.AddMinutes(1), "active"),
            MetricKit.Txt("a1", "session.state", T0.AddDays(1), "locked"));
        _ = await harness.Store.RollUpAsync(T0.AddHours(3), CancellationToken.None);
        long[] initialRows = await RowsAsync(harness);
        Assert.Equal([1L, 2L, 1L, 1L], initialRows);

        // Nothing has expired after 47 hours.
        MetricRetentionResult none = await harness.Store.ApplyRetentionAsync(T0.AddHours(47), CancellationToken.None);
        Assert.Equal(0, none.Total);

        // 49 hours: the raw sample is older than 48 hours.
        MetricRetentionResult raw = await harness.Store.ApplyRetentionAsync(T0.AddHours(49), CancellationToken.None);
        Assert.Equal(new MetricRetentionResult(1, 0, 0, 0), raw);
        long[] afterRawRows = await RowsAsync(harness);
        Assert.Equal([0L, 2L, 1L, 1L], afterRawRows);

        // 31 days: the five minute roll-up has expired.
        MetricRetentionResult fiveMinutes = await harness.Store.ApplyRetentionAsync(T0.AddDays(31), CancellationToken.None);
        Assert.Equal(new MetricRetentionResult(0, 0, 1, 0), fiveMinutes);

        // 91 days: the first entry of the change log has expired, the second is the current text and stays.
        MetricRetentionResult states = await harness.Store.ApplyRetentionAsync(T0.AddDays(91), CancellationToken.None);
        Assert.Equal(new MetricRetentionResult(0, 1, 0, 0), states);

        // 731 days: the hour roll-up has expired.
        MetricRetentionResult hours = await harness.Store.ApplyRetentionAsync(T0.AddDays(731), CancellationToken.None);
        Assert.Equal(new MetricRetentionResult(0, 0, 0, 1), hours);

        long[] finalRows = await RowsAsync(harness);
        Assert.Equal([0L, 1L, 0L, 0L], finalRows);
    }

    [Fact]
    public async Task A_shorter_retention_from_the_options_is_applied()
    {
        var options = new HubMetricsOptions { RawRetention = TimeSpan.FromHours(2), Rollup5mRetention = TimeSpan.FromHours(3) };
        options.Validate();
        using var harness = new MetricStoreHarness(options);
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10), MetricKit.Num("a1", "cpu.usage", T0.AddHours(5), 20));
        _ = await harness.Store.RollUpAsync(T0.AddHours(7), CancellationToken.None);

        MetricRetentionResult result = await harness.Store.ApplyRetentionAsync(T0.AddHours(7), CancellationToken.None);

        // Raw: the sample of 09:01 is older than two hours, the one of 14:00 is not. Five minutes: 09:00 is older than three hours, 14:00 is not.
        Assert.Equal(new MetricRetentionResult(1, 0, 1, 0), result);
        MetricSeriesData left = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], T0, T0.AddHours(8))));
        Assert.Equal(20d, Assert.Single(left.Points).Value);
    }

    [Fact]
    public async Task Raw_samples_that_are_not_rolled_up_yet_are_kept_however_old()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10));

        // The roll-up job did not run (it failed, or the hub was stopped): nothing may be deleted that the job still has to read.
        MetricRetentionResult result = await harness.Store.ApplyRetentionAsync(T0.AddDays(60), CancellationToken.None);

        Assert.Equal(0, result.Raw);
        long[] keptRows = await RowsAsync(harness);
        Assert.Equal([1L, 0L, 0L, 0L], keptRows);

        // Once the job has run, the sample goes.
        _ = await harness.Store.RollUpAsync(T0.AddDays(60), CancellationToken.None);
        MetricRetentionResult afterRollup = await harness.Store.ApplyRetentionAsync(T0.AddDays(60), CancellationToken.None);
        Assert.Equal(1, afterRollup.Raw);
    }

    [Fact]
    public async Task Five_minute_roll_ups_that_are_not_folded_into_an_hour_yet_are_kept()
    {
        var options = new HubMetricsOptions { Rollup5mRetention = TimeSpan.FromHours(1) };
        using var harness = new MetricStoreHarness(options);
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(1), 10));
        MetricRollupResult rolled = await harness.Store.RollUpAsync(T0.AddMinutes(10), CancellationToken.None);
        Assert.Equal(new MetricRollupResult(1, 0), rolled);

        MetricRetentionResult early = await harness.Store.ApplyRetentionAsync(T0.AddDays(5), CancellationToken.None);
        Assert.Equal(0, early.Rollups5m);

        _ = await harness.Store.RollUpAsync(T0.AddHours(2), CancellationToken.None);
        MetricRetentionResult late = await harness.Store.ApplyRetentionAsync(T0.AddDays(5), CancellationToken.None);
        Assert.Equal(1, late.Rollups5m);
        Assert.Equal(1, (await harness.Store.GetStatsAsync(CancellationToken.None)).Rollup1hRows);
    }

    [Fact]
    public async Task The_newest_text_of_a_series_is_kept_however_old_and_older_entries_are_removed()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(
            MetricKit.Txt("a1", "session.state", T0, "active"),
            MetricKit.Txt("a1", "session.state", T0.AddDays(1), "locked"),
            MetricKit.Txt("a1", "session.state", T0.AddDays(2), "active"),
            MetricKit.Txt("a1", "os.edition", T0, "Pro", MetricValueTypes.Text));

        MetricRetentionResult result = await harness.Store.ApplyRetentionAsync(T0.AddDays(500), CancellationToken.None);

        Assert.Equal(2, result.States);
        IReadOnlyList<LatestMetric> latest = await harness.Store.GetLatestAsync("a1", CancellationToken.None);
        Assert.Equal(["Pro", "active"], latest.Select(item => item.Text));

        // The text that never changes is still known after the retention.
        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["os.edition"], T0.AddDays(499), T0.AddDays(500))));
        Assert.Equal("Pro", Assert.Single(series.Points).Text);
    }

    [Fact]
    public async Task Retention_of_an_empty_store_removes_nothing()
    {
        using var harness = new MetricStoreHarness();

        MetricRetentionResult result = await harness.Store.ApplyRetentionAsync(T0.AddDays(1000), CancellationToken.None);

        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task The_database_does_not_grow_without_bound_in_a_simulated_30_day_run()
    {
        // The windows are as short as the options allow, so that 30 days are many times the retention of every table. Six series report every minute: two agents with
        // a number and a boolean each, and a text per agent that changes every six hours. The jobs run as the hub runs them: the roll-up after every hour (it runs
        // every minute in the hub, an hour per step is the harder case for the bookkeeping) and the retention every six hours.
        var options = new HubMetricsOptions
        {
            RawRetention = TimeSpan.FromHours(1),
            Rollup5mRetention = TimeSpan.FromHours(2),
            Rollup1hRetention = TimeSpan.FromDays(1),
            StateRetention = TimeSpan.FromDays(1),
        };
        options.Validate();
        using var harness = new MetricStoreHarness(options);
        harness.AddAgents("a1", "a2");
        const int days = 30;
        const int seriesCount = 6;
        long appended = 0;
        long removed = 0;
        var sizes = new List<long>();

        for (int hour = 0; hour < days * 24; hour++)
        {
            var batch = new List<MetricSample>(60 * seriesCount);
            for (int minute = 0; minute < 60; minute++)
            {
                DateTimeOffset at = T0.AddHours(hour).AddMinutes(minute);
                foreach (string agent in new[] { "a1", "a2" })
                {
                    batch.Add(MetricKit.Num(agent, "cpu.usage", at, (hour * 7) + (minute % 13)));
                    batch.Add(MetricKit.Flag(agent, "network.up", at, (minute + hour) % 5 != 0));
                    batch.Add(MetricKit.Txt(agent, "session.state", at, "state-" + (hour / 6).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }

            MetricAppendResult written = await harness.Store.AppendAsync(batch, CancellationToken.None);
            appended += written.Stored;
            DateTimeOffset now = T0.AddHours(hour + 1).Add(options.RollupGrace).AddSeconds(1);
            _ = await harness.Store.RollUpAsync(now, CancellationToken.None);

            if ((hour + 1) % 6 == 0)
            {
                removed += (await harness.Store.ApplyRetentionAsync(now, CancellationToken.None)).Total;
                MetricStoreStats stats = await harness.Store.GetStatsAsync(CancellationToken.None);

                // Right after a clean-up each table holds its window and a little more, whatever day it is.
                Assert.InRange(stats.RawRows, 0, 4 * 70);
                Assert.InRange(stats.Rollup5mRows, 0, 4 * 26);
                Assert.InRange(stats.Rollup1hRows, 0, 4 * 26);
                Assert.InRange(stats.StateRows, 0, 2 * 6);
                Assert.Equal(seriesCount, stats.Series);

                if ((hour + 1) % 24 == 0)
                {
                    await harness.CheckpointAsync();
                    sizes.Add(harness.FileBytes);
                }
            }
        }

        // Without a retention the 30 days would hold 4 x 43 200 raw samples; with it the database holds hours.
        Assert.True(appended > 100_000, "The run wrote " + appended.ToString(System.Globalization.CultureInfo.InvariantCulture) + " samples.");
        Assert.True(removed > appended / 2, "The retention removed " + removed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " rows.");

        // The windows are full after two days. From then on the file may breathe, but it must not trend upwards.
        long steady = sizes[2];
        long largest = sizes.Skip(2).Max();
        Assert.True(
            largest <= steady * 3 / 2,
            "The database grew from " + steady.ToString(System.Globalization.CultureInfo.InvariantCulture) + " to " + largest.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes.");
        Assert.True(
            sizes[^1] <= steady * 3 / 2,
            "The database ended at " + sizes[^1].ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes after " + steady.ToString(System.Globalization.CultureInfo.InvariantCulture) + " at day three.");
    }
}

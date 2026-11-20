using QoffeeAutomat.Hub.Metrics;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>The SQLite metric store (T910): writing, change log of text metrics, queries, the newest values and the removal of an agent with its series.</summary>
[Trait("Category", "Metrics")]
public sealed class MetricStoreTests
{
    private static readonly DateTimeOffset T0 = MetricKit.T0;

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public async Task Numeric_and_boolean_samples_are_stored_raw_and_read_back_in_time_order()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");

        MetricAppendResult result = await harness.AppendAsync(
            MetricKit.Num("a1", "cpu.usage", At(120), 30, "%"),
            MetricKit.Num("a1", "cpu.usage", At(0), 10, "%"),
            MetricKit.Num("a1", "cpu.usage", At(60), 20, "%"),
            MetricKit.Flag("a1", "network.up", At(0), true),
            MetricKit.Flag("a1", "network.up", At(60), false));

        Assert.Equal(new MetricAppendResult(5, 0, 0), result);

        IReadOnlyList<MetricSeriesData> series = await harness.QueryAsync(MetricKit.Query(["a1"], ["*"], At(-1), At(200)));

        Assert.Equal(["cpu.usage", "network.up"], series.Select(item => item.MetricId));
        MetricSeriesData cpu = series[0];
        Assert.Equal("a1", cpu.AgentId);
        Assert.Equal(MetricValueTypes.Number, cpu.ValueType);
        Assert.Equal("%", cpu.Unit);
        Assert.Equal("raw", cpu.Resolution);
        Assert.False(cpu.Truncated);
        Assert.Equal([10d, 20d, 30d], cpu.Points.Select(point => point.Value!.Value));
        Assert.Equal([At(0), At(60), At(120)], cpu.Points.Select(point => point.Ts));
        Assert.All(cpu.Points, point => Assert.Null(point.Text));

        MetricSeriesData network = series[1];
        Assert.Equal(MetricValueTypes.Bool, network.ValueType);
        Assert.Null(network.Unit);
        Assert.Equal([1d, 0d], network.Points.Select(point => point.Value!.Value));
    }

    [Fact]
    public async Task An_empty_batch_writes_nothing()
    {
        using var harness = new MetricStoreHarness();

        MetricAppendResult result = await harness.Store.AppendAsync([], CancellationToken.None);

        Assert.Equal(new MetricAppendResult(0, 0, 0), result);
        MetricStoreStats stats = await harness.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal(0, stats.Series);
    }

    [Fact]
    public async Task A_second_sample_with_the_same_time_replaces_the_first()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");

        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", At(0), 10));
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", At(0), 99));

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], At(-1), At(1))));
        Assert.Equal(99d, Assert.Single(series.Points).Value);
    }

    [Fact]
    public async Task A_changed_unit_or_type_of_a_metric_updates_its_series()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");

        _ = await harness.AppendAsync(MetricKit.Num("a1", "temp", At(0), 40, "C"));
        _ = await harness.AppendAsync(MetricKit.Num("a1", "temp", At(60), 104, "F"));

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["temp"], At(-1), At(100))));
        Assert.Equal("F", series.Unit);
        Assert.Equal(2, series.Points.Count);
        MetricStoreStats stats = await harness.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.Series);
    }

    [Fact]
    public async Task Text_metrics_are_a_change_log_and_an_unchanged_text_is_not_written_again()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");

        MetricAppendResult first = await harness.AppendAsync(
            MetricKit.Txt("a1", "session.state", At(0), "active"),
            MetricKit.Txt("a1", "session.state", At(60), "active"),
            MetricKit.Txt("a1", "session.state", At(120), "locked"),
            MetricKit.Txt("a1", "session.state", At(180), "locked"));
        MetricAppendResult second = await harness.AppendAsync(MetricKit.Txt("a1", "session.state", At(240), "locked"));
        MetricAppendResult third = await harness.AppendAsync(MetricKit.Txt("a1", "session.state", At(300), "active"));

        Assert.Equal(new MetricAppendResult(2, 2, 0), first);
        Assert.Equal(new MetricAppendResult(0, 1, 0), second);
        Assert.Equal(new MetricAppendResult(1, 0, 0), third);

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["session.state"], At(-1), At(400))));
        Assert.Equal("changes", series.Resolution);
        Assert.Equal(["active", "locked", "active"], series.Points.Select(point => point.Text));
        Assert.Equal([At(0), At(120), At(300)], series.Points.Select(point => point.Ts));
        Assert.All(series.Points, point => Assert.Null(point.Value));
    }

    [Fact]
    public async Task A_new_store_on_the_same_database_still_knows_the_last_text()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Txt("a1", "session.state", At(0), "active"));

        // A restart of the hub: no caches, only the database.
        var restarted = new SqliteMetricStore(harness.Database, harness.Options);
        MetricAppendResult result = await restarted.AppendAsync([MetricKit.Txt("a1", "session.state", At(60), "active")], CancellationToken.None);

        Assert.Equal(new MetricAppendResult(0, 1, 0), result);
    }

    [Fact]
    public async Task A_text_that_is_too_long_is_cut()
    {
        var options = new HubMetricsOptions { MaxTextLength = 10 };
        using var harness = new MetricStoreHarness(options);
        harness.AddAgents("a1");

        _ = await harness.AppendAsync(MetricKit.Txt("a1", "window.title", At(0), new string('x', 50), MetricValueTypes.Text));

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["window.title"], At(-1), At(1))));
        Assert.Equal(new string('x', 10), Assert.Single(series.Points).Text);
    }

    [Fact]
    public async Task The_text_in_effect_at_the_start_of_a_range_is_its_first_point()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(
            MetricKit.Txt("a1", "session.state", At(0), "active"),
            MetricKit.Txt("a1", "session.state", At(1000), "locked"));

        // Nothing changed between 100 and 500, but the text was "active".
        MetricSeriesData quiet = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["session.state"], At(100), At(500))));
        MetricPoint inEffect = Assert.Single(quiet.Points);
        Assert.Equal("active", inEffect.Text);
        Assert.Equal(At(0), inEffect.Ts);

        MetricSeriesData changing = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["session.state"], At(100), At(2000))));
        Assert.Equal(["active", "locked"], changing.Points.Select(point => point.Text));

        // Before the first entry there is nothing to know.
        MetricSeriesData before = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["session.state"], At(-500), At(-100))));
        Assert.Empty(before.Points);
    }

    [Fact]
    public async Task A_query_reads_several_agents_and_prefixes_ordered_by_agent_and_metric()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1", "a2", "a3");
        _ = await harness.AppendAsync(
            MetricKit.Num("a2", "memory.used", At(0), 1),
            MetricKit.Num("a2", "cpu.usage", At(0), 2),
            MetricKit.Num("a1", "cpu.usage", At(0), 3),
            MetricKit.Num("a1", "cpu.temperature", At(0), 4),
            MetricKit.Num("a1", "disk.free", At(0), 5),
            MetricKit.Num("a3", "cpu.usage", At(0), 6));

        IReadOnlyList<MetricSeriesData> series = await harness.QueryAsync(MetricKit.Query(["a2", "a1"], ["cpu.*", "memory.used"], At(-1), At(1)));

        Assert.Equal(
            [("a1", "cpu.temperature"), ("a1", "cpu.usage"), ("a2", "cpu.usage"), ("a2", "memory.used")],
            series.Select(item => (item.AgentId, item.MetricId)));

        // An exact id does not match a longer id, and a series without a match is absent, not empty.
        IReadOnlyList<MetricSeriesData> exact = await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu"], At(-1), At(1)));
        Assert.Empty(exact);
        Assert.Empty(await harness.QueryAsync(MetricKit.Query(["nobody"], ["*"], At(-1), At(1))));
    }

    [Fact]
    public async Task A_series_without_points_in_the_range_is_returned_with_no_points()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", At(0), 1));

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], At(500), At(600))));

        Assert.Empty(series.Points);
        Assert.False(series.Truncated);
    }

    [Fact]
    public async Task The_range_includes_both_ends()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(
            MetricKit.Num("a1", "cpu.usage", At(0), 0),
            MetricKit.Num("a1", "cpu.usage", At(60), 1),
            MetricKit.Num("a1", "cpu.usage", At(120), 2),
            MetricKit.Num("a1", "cpu.usage", At(180), 3));

        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], At(60), At(120))));

        Assert.Equal([1d, 2d], series.Points.Select(point => point.Value!.Value));
    }

    [Fact]
    public async Task A_series_with_more_points_than_allowed_is_cut_and_marked_but_an_exact_fit_is_not()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync([.. Enumerable.Range(0, 5).Select(index => MetricKit.Num("a1", "cpu.usage", At(index * 60), index))]);

        MetricSeriesData cut = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], At(-1), At(1000), maxPoints: 3)));
        Assert.True(cut.Truncated);
        Assert.Equal([0d, 1d, 2d], cut.Points.Select(point => point.Value!.Value));

        MetricSeriesData exact = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], At(-1), At(1000), maxPoints: 5)));
        Assert.False(exact.Truncated);
        Assert.Equal(5, exact.Points.Count);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], At(-1), At(1000), maxPoints: 0)));
    }

    [Fact]
    public async Task The_newest_value_of_every_series_is_listed_by_metric_id()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1", "a2");
        _ = await harness.AppendAsync(
            MetricKit.Num("a1", "memory.used", At(0), 1, "GB"),
            MetricKit.Num("a1", "memory.used", At(60), 2, "GB"),
            MetricKit.Num("a1", "cpu.usage", At(30), 50, "%"),
            MetricKit.Flag("a1", "network.up", At(10), true),
            MetricKit.Txt("a1", "session.state", At(5), "active"),
            MetricKit.Txt("a1", "session.state", At(50), "locked"),
            MetricKit.Num("a2", "cpu.usage", At(0), 7));

        IReadOnlyList<LatestMetric> latest = await harness.Store.GetLatestAsync("a1", CancellationToken.None);

        Assert.Equal(["cpu.usage", "memory.used", "network.up", "session.state"], latest.Select(item => item.MetricId));
        Assert.Equal(new LatestMetric("a1", "cpu.usage", MetricValueTypes.Number, "%", At(30), 50, null), latest[0]);
        Assert.Equal(2d, latest[1].Value);
        Assert.Equal(At(60), latest[1].Ts);
        Assert.Equal(1d, latest[2].Value);
        Assert.Equal("locked", latest[3].Text);
        Assert.Null(latest[3].Value);

        Assert.Empty(await harness.Store.GetLatestAsync("nobody", CancellationToken.None));
    }

    [Fact]
    public async Task Counting_series_matches_what_a_query_would_return()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1", "a2");
        _ = await harness.AppendAsync(
            MetricKit.Num("a1", "cpu.usage", At(0), 1),
            MetricKit.Num("a1", "cpu.temperature", At(0), 1),
            MetricKit.Txt("a1", "session.state", At(0), "active"),
            MetricKit.Num("a2", "cpu.usage", At(0), 1));

        Assert.Equal(4, await harness.Store.CountSeriesAsync(["a1", "a2"], ["*"], CancellationToken.None));
        Assert.Equal(3, await harness.Store.CountSeriesAsync(["a1", "a2"], ["cpu.*"], CancellationToken.None));
        Assert.Equal(1, await harness.Store.CountSeriesAsync(["a1", "a1"], ["cpu.usage"], CancellationToken.None));
        Assert.Equal(0, await harness.Store.CountSeriesAsync(["a9"], ["*"], CancellationToken.None));
    }

    [Fact]
    public async Task Samples_of_an_agent_that_does_not_exist_are_skipped_and_the_others_are_kept()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");

        MetricAppendResult result = await harness.AppendAsync(
            MetricKit.Num("ghost", "cpu.usage", At(0), 1),
            MetricKit.Num("ghost", "memory.used", At(0), 1),
            MetricKit.Num("a1", "cpu.usage", At(0), 2));

        Assert.Equal(new MetricAppendResult(1, 0, 2), result);
        MetricStoreStats stats = await harness.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.Series);
        Assert.Equal(1, stats.RawRows);
    }

    [Fact]
    public async Task Samples_for_an_agent_that_was_removed_after_its_series_were_cached_are_skipped_and_the_agent_can_come_back()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", At(0), 1), MetricKit.Txt("a1", "session.state", At(0), "active"));

        MetricKit.DeleteAgentRow(harness.Database, "a1");
        MetricAppendResult afterRemoval = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", At(60), 2), MetricKit.Txt("a1", "session.state", At(60), "locked"));

        Assert.Equal(new MetricAppendResult(0, 0, 2), afterRemoval);
        MetricStoreStats empty = await harness.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal([0L, 0L, 0L], MetricKit.Seq(empty.Series, empty.RawRows, empty.StateRows));

        // The agent enrolls again with the same id: the stale series ids must not be used.
        harness.AddAgents("a1");
        MetricAppendResult again = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", At(120), 3), MetricKit.Txt("a1", "session.state", At(120), "locked"));

        Assert.Equal(new MetricAppendResult(2, 0, 0), again);
        MetricSeriesData series = Assert.Single(await harness.QueryAsync(MetricKit.Query(["a1"], ["cpu.usage"], At(-1), At(1000))));
        Assert.Equal([3d], series.Points.Select(point => point.Value!.Value));
    }

    [Fact]
    public async Task Removing_an_agent_removes_its_series_and_all_of_their_data_and_nothing_else()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1", "a2");
        _ = await harness.AppendAsync(
            MetricKit.Num("a1", "cpu.usage", At(0), 1),
            MetricKit.Num("a1", "memory.used", At(0), 1),
            MetricKit.Txt("a1", "session.state", At(0), "active"),
            MetricKit.Num("a2", "cpu.usage", At(0), 1));
        _ = await harness.Store.RollUpAsync(At(7200), CancellationToken.None);
        MetricStoreStats before = await harness.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal([4L, 3L, 1L, 3L, 3L], MetricKit.Seq(before.Series, before.RawRows, before.StateRows, before.Rollup5mRows, before.Rollup1hRows));

        int removed = await harness.Store.RemoveAgentAsync("a1", CancellationToken.None);

        Assert.Equal(3, removed);
        MetricStoreStats after = await harness.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal([1L, 1L, 0L, 1L, 1L], MetricKit.Seq(after.Series, after.RawRows, after.StateRows, after.Rollup5mRows, after.Rollup1hRows));
        Assert.Empty(await harness.Store.GetLatestAsync("a1", CancellationToken.None));
        Assert.Single(await harness.Store.GetLatestAsync("a2", CancellationToken.None));

        // Removing again, and removing an agent without series, is not an error.
        Assert.Equal(0, await harness.Store.RemoveAgentAsync("a1", CancellationToken.None));
        Assert.Equal(0, await harness.Store.RemoveAgentAsync("never", CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_agent_row_removes_its_series_through_the_foreign_keys()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(MetricKit.Num("a1", "cpu.usage", At(0), 1), MetricKit.Txt("a1", "session.state", At(0), "active"));
        _ = await harness.Store.RollUpAsync(At(7200), CancellationToken.None);

        MetricKit.DeleteAgentRow(harness.Database, "a1");

        MetricStoreStats stats = await harness.Store.GetStatsAsync(CancellationToken.None);
        Assert.Equal([0L, 0L, 0L, 0L, 0L], MetricKit.Seq(stats.Series, stats.RawRows, stats.StateRows, stats.Rollup5mRows, stats.Rollup1hRows));
    }

    [Fact]
    public async Task The_statistics_count_the_rows_and_measure_the_database()
    {
        using var harness = new MetricStoreHarness();
        harness.AddAgents("a1");
        _ = await harness.AppendAsync(
            MetricKit.Num("a1", "cpu.usage", At(0), 1),
            MetricKit.Num("a1", "cpu.usage", At(60), 2),
            MetricKit.Txt("a1", "session.state", At(0), "active"));

        MetricStoreStats stats = await harness.Store.GetStatsAsync(CancellationToken.None);

        Assert.Equal(2, stats.Series);
        Assert.Equal(2, stats.RawRows);
        Assert.Equal(1, stats.StateRows);
        Assert.True(stats.DatabaseBytes > 0);
        Assert.InRange(stats.FreeBytes, 0, stats.DatabaseBytes);
    }
}

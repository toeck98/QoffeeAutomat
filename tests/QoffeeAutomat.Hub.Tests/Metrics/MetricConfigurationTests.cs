using System.Globalization;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>The settings of the metric collection (T910): patterns, the collection policy, the options of the operator, the choice of the resolution of a query and the schema.</summary>
[Trait("Category", "Metrics")]
public sealed class MetricConfigurationTests
{
    private static readonly DateTimeOffset Now = MetricKit.T0;

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        Dictionary<string, string> map = variables.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out string? value) ? value : null;
    }

    // ---- patterns ----

    [Theory]
    [InlineData("cpu.usage", true)]
    [InlineData("cpu.*", true)]
    [InlineData("*", true)]
    [InlineData("", false)]
    [InlineData("cp*u", false)]
    [InlineData("*cpu", false)]
    [InlineData("cpu.*.*", false)]
    [InlineData("a\nb", false)]
    public void A_pattern_is_an_id_or_a_prefix_with_a_star_at_the_end(string pattern, bool valid)
    {
        Assert.Equal(valid, MetricPattern.IsValid(pattern));
    }

    [Fact]
    public void A_pattern_is_limited_to_the_length_of_a_metric_id_plus_the_star()
    {
        Assert.False(MetricPattern.IsValid(null));
        Assert.True(MetricPattern.IsValid(new string('x', 128) + "*"));
        Assert.False(MetricPattern.IsValid(new string('x', 130)));
    }

    [Theory]
    [InlineData("cpu.usage", "cpu.usage", true)]
    [InlineData("cpu.usage", "cpu.usage.max", false)]
    [InlineData("cpu.*", "cpu.usage", true)]
    [InlineData("cpu.*", "cpu.", true)]
    [InlineData("cpu.*", "cpu", false)]
    [InlineData("cpu.*", "memory.cpu.usage", false)]
    [InlineData("*", "anything.at.all", true)]
    [InlineData("*", "", true)]
    [InlineData("CPU.*", "cpu.usage", false)]
    public void A_pattern_matches_an_equal_id_or_an_id_with_its_prefix(string pattern, string metricId, bool expected)
    {
        Assert.Equal(expected, MetricPattern.Matches(pattern, metricId));
        Assert.Equal(expected, MetricPattern.MatchesAny(["unrelated", pattern], metricId));
    }

    [Fact]
    public void No_pattern_matches_nothing()
    {
        Assert.False(MetricPattern.MatchesAny([], "cpu.usage"));
        Assert.False(MetricCollectionPolicy.Nothing.Collects("cpu.usage"));
        Assert.True(MetricCollectionPolicy.Default.Collects("cpu.usage"));
    }

    // ---- the policy ----

    [Fact]
    public void The_default_policy_is_every_metric_every_60_seconds()
    {
        Assert.Equal(["*"], MetricCollectionPolicy.Default.Metrics);
        Assert.Equal(TimeSpan.FromSeconds(60), MetricCollectionPolicy.Default.Interval);
        MetricCollectionPolicy.Default.Validate();
        MetricCollectionPolicy.Nothing.Validate();
        Assert.Equal(MetricCollectionPolicy.Default, new GlobalMetricPolicySource(new HubMetricsOptions()).GetPolicy("any-agent"));
    }

    [Fact]
    public void A_policy_with_a_bad_pattern_or_interval_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MetricCollectionPolicy(["cpu.*", "bad*pattern"], TimeSpan.FromSeconds(60)).Validate());
        Assert.Throws<ArgumentException>(() => new MetricCollectionPolicy(["cpu.*"], TimeSpan.FromMilliseconds(500)).Validate());
        Assert.Throws<ArgumentException>(() => new MetricCollectionPolicy(["cpu.*"], TimeSpan.FromDays(2)).Validate());
        new MetricCollectionPolicy(["cpu.*"], TimeSpan.FromSeconds(1)).Validate();
        new MetricCollectionPolicy(["cpu.*"], TimeSpan.FromDays(1)).Validate();
    }

    // ---- the options ----

    [Fact]
    public void Without_settings_the_options_are_the_defaults_of_the_specification()
    {
        HubMetricsOptions options = HubMetricsOptions.FromEnvironment(Environment());

        Assert.Equal(MetricCollectionPolicy.Default.Metrics, options.DefaultPolicy.Metrics);
        Assert.Equal(TimeSpan.FromSeconds(60), options.DefaultPolicy.Interval);
        Assert.Equal(TimeSpan.FromHours(48), options.RawRetention);
        Assert.Equal(TimeSpan.FromDays(90), options.StateRetention);
        Assert.Equal(TimeSpan.FromDays(30), options.Rollup5mRetention);
        Assert.Equal(TimeSpan.FromDays(730), options.Rollup1hRetention);
        options.Validate();
    }

    [Fact]
    public void The_operator_sets_the_policy_and_the_retention_through_the_environment()
    {
        HubMetricsOptions options = HubMetricsOptions.FromEnvironment(
            Environment(
                (HubMetricsOptions.CollectEnvironmentVariable, "cpu.*, memory.used;network.up"),
                (HubMetricsOptions.IntervalEnvironmentVariable, "30"),
                (HubMetricsOptions.RawRetentionEnvironmentVariable, "72"),
                (HubMetricsOptions.StateRetentionEnvironmentVariable, "10"),
                (HubMetricsOptions.Rollup5mRetentionEnvironmentVariable, "7"),
                (HubMetricsOptions.Rollup1hRetentionEnvironmentVariable, "365")));

        Assert.Equal(["cpu.*", "memory.used", "network.up"], options.DefaultPolicy.Metrics);
        Assert.Equal(TimeSpan.FromSeconds(30), options.DefaultPolicy.Interval);
        Assert.Equal(TimeSpan.FromHours(72), options.RawRetention);
        Assert.Equal(TimeSpan.FromDays(10), options.StateRetention);
        Assert.Equal(TimeSpan.FromDays(7), options.Rollup5mRetention);
        Assert.Equal(TimeSpan.FromDays(365), options.Rollup1hRetention);
    }

    [Fact]
    public void A_blank_setting_counts_as_not_set()
    {
        HubMetricsOptions options = HubMetricsOptions.FromEnvironment(
            Environment((HubMetricsOptions.CollectEnvironmentVariable, "  "), (HubMetricsOptions.RawRetentionEnvironmentVariable, string.Empty)));

        Assert.Equal(["*"], options.DefaultPolicy.Metrics);
        Assert.Equal(TimeSpan.FromHours(48), options.RawRetention);
    }

    [Theory]
    [InlineData("QOFFEE_HUB_METRICS_INTERVAL", "abc")]
    [InlineData("QOFFEE_HUB_METRICS_INTERVAL", "0")]
    [InlineData("QOFFEE_HUB_METRICS_INTERVAL", "-5")]
    [InlineData("QOFFEE_HUB_METRICS_INTERVAL", "0.5")]
    [InlineData("QOFFEE_HUB_METRICS_INTERVAL", "100000000")]
    [InlineData("QOFFEE_HUB_METRICS_COLLECT", "cpu*usage")]
    [InlineData("QOFFEE_HUB_RETENTION_RAW_HOURS", "0")]
    [InlineData("QOFFEE_HUB_RETENTION_RAW_HOURS", "0.5")]
    [InlineData("QOFFEE_HUB_RETENTION_STATES_DAYS", "forever")]
    [InlineData("QOFFEE_HUB_RETENTION_5M_DAYS", "-1")]
    [InlineData("QOFFEE_HUB_RETENTION_1H_DAYS", "0.5")]
    public void A_setting_that_is_not_valid_stops_the_hub_with_a_clear_message(string name, string value)
    {
        HubStartupException exception = Assert.Throws<HubStartupException>(() => HubMetricsOptions.FromEnvironment(Environment((name, value))));

        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void The_environment_variable_names_are_the_documented_ones()
    {
        Assert.Equal("QOFFEE_HUB_METRICS_COLLECT", HubMetricsOptions.CollectEnvironmentVariable);
        Assert.Equal("QOFFEE_HUB_METRICS_INTERVAL", HubMetricsOptions.IntervalEnvironmentVariable);
        Assert.Equal("QOFFEE_HUB_RETENTION_RAW_HOURS", HubMetricsOptions.RawRetentionEnvironmentVariable);
        Assert.Equal("QOFFEE_HUB_RETENTION_STATES_DAYS", HubMetricsOptions.StateRetentionEnvironmentVariable);
        Assert.Equal("QOFFEE_HUB_RETENTION_5M_DAYS", HubMetricsOptions.Rollup5mRetentionEnvironmentVariable);
        Assert.Equal("QOFFEE_HUB_RETENTION_1H_DAYS", HubMetricsOptions.Rollup1hRetentionEnvironmentVariable);
    }

    [Fact]
    public void Options_that_are_out_of_range_are_refused_by_validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { RawRetention = TimeSpan.FromMinutes(30) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { StateRetention = TimeSpan.FromHours(1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { Rollup5mRetention = TimeSpan.FromMinutes(1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { Rollup1hRetention = TimeSpan.FromHours(1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { FlushInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { FlushBatchSize = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { FlushBatchSize = 100, MaxBufferedSamples = 10 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { RollupInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { RollupGrace = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { RetentionInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { MaxQueryPoints = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { MaxQuerySeries = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { MaxTextLength = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubMetricsOptions { MaxClockSkew = TimeSpan.FromSeconds(-1) }.Validate());
        new HubMetricsOptions().Validate();
    }

    // ---- value types and resolutions ----

    [Fact]
    public void Numbers_and_booleans_are_numeric_and_every_other_known_type_is_a_text()
    {
        Assert.True(MetricValueTypes.IsNumeric(MetricValueTypes.Number));
        Assert.True(MetricValueTypes.IsNumeric(MetricValueTypes.Bool));
        foreach (string text in new[] { MetricValueTypes.Text, MetricValueTypes.Timestamp, MetricValueTypes.Choice })
        {
            Assert.False(MetricValueTypes.IsNumeric(text));
            Assert.True(MetricValueTypes.IsKnown(text));
        }

        Assert.False(MetricValueTypes.IsKnown("blob"));
        Assert.False(MetricValueTypes.IsKnown(string.Empty));
    }

    [Fact]
    public void Resolutions_have_names_and_widths_and_round_trip()
    {
        foreach (MetricResolution resolution in Enum.GetValues<MetricResolution>())
        {
            Assert.True(MetricResolutions.TryParse(MetricResolutions.ToText(resolution), out MetricResolution parsed));
            Assert.Equal(resolution, parsed);
        }

        Assert.Equal("raw", MetricResolutions.ToText(MetricResolution.Raw));
        Assert.Equal("5m", MetricResolutions.ToText(MetricResolution.FiveMinutes));
        Assert.Equal("1h", MetricResolutions.ToText(MetricResolution.OneHour));
        Assert.False(MetricResolutions.TryParse("auto", out _));
        Assert.False(MetricResolutions.TryParse("1H", out _));
        Assert.False(MetricResolutions.TryParse(null, out _));
        Assert.Equal(300_000, MetricResolutions.BucketMilliseconds(MetricResolution.FiveMinutes));
        Assert.Equal(3_600_000, MetricResolutions.BucketMilliseconds(MetricResolution.OneHour));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricResolutions.BucketMilliseconds(MetricResolution.Raw));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricResolutions.ToText((MetricResolution)99));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(299_999, 0)]
    [InlineData(300_000, 300_000)]
    [InlineData(300_001, 300_000)]
    [InlineData(-1, -300_000)]
    [InlineData(-300_000, -300_000)]
    [InlineData(-300_001, -600_000)]
    public void A_time_belongs_to_the_bucket_that_starts_at_or_before_it(long milliseconds, long expected)
    {
        Assert.Equal(expected, MetricResolutions.Floor(milliseconds, 300_000));
    }

    // ---- the resolution of a query ----

    [Theory]
    [InlineData(1, MetricResolution.Raw)]
    [InlineData(6, MetricResolution.Raw)]
    [InlineData(7, MetricResolution.FiveMinutes)]
    [InlineData(24 * 7, MetricResolution.FiveMinutes)]
    [InlineData((24 * 7) + 1, MetricResolution.OneHour)]
    [InlineData(24 * 90, MetricResolution.OneHour)]
    public void The_resolution_follows_from_the_length_of_the_range(int hours, MetricResolution expected)
    {
        var options = new HubMetricsOptions { Rollup5mRetention = TimeSpan.FromDays(365) };

        MetricResolution chosen = MetricQueryPlanner.Choose(Now.AddHours(-hours), Now, Now, options);

        Assert.Equal(expected, chosen);
    }

    [Fact]
    public void A_range_that_ends_a_little_before_the_limit_still_gets_the_finer_resolution_and_one_second_more_gets_the_coarser()
    {
        var options = new HubMetricsOptions { Rollup5mRetention = TimeSpan.FromDays(365) };

        Assert.Equal(MetricResolution.Raw, MetricQueryPlanner.Choose(Now.AddHours(-6), Now, Now, options));
        Assert.Equal(MetricResolution.FiveMinutes, MetricQueryPlanner.Choose(Now.AddHours(-6).AddSeconds(-1), Now, Now, options));
        Assert.Equal(MetricResolution.FiveMinutes, MetricQueryPlanner.Choose(Now.AddDays(-7), Now, Now, options));
        Assert.Equal(MetricResolution.OneHour, MetricQueryPlanner.Choose(Now.AddDays(-7).AddSeconds(-1), Now, Now, options));
    }

    [Fact]
    public void A_range_that_starts_before_the_retention_of_the_resolution_gets_the_next_coarser_one()
    {
        var options = new HubMetricsOptions();

        // One hour of 3 days ago: the raw samples are gone (48 hours), the five minute buckets are there (30 days).
        Assert.Equal(MetricResolution.FiveMinutes, MetricQueryPlanner.Choose(Now.AddDays(-3).AddHours(-1), Now.AddDays(-3), Now, options));

        // Three days of 40 days ago: the five minute buckets are gone too.
        Assert.Equal(MetricResolution.OneHour, MetricQueryPlanner.Choose(Now.AddDays(-43), Now.AddDays(-40), Now, options));

        // One hour of 100 days ago: raw, then five minutes, then the hour.
        Assert.Equal(MetricResolution.OneHour, MetricQueryPlanner.Choose(Now.AddDays(-100).AddHours(-1), Now.AddDays(-100), Now, options));

        // Recent data keeps its resolution.
        Assert.Equal(MetricResolution.Raw, MetricQueryPlanner.Choose(Now.AddHours(-47), Now.AddHours(-46), Now, options));
    }

    [Fact]
    public void The_planner_needs_options()
    {
        Assert.Throws<ArgumentNullException>(() => MetricQueryPlanner.Choose(Now.AddHours(-1), Now, Now, null!));
    }

    // ---- the schema ----

    [Fact]
    public void The_migration_creates_the_metric_tables_and_their_indexes()
    {
        using var harness = new MetricStoreHarness();

        string[] names = harness.Database.Query(connection =>
        {
            using Microsoft.Data.Sqlite.SqliteCommand command = HubSql.Command(
                connection,
                null,
                "SELECT name FROM sqlite_master WHERE name LIKE 'metric\\_%' ESCAPE '\\' OR name LIKE 'ix\\_metric\\_%' ESCAPE '\\' ORDER BY name;");
            using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
            var found = new List<string>();
            while (reader.Read())
            {
                found.Add(reader.GetString(0));
            }

            return found.Where(name => !name.StartsWith("sqlite_", StringComparison.Ordinal)).ToArray();
        });

        Assert.Contains("metric_series", names);
        Assert.Contains("metric_raw", names);
        Assert.Contains("metric_states", names);
        Assert.Contains("metric_rollup_5m", names);
        Assert.Contains("metric_rollup_1h", names);
        Assert.Contains("ix_metric_raw_ts", names);
        Assert.Contains("ix_metric_states_ts", names);
        Assert.Contains("ix_metric_rollup_5m_bucket", names);
        Assert.Contains("ix_metric_rollup_1h_bucket", names);
        Assert.True(harness.Database.SchemaVersion >= 7, "The metric tables came with schema version 7 but the database is at " + harness.Database.SchemaVersion.ToString(CultureInfo.InvariantCulture) + ".");
        Assert.Equal(HubSchema.LatestVersion, harness.Database.SchemaVersion);
    }
}

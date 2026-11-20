using System.Globalization;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Metrics;

/// <summary>
/// The settings of the metric collection of the hub (PLAN.md S20 "Metrics on the hub", T910): the global collection policy, the retention of every table, and the
/// timings of the writer and of the roll-up and retention jobs. The defaults are the values of the specification; <see cref="FromEnvironment"/> reads the ones an
/// operator changes, tests set the others through the service collection.
/// </summary>
public sealed class HubMetricsOptions
{
    /// <summary>The environment variable with the metric ids or prefixes to collect, separated by commas (default <c>*</c>: every metric).</summary>
    public const string CollectEnvironmentVariable = "QOFFEE_HUB_METRICS_COLLECT";

    /// <summary>The environment variable with the collection interval in seconds (default 60).</summary>
    public const string IntervalEnvironmentVariable = "QOFFEE_HUB_METRICS_INTERVAL";

    /// <summary>The environment variable with the retention of raw samples in hours (default 48).</summary>
    public const string RawRetentionEnvironmentVariable = "QOFFEE_HUB_RETENTION_RAW_HOURS";

    /// <summary>The environment variable with the retention of the change log of text metrics in days (default 90).</summary>
    public const string StateRetentionEnvironmentVariable = "QOFFEE_HUB_RETENTION_STATES_DAYS";

    /// <summary>The environment variable with the retention of the five minute roll-ups in days (default 30).</summary>
    public const string Rollup5mRetentionEnvironmentVariable = "QOFFEE_HUB_RETENTION_5M_DAYS";

    /// <summary>The environment variable with the retention of the one hour roll-ups in days (default 730).</summary>
    public const string Rollup1hRetentionEnvironmentVariable = "QOFFEE_HUB_RETENTION_1H_DAYS";

    private static readonly char[] ListSeparators = [',', ';', ' '];

    /// <summary>Which metrics are collected and how often, for every agent.</summary>
    public MetricCollectionPolicy DefaultPolicy { get; init; } = MetricCollectionPolicy.Default;

    /// <summary>How long raw samples are kept (default 48 hours).</summary>
    public TimeSpan RawRetention { get; init; } = TimeSpan.FromHours(48);

    /// <summary>How long the change log of text metrics is kept (default 90 days). The newest entry of a series is always kept.</summary>
    public TimeSpan StateRetention { get; init; } = TimeSpan.FromDays(90);

    /// <summary>How long five minute roll-ups are kept (default 30 days).</summary>
    public TimeSpan Rollup5mRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How long one hour roll-ups are kept (default 2 years).</summary>
    public TimeSpan Rollup1hRetention { get; init; } = TimeSpan.FromDays(730);

    /// <summary>How often the samples in memory are written to the database.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The number of samples in memory that makes the writer write at once.</summary>
    public int FlushBatchSize { get; init; } = 2000;

    /// <summary>The most samples that wait in memory for the writer; further samples are dropped and counted until the database has caught up.</summary>
    public int MaxBufferedSamples { get; init; } = 200_000;

    /// <summary>How often the roll-up job runs.</summary>
    public TimeSpan RollupInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long after the end of a bucket it is rolled up, so that the last samples of the bucket have reached the database.</summary>
    public TimeSpan RollupGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the retention job runs.</summary>
    public TimeSpan RetentionInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The most points per series that a query returns when the request does not ask for fewer.</summary>
    public int MaxQueryPoints { get; init; } = 5000;

    /// <summary>The most series that one query may select.</summary>
    public int MaxQuerySeries { get; init; } = 200;

    /// <summary>The longest text of a text metric that is stored; a longer text is cut.</summary>
    public int MaxTextLength { get; init; } = 1024;

    /// <summary>A sample whose time differs from the hub's clock by more than this gets the time of the hub (an agent with a wrong clock).</summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Reads the settings that an operator changes from the environment; every other setting keeps its default.</summary>
    /// <param name="environment">Reads an environment variable; null or white space counts as not set.</param>
    /// <returns>The options.</returns>
    /// <exception cref="HubStartupException">A value is not a positive number, or the policy is not valid.</exception>
    public static HubMetricsOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var defaults = new HubMetricsOptions();
        string? collect = Value(environment, CollectEnvironmentVariable);
        string[] patterns = collect is null
            ? [.. defaults.DefaultPolicy.Metrics]
            : collect.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        TimeSpan interval = Positive(environment, IntervalEnvironmentVariable, TimeSpan.FromSeconds, defaults.DefaultPolicy.Interval);
        var policy = new MetricCollectionPolicy(patterns, interval);
        try
        {
            policy.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new HubStartupException("The metric collection policy is not valid: " + exception.Message, exception);
        }

        var options = new HubMetricsOptions
        {
            DefaultPolicy = policy,
            RawRetention = Positive(environment, RawRetentionEnvironmentVariable, TimeSpan.FromHours, defaults.RawRetention),
            StateRetention = Positive(environment, StateRetentionEnvironmentVariable, TimeSpan.FromDays, defaults.StateRetention),
            Rollup5mRetention = Positive(environment, Rollup5mRetentionEnvironmentVariable, TimeSpan.FromDays, defaults.Rollup5mRetention),
            Rollup1hRetention = Positive(environment, Rollup1hRetentionEnvironmentVariable, TimeSpan.FromDays, defaults.Rollup1hRetention),
        };
        try
        {
            options.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new HubStartupException("The metric settings are not valid: " + exception.Message, exception);
        }

        return options;
    }

    /// <summary>Checks the settings.</summary>
    /// <exception cref="ArgumentException">A setting is out of range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A number or a time span is out of range.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(DefaultPolicy);
        DefaultPolicy.Validate();

        ArgumentOutOfRangeException.ThrowIfLessThan(RawRetention, TimeSpan.FromHours(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(StateRetention, TimeSpan.FromDays(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(Rollup5mRetention, TimeSpan.FromHours(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(Rollup1hRetention, TimeSpan.FromDays(1));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FlushInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(FlushBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBufferedSamples, FlushBatchSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RollupInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(RollupGrace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RetentionInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQueryPoints, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQuerySeries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTextLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxClockSkew, TimeSpan.Zero);
    }

    private static string? Value(Func<string, string?> environment, string name)
    {
        string? value = environment(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static TimeSpan Positive(Func<string, string?> environment, string name, Func<double, TimeSpan> unit, TimeSpan fallback)
    {
        string? text = Value(environment, name);
        if (text is null)
        {
            return fallback;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number <= 0 || number > 100_000)
        {
            throw new HubStartupException($"{name} must be a positive number, but it is '{text}'.");
        }

        return unit(number);
    }
}

namespace QoffeeAutomat.Core.Metrics;

/// <summary>Settings of <see cref="MetricEngine"/>.</summary>
public sealed class MetricEngineOptions
{
    /// <summary>The interval of a metric that declares none.</summary>
    public double DefaultIntervalSeconds { get; init; } = 5;

    /// <summary>The shortest sampling interval; shorter positive intervals are raised to it. Zero stays "event-driven".</summary>
    public double MinimumIntervalSeconds { get; init; } = 0.1;

    /// <summary>
    /// The shortest sampling interval of a custom sensor that runs a script (PLAN.md T215); shorter positive
    /// intervals are raised to it, so the manifest announces the interval that really applies. Zero stays "event-driven".
    /// </summary>
    public double MinimumScriptIntervalSeconds { get; init; } = 5;

    /// <summary>A subscriber gets at most one update per this time (PLAN.md S07: once per second).</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long samples are kept for charts.</summary>
    public TimeSpan HistoryRetention { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The most samples kept per metric. The default holds an hour of a metric that is sampled every
    /// half second; faster metrics keep a shorter window.
    /// </summary>
    public int HistoryMaxSamples { get; init; } = 7200;

    /// <summary>The wait after the first failure of a provider; it doubles with every further failure.</summary>
    public TimeSpan ProviderBackoffInitial { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between attempts to sample a failing provider.</summary>
    public TimeSpan ProviderBackoffMax { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A provider call that takes longer is cancelled and counts as a failure.</summary>
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        Require(DefaultIntervalSeconds > 0 && double.IsFinite(DefaultIntervalSeconds), nameof(DefaultIntervalSeconds));
        Require(MinimumIntervalSeconds > 0 && double.IsFinite(MinimumIntervalSeconds), nameof(MinimumIntervalSeconds));
        Require(MinimumScriptIntervalSeconds > 0 && double.IsFinite(MinimumScriptIntervalSeconds), nameof(MinimumScriptIntervalSeconds));
        Require(FlushInterval >= TimeSpan.Zero, nameof(FlushInterval));
        Require(HistoryRetention > TimeSpan.Zero, nameof(HistoryRetention));
        Require(HistoryMaxSamples > 0, nameof(HistoryMaxSamples));
        Require(ProviderBackoffInitial > TimeSpan.Zero, nameof(ProviderBackoffInitial));
        Require(ProviderBackoffMax >= ProviderBackoffInitial, nameof(ProviderBackoffMax));
        Require(ProviderTimeout > TimeSpan.Zero, nameof(ProviderTimeout));
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new ArgumentOutOfRangeException(name, "Invalid metric engine option.");
        }
    }
}

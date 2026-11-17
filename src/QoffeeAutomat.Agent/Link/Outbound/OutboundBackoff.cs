namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>
/// The reconnect delays of an outbound link (PLAN.md S20: from one second to five minutes, with jitter): the step doubles with every
/// failure up to the maximum, and each delay is a random value between half of the step and the whole step so that many agents that
/// lost the same peer do not come back at the same moment. <see cref="Reset"/> starts again at the first step.
/// </summary>
public sealed class OutboundBackoff
{
    private const int MaxDoublings = 30;

    private readonly TimeSpan _min;
    private readonly TimeSpan _max;
    private readonly Func<double> _random;
    private int _attempt;

    /// <summary>Creates the sequence.</summary>
    /// <param name="min">The first step.</param>
    /// <param name="max">The largest step.</param>
    /// <param name="random">A source of numbers in [0, 1); null uses <see cref="Random.Shared"/>.</param>
    public OutboundBackoff(TimeSpan min, TimeSpan max, Func<double>? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(min, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);

        _min = min;
        _max = max;
        _random = random ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>The number of delays handed out since the last reset.</summary>
    public int Attempts => _attempt;

    /// <summary>Returns the next delay and advances the sequence.</summary>
    /// <returns>The delay, between half of the current step and the step.</returns>
    public TimeSpan Next()
    {
        double step = Math.Min(_max.TotalSeconds, _min.TotalSeconds * Math.Pow(2, Math.Min(_attempt, MaxDoublings)));
        _attempt = Math.Min(_attempt + 1, MaxDoublings);
        double jitter = Math.Clamp(_random(), 0.0, 1.0);
        return TimeSpan.FromSeconds(step * (0.5 + (0.5 * jitter)));
    }

    /// <summary>Starts again at the first step (a connection was established, or the configuration changed).</summary>
    public void Reset()
    {
        _attempt = 0;
    }
}

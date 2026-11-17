using QoffeeAutomat.Agent.Link.Outbound;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>The reconnect delays of the outbound links (PLAN.md S20: one second to five minutes, with jitter).</summary>
[Trait("Category", "OutboundLink")]
public sealed class OutboundBackoffTests
{
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Fact]
    public void The_delay_doubles_from_one_second_and_stops_at_five_minutes()
    {
        var backoff = new OutboundBackoff(Min, Max, () => 1.0);

        double[] seconds = [.. Enumerable.Range(0, 11).Select(_ => backoff.Next().TotalSeconds)];

        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 300, 300 }, seconds);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.75)]
    [InlineData(1.0, 1.0)]
    public void Jitter_places_the_delay_between_half_of_the_step_and_the_step(double random, double expectedSeconds)
    {
        var backoff = new OutboundBackoff(Min, Max, () => random);

        Assert.Equal(expectedSeconds, backoff.Next().TotalSeconds, precision: 6);
    }

    [Fact]
    public void Reset_starts_again_at_the_first_step()
    {
        var backoff = new OutboundBackoff(Min, Max, () => 1.0);
        _ = backoff.Next();
        _ = backoff.Next();
        _ = backoff.Next();
        Assert.Equal(3, backoff.Attempts);

        backoff.Reset();

        Assert.Equal(0, backoff.Attempts);
        Assert.Equal(1.0, backoff.Next().TotalSeconds, precision: 6);
    }

    [Fact]
    public void Real_jitter_stays_inside_the_bounds_of_every_step()
    {
        var backoff = new OutboundBackoff(Min, Max);

        for (int attempt = 0; attempt < 40; attempt++)
        {
            double step = Math.Min(Max.TotalSeconds, Math.Pow(2, attempt));
            double delay = backoff.Next().TotalSeconds;

            Assert.InRange(delay, step * 0.5, step);
        }
    }

    [Fact]
    public void A_far_too_long_run_of_failures_does_not_overflow()
    {
        var backoff = new OutboundBackoff(Min, Max, () => 1.0);

        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            Assert.True(backoff.Next().TotalSeconds <= Max.TotalSeconds);
        }
    }

    [Fact]
    public void A_maximum_below_the_minimum_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboundBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboundBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }
}

using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Auth;

/// <summary>Rate limiting and lockout of sign-in attempts (T212, PLAN.md S09 item 1).</summary>
[Trait("Category", "Auth")]
public sealed class AttemptLimiterTests
{
    private readonly SimulatedClock _clock = new();
    private readonly AuthOptions _options = new();

    [Fact]
    public void Five_failures_lock_the_caller_out_for_fifteen_minutes()
    {
        var limiter = new AttemptLimiter(_clock, _options);

        for (int attempt = 1; attempt < 5; attempt++)
        {
            limiter.RecordFailure("login", "10.0.0.1");
            Assert.Null(limiter.GetLockout("login", "10.0.0.1"));
        }

        limiter.RecordFailure("login", "10.0.0.1");

        Assert.Equal(TimeSpan.FromMinutes(15), limiter.GetLockout("login", "10.0.0.1"));
    }

    [Fact]
    public void The_lockout_ends_after_its_duration()
    {
        var limiter = new AttemptLimiter(_clock, _options);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            limiter.RecordFailure("login", "10.0.0.1");
        }

        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(TimeSpan.FromMinutes(1), limiter.GetLockout("login", "10.0.0.1"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(limiter.GetLockout("login", "10.0.0.1"));

        // After the lockout the caller starts with a clean counter.
        limiter.RecordFailure("login", "10.0.0.1");
        Assert.Null(limiter.GetLockout("login", "10.0.0.1"));
    }

    [Fact]
    public void Callers_and_scopes_are_counted_separately()
    {
        var limiter = new AttemptLimiter(_clock, _options);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            limiter.RecordFailure("login", "10.0.0.1");
        }

        Assert.NotNull(limiter.GetLockout("login", "10.0.0.1"));
        Assert.Null(limiter.GetLockout("login", "10.0.0.2"));
        Assert.Null(limiter.GetLockout("setup", "10.0.0.1"));
    }

    [Fact]
    public void A_success_clears_the_failures_of_the_caller()
    {
        var limiter = new AttemptLimiter(_clock, _options);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            limiter.RecordFailure("login", "10.0.0.1");
        }

        limiter.Reset("login", "10.0.0.1");
        limiter.RecordFailure("login", "10.0.0.1");

        Assert.Null(limiter.GetLockout("login", "10.0.0.1"));
    }

    [Fact]
    public void Failures_older_than_the_window_do_not_count()
    {
        var limiter = new AttemptLimiter(_clock, _options);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            limiter.RecordFailure("login", "10.0.0.1");
        }

        _clock.Advance(TimeSpan.FromMinutes(16));
        limiter.RecordFailure("login", "10.0.0.1");

        Assert.Null(limiter.GetLockout("login", "10.0.0.1"));
    }

    [Fact]
    public void Failures_spread_over_many_addresses_lock_everybody_out()
    {
        var options = new AuthOptions { MaxFailedAttempts = 5, MaxFailedAttemptsOverall = 8 };
        var limiter = new AttemptLimiter(_clock, options);

        for (int caller = 0; caller < 8; caller++)
        {
            limiter.RecordFailure("login", "10.0.0." + caller);
        }

        Assert.NotNull(limiter.GetLockout("login", "192.168.1.1"));
        Assert.Null(limiter.GetLockout("setup", "192.168.1.1"));
    }
}

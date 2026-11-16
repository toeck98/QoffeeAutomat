using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Auth;

/// <summary>Sessions of the management API (T212): idle timeout, lifetime, revocation.</summary>
[Trait("Category", "Auth")]
public sealed class SessionStoreTests
{
    private readonly SimulatedClock _clock = new();
    private readonly AuthOptions _options = new();

    [Fact]
    public void A_new_session_is_valid_and_has_a_long_random_id()
    {
        var sessions = new SessionStore(_clock, _options);

        string id = sessions.Create();

        Assert.Equal(43, id.Length);
        Assert.True(sessions.TryValidate(id));
        Assert.NotEqual(id, sessions.Create());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void An_unknown_session_is_not_valid(string? id)
    {
        var sessions = new SessionStore(_clock, _options);
        sessions.Create();

        Assert.False(sessions.TryValidate(id));
    }

    [Fact]
    public void A_session_ends_after_the_idle_timeout_but_activity_keeps_it_alive()
    {
        var sessions = new SessionStore(_clock, _options);
        string id = sessions.Create();

        _clock.Advance(TimeSpan.FromHours(7));
        Assert.True(sessions.TryValidate(id));
        _clock.Advance(TimeSpan.FromHours(7));
        Assert.True(sessions.TryValidate(id));

        _clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1));
        Assert.False(sessions.TryValidate(id));
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public void A_session_ends_after_its_lifetime_however_active_it_is()
    {
        var sessions = new SessionStore(_clock, _options);
        string id = sessions.Create();

        // Active every six hours for exactly seven days: still valid (the idle timeout is eight hours).
        for (int step = 0; step < 28; step++)
        {
            _clock.Advance(TimeSpan.FromHours(6));
            Assert.True(sessions.TryValidate(id));
        }

        _clock.Advance(TimeSpan.FromHours(6));
        Assert.False(sessions.TryValidate(id));
    }

    [Fact]
    public void Revoking_ends_one_session_and_revoke_all_ends_every_one()
    {
        var sessions = new SessionStore(_clock, _options);
        string first = sessions.Create();
        string second = sessions.Create();

        sessions.Revoke(first);
        Assert.False(sessions.TryValidate(first));
        Assert.True(sessions.TryValidate(second));

        sessions.RevokeAll();
        Assert.False(sessions.TryValidate(second));
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public void Revoke_all_except_keeps_one_session()
    {
        var sessions = new SessionStore(_clock, _options);
        string keep = sessions.Create();
        string other = sessions.Create();
        string third = sessions.Create();

        int ended = sessions.RevokeAllExcept(keep);

        Assert.Equal(2, ended);
        Assert.True(sessions.TryValidate(keep));
        Assert.False(sessions.TryValidate(other));
        Assert.False(sessions.TryValidate(third));
    }

    [Fact]
    public void The_number_of_sessions_is_bounded_and_the_oldest_goes_first()
    {
        var sessions = new SessionStore(_clock, new AuthOptions { MaxSessions = 3 });
        string oldest = sessions.Create();
        _clock.Advance(TimeSpan.FromMinutes(1));
        string second = sessions.Create();
        _clock.Advance(TimeSpan.FromMinutes(1));
        string third = sessions.Create();
        _clock.Advance(TimeSpan.FromMinutes(1));

        string newest = sessions.Create();

        Assert.Equal(3, sessions.Count);
        Assert.False(sessions.TryValidate(oldest));
        Assert.True(sessions.TryValidate(second));
        Assert.True(sessions.TryValidate(third));
        Assert.True(sessions.TryValidate(newest));
    }
}

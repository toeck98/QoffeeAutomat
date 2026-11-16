using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Platform;

[Trait("Category", "Platform")]
public sealed class SimulatedClockTests
{
    [Fact]
    public void Time_only_moves_when_the_clock_is_advanced()
    {
        var clock = new SimulatedClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)));

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 1, 4, 5, TimeSpan.Zero), clock.GetUtcNow());
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow().Offset);

        clock.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 2, 34, 5, TimeSpan.Zero), clock.GetUtcNow());
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void A_one_shot_timer_fires_when_its_due_time_has_passed()
    {
        var clock = new SimulatedClock();
        int fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, fired);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_periodic_timer_fires_once_per_period_each_at_its_own_time()
    {
        var clock = new SimulatedClock(DateTimeOffset.UnixEpoch);
        var seen = new List<DateTimeOffset>();
        using ITimer timer = clock.CreateTimer(
            _ => seen.Add(clock.GetUtcNow()),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(17));

        DateTimeOffset[] expected =
        [
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            DateTimeOffset.UnixEpoch.AddSeconds(15),
        ];
        Assert.Equal(expected, seen);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(17), clock.GetUtcNow());
    }

    [Fact]
    public void Timers_fire_in_order_of_their_due_time()
    {
        var clock = new SimulatedClock();
        var order = new List<string>();
        using ITimer late = clock.CreateTimer(_ => order.Add("late"), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        using ITimer early = clock.CreateTimer(_ => order.Add("early"), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        using ITimer middle = clock.CreateTimer(_ => order.Add("middle"), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(30));

        string[] expected = ["early", "middle", "late"];
        Assert.Equal(expected, order);
    }

    [Fact]
    public void A_disposed_or_changed_timer_follows_its_new_schedule()
    {
        var clock = new SimulatedClock();
        int disposedFired = 0;
        int changedFired = 0;
        ITimer disposed = clock.CreateTimer(_ => disposedFired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        using ITimer changed = clock.CreateTimer(_ => changedFired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        disposed.Dispose();
        Assert.False(disposed.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));
        Assert.True(changed.Change(TimeSpan.FromSeconds(100), Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal(0, disposedFired);
        Assert.Equal(0, changedFired);

        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(1, changedFired);
    }

    [Fact]
    public void A_timer_can_be_stopped_with_an_infinite_due_time()
    {
        var clock = new SimulatedClock();
        int fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(2));
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task Task_Delay_with_the_clock_completes_when_the_clock_is_advanced()
    {
        var clock = new SimulatedClock();
        Task delay = Task.Delay(TimeSpan.FromMinutes(30), clock);

        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));

        await delay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public void Setting_the_clock_forward_runs_timers_and_setting_it_back_does_not()
    {
        var clock = new SimulatedClock(DateTimeOffset.UnixEpoch);
        int fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddSeconds(20));
        Assert.Equal(1, fired);

        clock.SetUtcNow(DateTimeOffset.UnixEpoch);
        Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
        Assert.Equal(1, fired);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>Suspend and shutdown notifications through the delay lock and the signals of logind (T802).</summary>
[Trait("Category", "Service")]
public sealed class LinuxPowerEventsTests
{
    private static readonly LogindSignal SleepStarts = new(LogindSignalKind.PrepareForSleep, Starting: true);
    private static readonly LogindSignal SleepEnds = new(LogindSignalKind.PrepareForSleep, Starting: false);
    private static readonly LogindSignal ShutdownStarts = new(LogindSignalKind.PrepareForShutdown, Starting: true);
    private static readonly LogindSignal ShutdownCancelled = new(LogindSignalKind.PrepareForShutdown, Starting: false);

    [Theory]
    [InlineData("""{"type":"signal","endian":"l","flags":1,"version":1,"cookie":7,"sender":":1.2","path":"/org/freedesktop/login1","interface":"org.freedesktop.login1.Manager","member":"PrepareForSleep","payload":{"type":"b","data":[true]}}""", LogindSignalKind.PrepareForSleep, true)]
    [InlineData("""{"member":"PrepareForSleep","payload":{"type":"b","data":[false]}}""", LogindSignalKind.PrepareForSleep, false)]
    [InlineData("""{"member":"PrepareForShutdown","payload":{"type":"b","data":[true]}}""", LogindSignalKind.PrepareForShutdown, true)]
    [InlineData("""{"member":"PrepareForShutdown","payload":{"type":"b","data":[false]}}""", LogindSignalKind.PrepareForShutdown, false)]
    public void A_line_of_busctl_json_becomes_a_signal(string line, LogindSignalKind kind, bool starting)
    {
        Assert.Equal(new LogindSignal(kind, starting), BusctlLogindSignalSource.ParseLine(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Monitoring bus message stream.")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"member":"SessionNew","payload":{"type":"so","data":["1","/org/freedesktop/login1/session/_31"]}}""")]
    [InlineData("""{"member":"PrepareForSleep","payload":{"type":"s","data":["true"]}}""")]
    [InlineData("""{"member":"PrepareForSleep","payload":{"type":"b","data":[]}}""")]
    [InlineData("""{"member":"PrepareForSleep"}""")]
    [InlineData("""{"member":5,"payload":{"data":[true]}}""")]
    public void Anything_else_is_not_a_power_signal(string line)
    {
        Assert.Null(BusctlLogindSignalSource.ParseLine(line));
    }

    [Fact]
    public void Busctl_is_asked_for_exactly_the_two_signals_as_separate_arguments()
    {
        IReadOnlyList<string> arguments = BusctlLogindSignalSource.BuildArguments();

        Assert.Equal(
            [
                "--system",
                "--json=short",
                "--match=type='signal',interface='org.freedesktop.login1.Manager',member='PrepareForSleep'",
                "--match=type='signal',interface='org.freedesktop.login1.Manager',member='PrepareForShutdown'",
                "monitor",
            ],
            arguments);
    }

    [Fact]
    public void The_lock_is_a_delay_lock_for_sleep_and_shutdown_held_by_a_program_that_ends_with_its_input()
    {
        IReadOnlyList<string> arguments = SystemdInhibitor.BuildArguments("because; rm -rf /");

        Assert.Equal(["--what=sleep:shutdown", "--who=QoffeeAutomat", "--why=because; rm -rf /", "--mode=delay", "cat"], arguments);
    }

    [Fact]
    public async Task Sleep_is_announced_to_the_listeners_while_the_lock_is_still_held_and_the_lock_is_released_afterwards()
    {
        var inhibitor = new FakeInhibitor();
        using var events = Create(inhibitor, new FakeSignalSource());
        await events.StartAsync(CancellationToken.None);
        FakeLock held = Assert.Single(inhibitor.Locks);
        var seen = new List<(PowerEventKind Kind, bool LockReleased)>();
        events.PowerChanged += (_, e) => seen.Add((e.Kind, held.Released));

        await events.HandleAsync(SleepStarts);

        Assert.Equal([(PowerEventKind.Sleep, false)], seen);
        Assert.True(held.Released);
        Assert.False(events.HoldsLock);
        await events.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Resume_takes_the_lock_again_and_is_announced()
    {
        var inhibitor = new FakeInhibitor();
        using var events = Create(inhibitor, new FakeSignalSource());
        await events.StartAsync(CancellationToken.None);
        var kinds = new List<PowerEventKind>();
        events.PowerChanged += (_, e) => kinds.Add(e.Kind);

        await events.HandleAsync(SleepStarts);
        await events.HandleAsync(SleepEnds);

        Assert.Equal([PowerEventKind.Sleep, PowerEventKind.Resume], kinds);
        Assert.Equal(["lock acquired", "lock released", "lock acquired"], inhibitor.Log);
        Assert.True(events.HoldsLock);
        await events.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Shutdown_is_announced_before_the_lock_goes_and_a_cancelled_shutdown_takes_the_lock_again_without_an_event()
    {
        var inhibitor = new FakeInhibitor();
        using var events = Create(inhibitor, new FakeSignalSource());
        await events.StartAsync(CancellationToken.None);
        var kinds = new List<PowerEventKind>();
        events.PowerChanged += (_, e) => kinds.Add(e.Kind);

        await events.HandleAsync(ShutdownStarts);
        Assert.Equal([PowerEventKind.Shutdown], kinds);
        Assert.False(events.HoldsLock);

        await events.HandleAsync(ShutdownCancelled);
        Assert.Equal([PowerEventKind.Shutdown], kinds);
        Assert.True(events.HoldsLock);
        await events.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_hint_turns_the_next_sleep_into_hibernate_and_the_next_shutdown_into_restart_once()
    {
        var inhibitor = new FakeInhibitor();
        using var events = Create(inhibitor, new FakeSignalSource());
        var kinds = new List<PowerEventKind>();
        events.PowerChanged += (_, e) => kinds.Add(e.Kind);

        events.ExpectHibernate();
        await events.HandleAsync(SleepStarts);
        await events.HandleAsync(SleepStarts);
        events.ExpectRestart();
        await events.HandleAsync(ShutdownStarts);
        await events.HandleAsync(ShutdownStarts);

        Assert.Equal([PowerEventKind.Hibernate, PowerEventKind.Sleep, PowerEventKind.Restart, PowerEventKind.Shutdown], kinds);
    }

    [Fact]
    public async Task A_hint_does_not_apply_to_the_other_kind_of_event_and_expires()
    {
        var time = new ManualTime();
        var inhibitor = new FakeInhibitor();
        using var events = Create(inhibitor, new FakeSignalSource(), time);
        var kinds = new List<PowerEventKind>();
        events.PowerChanged += (_, e) => kinds.Add(e.Kind);

        events.ExpectRestart();
        await events.HandleAsync(SleepStarts);
        events.ExpectHibernate();
        time.Now += TimeSpan.FromMinutes(3);
        await events.HandleAsync(SleepStarts);

        Assert.Equal([PowerEventKind.Sleep, PowerEventKind.Sleep], kinds);
    }

    [Fact]
    public async Task A_listener_that_fails_does_not_keep_the_others_from_being_told()
    {
        var inhibitor = new FakeInhibitor();
        using var events = Create(inhibitor, new FakeSignalSource());
        var kinds = new List<PowerEventKind>();
        events.PowerChanged += (_, _) => throw new InvalidOperationException("a listener that fails");
        events.PowerChanged += (_, e) => kinds.Add(e.Kind);

        await events.HandleAsync(SleepStarts);

        Assert.Equal([PowerEventKind.Sleep], kinds);
    }

    [Fact]
    public async Task Without_a_lock_the_events_are_still_announced()
    {
        var inhibitor = new FakeInhibitor { Refuse = true };
        using var events = Create(inhibitor, new FakeSignalSource());
        await events.StartAsync(CancellationToken.None);
        var kinds = new List<PowerEventKind>();
        events.PowerChanged += (_, e) => kinds.Add(e.Kind);

        Assert.False(events.HoldsLock);
        await events.HandleAsync(ShutdownStarts);

        Assert.Equal([PowerEventKind.Shutdown], kinds);
        await events.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_signals_of_logind_reach_the_listeners_and_stopping_releases_the_lock()
    {
        var inhibitor = new FakeInhibitor();
        var signals = new FakeSignalSource();
        using var events = Create(inhibitor, signals);
        var told = new TaskCompletionSource<PowerEventKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        events.PowerChanged += (_, e) => told.TrySetResult(e.Kind);
        await events.StartAsync(CancellationToken.None);

        signals.Send(SleepStarts);

        Assert.Equal(PowerEventKind.Sleep, await told.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // The listeners run first; the lock is released right after them.
        await WaitUntilAsync(() => inhibitor.Log.Contains("lock released"));
        Assert.Equal(["lock acquired", "lock released"], inhibitor.Log);

        signals.Send(SleepEnds);
        await WaitUntilAsync(() => events.HoldsLock);
        await events.StopAsync(CancellationToken.None);

        Assert.False(events.HoldsLock);
        Assert.All(inhibitor.Locks, held => Assert.True(held.Released));
    }

    [Fact]
    public async Task A_connection_to_logind_that_ends_is_opened_again_later()
    {
        var inhibitor = new FakeInhibitor();
        var signals = new FakeSignalSource();
        var options = new LinuxPowerEventsOptions { ReconnectDelay = TimeSpan.FromMilliseconds(50) };
        using var events = new LinuxPowerEvents(signals, inhibitor, NullLogger.Instance, options: options);
        await events.StartAsync(CancellationToken.None);

        signals.End();

        await WaitUntilAsync(() => signals.Watches >= 2);
        await events.StopAsync(CancellationToken.None);
    }

    private static LinuxPowerEvents Create(FakeInhibitor inhibitor, FakeSignalSource signals, TimeProvider? time = null) =>
        new(signals, inhibitor, NullLogger.Instance, time, new LinuxPowerEventsOptions { ReconnectDelay = TimeSpan.FromHours(1) });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10);
        }
    }
}

using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Modes;

namespace QoffeeAutomat.Core.Tests.Modes;

/// <summary>
/// The three <c>onBoot</c> behaviours (PLAN.md S06, T717), decided by the boot id: the PC was at mode b when the agent last ran in boot
/// <c>boot-1</c>; then the agent starts again with a new boot id (the PC was restarted) or with the same one (only the agent restarted).
/// </summary>
[Trait("Category", "Modes")]
public sealed class ModeBootTests
{
    private static async Task<ModeHarness> StartedAtBAsync(string onBoot)
    {
        ModeHarness h = await ModeHarness.CreateAsync("boot-1");
        await h.SeedAsync(ModeDocs.Standard(onBoot));
        await h.SwitchAsync("room", "b");
        h.TakeChangedEvents();
        return h;
    }

    [Fact]
    public async Task Keep_remembers_the_last_mode_when_the_boot_id_changed()
    {
        await using ModeHarness h = await StartedAtBAsync(ModeBootBehaviors.Keep);
        int calls = h.Runner.Calls.Count;

        h.Restart("boot-2");
        IReadOnlyList<string> pending = await h.Engine.ReconcileBootAsync(CancellationToken.None);

        Assert.Empty(pending);
        Assert.Equal("b", h.Engine.GetCurrent("room"));
        Assert.Equal(calls, h.Runner.Calls.Count);
        Assert.Empty(h.TakeChangedEvents());
    }

    [Fact]
    public async Task Reset_makes_the_default_mode_current_without_running_steps_when_the_boot_id_changed()
    {
        await using ModeHarness h = await StartedAtBAsync(ModeBootBehaviors.Reset);
        int calls = h.Runner.Calls.Count;

        h.Restart("boot-2");
        IReadOnlyList<string> pending = await h.Engine.ReconcileBootAsync(CancellationToken.None);

        Assert.Empty(pending);
        Assert.Equal("a", h.Engine.GetCurrent("room"));
        Assert.Equal(calls, h.Runner.Calls.Count);
    }

    [Fact]
    public async Task Apply_default_makes_the_state_unknown_and_waits_for_the_session_before_running_the_enter_steps()
    {
        await using ModeHarness h = await StartedAtBAsync(ModeBootBehaviors.ApplyDefault);
        int calls = h.Runner.Calls.Count;
        h.Restart("boot-2");
        (CommandEngine commands, var platform) = h.CreateCommandEngine();
        var applier = new ModeBootApplier(h.Engine, commands, platform.Session);

        IReadOnlyList<string> pending = await h.Engine.ReconcileBootAsync(CancellationToken.None);

        Assert.Equal(["room"], pending);
        Assert.Null(h.Engine.GetCurrent("room"));
        Assert.True(h.Engine.IsBootApplyPending("room"));

        Task applying = applier.ApplyAsync(pending, CancellationToken.None);
        await Task.Delay(150);

        // No session yet: nothing ran and the state is still unknown.
        Assert.False(applying.IsCompleted);
        Assert.Equal(calls, h.Runner.Calls.Count);
        Assert.Null(h.Engine.GetCurrent("room"));

        platform.Session.SetReady(true);
        await applying.WaitAsync(TimeSpan.FromSeconds(10));

        // The state was unknown, so only the enter steps of the default mode ran.
        Assert.Equal(["a_enter"], h.Runner.Calls.Skip(calls));
        Assert.Equal("a", h.Engine.GetCurrent("room"));
        Assert.False(h.Engine.IsBootApplyPending("room"));
    }

    [Fact]
    public async Task Apply_default_runs_at_once_when_the_session_is_already_ready()
    {
        await using ModeHarness h = await StartedAtBAsync(ModeBootBehaviors.ApplyDefault);
        h.Restart("boot-2");
        (CommandEngine commands, var platform) = h.CreateCommandEngine();
        platform.Session.SetReady(true);
        var applier = new ModeBootApplier(h.Engine, commands, platform.Session);

        IReadOnlyList<string> pending = await h.Engine.ReconcileBootAsync(CancellationToken.None);
        await applier.ApplyAsync(pending, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("a", h.Engine.GetCurrent("room"));
    }

    [Fact]
    public async Task Apply_default_leaves_a_group_alone_that_was_switched_by_hand_while_waiting()
    {
        await using ModeHarness h = await StartedAtBAsync(ModeBootBehaviors.ApplyDefault);
        h.Restart("boot-2");
        (CommandEngine commands, var platform) = h.CreateCommandEngine();
        var applier = new ModeBootApplier(h.Engine, commands, platform.Session);
        IReadOnlyList<string> pending = await h.Engine.ReconcileBootAsync(CancellationToken.None);
        Task applying = applier.ApplyAsync(pending, CancellationToken.None);

        await h.SwitchAsync("room", "b");
        platform.Session.SetReady(true);
        await applying.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("b", h.Engine.GetCurrent("room"));
        Assert.DoesNotContain("a_enter", h.Runner.Calls);
    }

    [Fact]
    public async Task Apply_default_is_applied_again_when_the_agent_restarts_before_the_session_was_ready()
    {
        await using ModeHarness h = await StartedAtBAsync(ModeBootBehaviors.ApplyDefault);
        h.Restart("boot-2");
        await h.Engine.ReconcileBootAsync(CancellationToken.None);

        h.Restart("boot-2");
        IReadOnlyList<string> pending = await h.Engine.ReconcileBootAsync(CancellationToken.None);

        Assert.Equal(["room"], pending);
        Assert.Null(h.Engine.GetCurrent("room"));
    }

    [Theory]
    [InlineData(ModeBootBehaviors.Keep)]
    [InlineData(ModeBootBehaviors.Reset)]
    [InlineData(ModeBootBehaviors.ApplyDefault)]
    public async Task Nothing_happens_on_an_agent_restart_with_the_same_boot_id(string onBoot)
    {
        await using ModeHarness h = await StartedAtBAsync(onBoot);
        int calls = h.Runner.Calls.Count;

        h.Restart("boot-1");
        IReadOnlyList<string> pending = await h.Engine.ReconcileBootAsync(CancellationToken.None);

        Assert.Empty(pending);
        Assert.Equal("b", h.Engine.GetCurrent("room"));
        Assert.Equal(calls, h.Runner.Calls.Count);
        Assert.Empty(h.TakeChangedEvents());
    }

    [Fact]
    public async Task The_current_mode_survives_an_agent_restart()
    {
        await using ModeHarness h = await StartedAtBAsync(ModeBootBehaviors.Keep);

        h.Restart("boot-1");

        Assert.Equal("b", h.Engine.GetCurrent("room"));
        Assert.NotNull(h.Engine.GetState("room").ChangedAt);
    }
}

using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Tests.Commands;

/// <summary>The command registry: built from the config store and updated on change events.</summary>
[Trait("Category", "Commands")]
public sealed class CommandRegistryTests
{
    [Fact]
    public async Task The_registry_lists_the_saved_commands_ordered_by_id()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        Assert.Empty(h.Registry.Commands);

        await h.SaveAsync(Cmd.Gated("b"), Cmd.Gated("a"));

        Assert.Equal(["a", "b"], h.Registry.Commands.Select(command => command.Id));
        Assert.NotNull(h.Registry.TryGet("a"));
        Assert.Null(h.Registry.TryGet("zzz"));
        Assert.Null(h.Registry.TryGet(string.Empty));
    }

    [Fact]
    public async Task A_run_right_after_a_save_sees_the_new_document()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c") with { Name = "First" });
        Assert.Equal("First", h.Registry.TryGet("c")!.Name);

        await h.SaveAsync(Cmd.Gated("c") with { Name = "Second" });

        Assert.Equal("Second", h.Registry.TryGet("c")!.Name);
    }

    [Fact]
    public async Task A_deleted_command_leaves_the_registry()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("a"), Cmd.Gated("b"));

        var result = await h.Config.Local.DeleteAsync<CommandDocument>("a", null, CancellationToken.None);

        Assert.True(result.IsApplied);
        Assert.Equal(["b"], h.Registry.Commands.Select(command => command.Id));
    }

    [Fact]
    public async Task Changed_is_raised_after_a_command_was_added_edited_or_removed()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Registry.Start();
        var changes = new SemaphoreSlim(0);
        h.Registry.Changed += (_, _) => changes.Release();

        await h.SaveAsync(Cmd.Gated("a"));
        Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10)), "No notification after adding a command.");

        await h.SaveAsync(Cmd.Gated("a") with { Enabled = false });
        Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10)), "No notification after editing a command.");

        await h.Config.Local.DeleteAsync<CommandDocument>("a", null, CancellationToken.None);
        Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10)), "No notification after removing a command.");
    }

    [Fact]
    public async Task Changes_that_do_not_concern_commands_are_not_announced()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Registry.Start();
        int raised = 0;
        h.Registry.Changed += (_, _) => Interlocked.Increment(ref raised);

        await h.Config.Local.SaveScriptAsync("tool.ps1", "Write-Output 1", null, CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_stop_later_notifications()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Registry.Start();
        var seen = new SemaphoreSlim(0);
        int calls = 0;
        h.Registry.Changed += (_, _) =>
        {
            seen.Release();
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("faulty subscriber");
            }
        };

        await h.SaveAsync(Cmd.Gated("a"));
        Assert.True(await seen.WaitAsync(TimeSpan.FromSeconds(10)));
        await h.SaveAsync(Cmd.Gated("b"));

        Assert.True(await seen.WaitAsync(TimeSpan.FromSeconds(10)), "The second change was not announced.");
    }
}

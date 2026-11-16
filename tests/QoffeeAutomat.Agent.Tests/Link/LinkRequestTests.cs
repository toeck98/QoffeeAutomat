using System.Text.Json;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Tests.Link;

/// <summary>
/// <c>run</c>, <c>manifest_changed</c> and <c>event</c> of the QoffeeLink server (T210, PLAN.md S07), through the
/// whole agent with the simulated platform. A recording launcher stands in for the operating system, so a
/// test can tell whether a process would have been started.
/// </summary>
[Trait("Category", "Link")]
public sealed class LinkRequestTests
{
    private const string RunLevel3 = "\"command\":\"set_level\",\"vars\":{\"Level\":3},\"context\":{\"user_id\":\"user-1\"}";

    [Fact]
    public async Task A_run_that_waits_returns_the_exit_code_and_the_output_and_binds_the_variables_as_separate_arguments()
    {
        await using var host = new LinkTestHost();
        await host.AddCommandAsync();
        await using LinkTestClient client = await host.ConnectAsync();

        JsonElement result = await client.RequestAsync(MessageTypes.Run, RunLevel3);

        Assert.True(result.GetProperty("ok").GetBoolean());
        JsonElement data = result.GetProperty("data");
        Assert.False(string.IsNullOrEmpty(data.GetProperty("run_id").GetString()));
        Assert.Equal(0, data.GetProperty("exit_code").GetInt32());
        Assert.Contains("--level 3", data.GetProperty("stdout").GetString(), StringComparison.Ordinal);
        Assert.False(data.GetProperty("timed_out").GetBoolean());
        Assert.True(data.GetProperty("duration_ms").GetInt64() >= 0);
        ProcessLaunchRequest launch = Assert.Single(host.Launcher.Launches);
        Assert.Equal("level-tool", launch.FileName);
        Assert.Equal(["--level", "3"], launch.Arguments.ToArray());
    }

    [Fact]
    public async Task A_run_that_does_not_wait_returns_the_run_id_at_once_and_the_end_comes_as_an_event()
    {
        await using var host = new LinkTestHost();
        await host.AddCommandAsync();
        await using LinkTestClient client = await host.ConnectAsync();
        host.Launcher.Hold();
        try
        {
            JsonElement accepted = await client.RequestAsync(MessageTypes.Run, RunLevel3 + ",\"wait\":false");

            Assert.True(accepted.GetProperty("ok").GetBoolean());
            JsonElement data = accepted.GetProperty("data");
            string runId = data.GetProperty("run_id").GetString()!;
            Assert.False(data.TryGetProperty("exit_code", out _));
            await TestWait.UntilAsync(() => host.Launcher.Launches.Length == 1, "the command to reach the launcher");
            Assert.DoesNotContain(client.FramesOf(MessageTypes.Event), frame => frame.GetProperty("type").GetString() == EventTypes.CommandFinished);

            host.Launcher.Release();
            JsonElement finished = await client.WaitForAsync(
                frame => LinkTestClient.TypeOf(frame) == MessageTypes.Event && frame.GetProperty("type").GetString() == EventTypes.CommandFinished,
                "the command_finished event");
            JsonElement eventData = finished.GetProperty("data");
            Assert.Equal(runId, eventData.GetProperty("run_id").GetString());
            Assert.Equal(LinkTestHost.CommandId, eventData.GetProperty("command").GetString());
            Assert.Equal("ha", eventData.GetProperty("source").GetString());
        }
        finally
        {
            host.Launcher.Release();
        }
    }

    [Theory]
    [InlineData("\"command\":\"set_level\",\"vars\":{\"Level\":99}")]
    [InlineData("\"command\":\"set_level\",\"vars\":{\"Level\":\"high\"}")]
    [InlineData("\"command\":\"set_level\",\"vars\":{}")]
    [InlineData("\"command\":\"set_level\"")]
    [InlineData("\"command\":\"set_level\",\"vars\":{\"Level\":99},\"wait\":false")]
    public async Task A_run_with_invalid_variables_returns_invalid_vars_and_does_not_start_a_process(string members)
    {
        await using var host = new LinkTestHost();
        await host.AddCommandAsync();
        await using LinkTestClient client = await host.ConnectAsync();

        JsonElement result = await client.RequestAsync(MessageTypes.Run, members);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.InvalidVars, LinkTestClient.ErrorCodeOf(result));
        Assert.Empty(host.Launcher.Launches);
        Assert.True(client.IsOpen);
    }

    [Fact]
    public async Task A_run_of_a_command_that_does_not_exist_returns_unknown_command()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        JsonElement result = await client.RequestAsync(MessageTypes.Run, "\"command\":\"no_such_command\"");

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.UnknownCommand, LinkTestClient.ErrorCodeOf(result));
        Assert.Empty(host.Launcher.Launches);
    }

    [Fact]
    public async Task A_long_run_does_not_block_other_requests_of_the_connection()
    {
        await using var host = new LinkTestHost();
        await host.AddCommandAsync();
        await using LinkTestClient client = await host.ConnectAsync();
        host.Launcher.Hold();
        try
        {
            long slow = await client.SendRequestAsync(MessageTypes.Run, RunLevel3);
            await TestWait.UntilAsync(() => host.Launcher.Launches.Length == 1, "the command to reach the launcher");

            JsonElement manifest = await client.RequestAsync(MessageTypes.GetManifest);

            Assert.True(manifest.GetProperty("ok").GetBoolean());
            Assert.DoesNotContain(client.Frames, frame => LinkTestClient.IsResult(frame, slow));
            host.Launcher.Release();
            JsonElement done = await client.WaitForResultAsync(slow);
            Assert.True(done.GetProperty("ok").GetBoolean());
        }
        finally
        {
            host.Launcher.Release();
        }
    }

    [Fact]
    public async Task A_change_of_the_commands_is_announced_with_manifest_changed_and_get_manifest_returns_the_new_manifest()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();
        string before = client.Frames[0].GetProperty("manifest_rev").GetString()!;

        string after = await host.AddCommandAsync();

        JsonElement changed = await client.WaitForTypeAsync(MessageTypes.ManifestChanged);
        Assert.NotEqual(before, after);
        Assert.Equal(after, changed.GetProperty("rev").GetString());
        JsonElement manifest = (await client.RequestAsync(MessageTypes.GetManifest)).GetProperty("data");
        Assert.Empty(LinkSchemas.Validate(manifest, LinkSchemas.Manifest));
        Assert.Equal(after, manifest.GetProperty("rev").GetString());
        Assert.Contains(manifest.GetProperty("commands").EnumerateArray(), command => command.GetProperty("id").GetString() == LinkTestHost.CommandId);
    }

    [Fact]
    public async Task Every_connected_client_is_told_about_a_changed_manifest_once()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient first = await host.ConnectAsync();
        await using LinkTestClient second = await host.ConnectAsync();

        string after = await host.AddCommandAsync();

        JsonElement forFirst = await first.WaitForTypeAsync(MessageTypes.ManifestChanged);
        JsonElement forSecond = await second.WaitForTypeAsync(MessageTypes.ManifestChanged);
        Assert.Equal(after, forFirst.GetProperty("rev").GetString());
        Assert.Equal(after, forSecond.GetProperty("rev").GetString());
        await Task.Delay(200);
        Assert.Single(first.FramesOf(MessageTypes.ManifestChanged));
        Assert.Single(second.FramesOf(MessageTypes.ManifestChanged));
    }

    [Fact]
    public async Task An_event_of_the_bus_arrives_as_an_event_frame_and_events_outside_the_catalog_do_not()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();
        await client.RequestAsync(MessageTypes.GetManifest);

        // command_started is an in-process notification; the link must not forward it.
        await host.Events.PublishAsync(EventTypes.CommandStarted, EventData.Create(("command", "x")));
        await host.Events.PublishAsync(EventTypes.Custom, EventData.Create(("name", "doorbell")));

        JsonElement frame = await client.WaitForAsync(
            candidate => LinkTestClient.TypeOf(candidate) == MessageTypes.Event && candidate.GetProperty("type").GetString() == EventTypes.Custom,
            "the custom event");
        Assert.True(frame.GetProperty("ts").GetInt64() > 0);
        Assert.Equal("doorbell", frame.GetProperty("data").GetProperty("name").GetString());
        Assert.DoesNotContain(
            client.FramesOf(MessageTypes.Event),
            candidate => candidate.GetProperty("type").GetString() == EventTypes.CommandStarted);
    }

    [Fact]
    public async Task A_client_that_has_gone_no_longer_receives_events_and_the_others_still_do()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient staying = await host.ConnectAsync();
        LinkTestClient leaving = await host.ConnectAsync();
        int before = leaving.Count;
        await leaving.DisposeAsync();
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 1, "the leaving client to be removed");

        await host.Events.PublishAsync(EventTypes.Custom, EventData.Create(("name", "after-leaving")));

        await staying.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.Event && frame.GetProperty("data").GetProperty("name").GetString() == "after-leaving",
            "the event for the client that stayed");
        Assert.Equal(before, leaving.Count);
    }
}

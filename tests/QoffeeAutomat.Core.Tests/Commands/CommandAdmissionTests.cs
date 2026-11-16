using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Tests.Commands;

/// <summary>
/// The checks before a command starts, in the order of PLAN.md T207: lookup, enabled, pause, cooldown,
/// variable binding; concurrency is covered by <see cref="CommandConcurrencyTests"/>.
/// </summary>
[Trait("Category", "Commands")]
public sealed class CommandAdmissionTests
{
    private static ExecutionResult Ok(GateCall call) => new(0, "ok\n", string.Empty);

    [Fact]
    public async Task An_unknown_command_is_rejected_and_leaves_no_history()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();

        RunResult result = await h.RunAsync("does_not_exist");

        Assert.Equal(ErrorCodes.UnknownCommand, result.ErrorCode);
        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Empty(await h.History.QueryAsync(new RunQuery(), CancellationToken.None));
    }

    [Theory]
    [InlineData(RunSources.HomeAssistant)]
    [InlineData(RunSources.Ui)]
    [InlineData(RunSources.Hub)]
    [InlineData(RunSources.Cli)]
    public async Task A_disabled_command_is_rejected_from_every_entry_point(string source)
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c") with { Enabled = false });

        RunResult result = await h.RunFromAsync(source, "c");

        Assert.Equal(ErrorCodes.CommandDisabled, result.ErrorCode);
        Assert.False(result.Started);
        Assert.Empty(h.Gate!.Calls);
        RunRecord recorded = Assert.Single(await h.History.QueryAsync(new RunQuery(), CancellationToken.None));
        Assert.Equal(ErrorCodes.CommandDisabled, recorded.ErrorCode);
        Assert.Equal(source, recorded.Source);
        Assert.Null(recorded.ExitCode);
    }

    [Fact]
    public async Task A_disabled_command_is_also_rejected_when_the_run_is_started_without_waiting()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c") with { Enabled = false });

        RunHandle handle = await h.StartAsync("c");

        Assert.False(handle.IsAccepted);
        Assert.Equal(ErrorCodes.CommandDisabled, (await CommandHarness.Finished(handle)).ErrorCode);
        Assert.Empty(h.Engine.ActiveRuns);
    }

    [Fact]
    public async Task A_disabled_step_stops_the_sequence_with_command_disabled()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(
            Cmd.Gated("child") with { Enabled = false },
            Cmd.Sequence("seq", SequenceErrorModes.Stop, Cmd.Step("child")));

        RunResult result = await h.RunAsync("seq");

        Assert.Equal(ErrorCodes.CommandDisabled, result.ErrorCode);
        Assert.Empty(h.Gate.Calls);
        RunRecord child = Assert.Single(await h.History.QueryAsync(new RunQuery(Command: "child"), CancellationToken.None));
        Assert.Equal(ErrorCodes.CommandDisabled, child.ErrorCode);
        Assert.Equal(RunSources.Sequence, child.Source);
        Assert.Equal(result.RunId, child.ParentRunId);
    }

    [Fact]
    public async Task A_paused_agent_rejects_every_run_until_it_is_resumed()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c"), Cmd.Sequence("seq", SequenceErrorModes.Stop, Cmd.Step("c")));

        h.Pause.SetPaused(true);
        RunResult direct = await h.RunAsync("c");
        RunResult sequence = await h.RunAsync("seq");

        Assert.Equal(ErrorCodes.Paused, direct.ErrorCode);
        Assert.Equal(ErrorCodes.Paused, sequence.ErrorCode);
        Assert.Empty(h.Gate.Calls);

        h.Pause.SetPaused(false);
        Assert.True((await h.RunAsync("c")).Succeeded);
    }

    [Theory]
    [InlineData(RunSources.HomeAssistant)]
    [InlineData(RunSources.Ui)]
    [InlineData(RunSources.Hub)]
    [InlineData(RunSources.Cli)]
    [InlineData(RunSources.Sequence)]
    [InlineData(RunSources.Test)]
    public async Task The_pause_of_the_tray_icon_rejects_runs_of_every_source_with_the_error_paused(string source)
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c"));

        h.Pause.SetPaused(true);
        RunResult rejected = await h.RunFromAsync(source, "c");

        Assert.False(rejected.Started);
        Assert.Equal(ErrorCodes.Paused, rejected.ErrorCode);
        Assert.Empty(h.Gate.Calls);

        h.Pause.SetPaused(false);
        Assert.True((await h.RunFromAsync(source, "c")).Succeeded);
    }

    [Fact]
    public async Task A_run_that_is_already_running_is_not_affected_by_a_later_pause()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(Cmd.Gated("c"));
        RunHandle handle = await h.StartAsync("c");
        GateCall call = await h.Gate!.NextStartAsync();

        h.Pause.SetPaused(true);
        call.Finish();

        Assert.True((await CommandHarness.Finished(handle)).Succeeded);
    }

    [Fact]
    public async Task A_command_in_its_cooldown_is_rejected_and_the_rejection_does_not_extend_it()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c", cooldownSeconds: 10));

        Assert.True((await h.RunAsync("c")).Succeeded);

        h.Clock.Advance(TimeSpan.FromSeconds(4));
        RunResult tooSoon = await h.RunAsync("c");
        Assert.Equal(ErrorCodes.Cooldown, tooSoon.ErrorCode);
        Assert.Contains("6 seconds", tooSoon.ErrorMessage, StringComparison.Ordinal);

        // Ten seconds after the run that started, not after the rejected request.
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True((await h.RunAsync("c")).Succeeded);
        Assert.Equal(2, h.Gate.Calls.Count);
    }

    [Fact]
    public async Task The_cooldown_applies_per_command()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("slow", cooldownSeconds: 60), Cmd.Gated("other"));

        Assert.True((await h.RunAsync("slow")).Succeeded);

        Assert.True((await h.RunAsync("other")).Succeeded);
        Assert.Equal(ErrorCodes.Cooldown, (await h.RunAsync("slow")).ErrorCode);
    }

    [Fact]
    public async Task A_zero_cooldown_allows_back_to_back_runs()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c"));

        Assert.True((await h.RunAsync("c")).Succeeded);
        Assert.True((await h.RunAsync("c")).Succeeded);
    }

    [Fact]
    public async Task Missing_unknown_and_malformed_variables_are_rejected_without_echoing_values()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c", parameters: [Cmd.Text("Name", required: true), Cmd.Number("Count")]));

        RunResult missing = await h.RunAsync("c");
        RunResult unknown = await h.RunAsync("c", ("Name", "x"), ("Other", "secret-value-1"));
        RunResult malformed = await h.RunAsync("c", ("Name", "x"), ("Count", "secret-value-2"));

        Assert.All([missing, unknown, malformed], result =>
        {
            Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
            Assert.False(result.Started);
        });
        Assert.Contains("/vars/Name", missing.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("/vars/Other", unknown.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("/vars/Count", malformed.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", unknown.ErrorMessage + malformed.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Gate.Calls);
    }

    [Fact]
    public async Task Defaults_and_converted_values_reach_the_handler()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated(
            "c",
            parameters:
            [
                new ParameterDefinition("Name", ParameterTypes.Text, Default: CommandTestJson.Text("anonymous")),
                Cmd.Number("Count"),
            ]));

        await h.RunAsync("c", ("Count", 7));

        GateCall call = Assert.Single(h.Gate.Calls);
        Assert.Equal("anonymous", call.Variables["Name"]);
        Assert.Equal(7L, call.Variables["Count"]);
    }

    [Fact]
    public async Task The_checks_apply_in_the_documented_order()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        CommandDocument command = Cmd.Gated("c", cooldownSeconds: 10, parameters: [Cmd.Text("Name", required: true)]);

        // Disabled beats paused (and the missing variable).
        await h.SaveAsync(command with { Enabled = false });
        h.Pause.SetPaused(true);
        Assert.Equal(ErrorCodes.CommandDisabled, (await h.RunAsync("c")).ErrorCode);

        // Paused beats the missing variable.
        await h.SaveAsync(command);
        Assert.Equal(ErrorCodes.Paused, (await h.RunAsync("c")).ErrorCode);

        // A run that starts the cooldown, then: cooldown beats the missing variable.
        h.Pause.SetPaused(false);
        Assert.True((await h.RunAsync("c", ("Name", "x"))).Succeeded);
        Assert.Equal(ErrorCodes.Cooldown, (await h.RunAsync("c")).ErrorCode);

        // After the cooldown the variable is what is wrong.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ErrorCodes.InvalidVars, (await h.RunAsync("c")).ErrorCode);
    }

    [Fact]
    public async Task Invalid_variables_do_not_start_the_cooldown()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = Ok;
        await h.SaveAsync(Cmd.Gated("c", cooldownSeconds: 60, parameters: [Cmd.Text("Name", required: true)]));

        Assert.Equal(ErrorCodes.InvalidVars, (await h.RunAsync("c")).ErrorCode);

        Assert.True((await h.RunAsync("c", ("Name", "x"))).Succeeded);
    }

    [Fact]
    public async Task Run_requests_need_a_source()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Engine.StartAsync(new RunRequest("c", null, " ")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Engine.StartAsync(null!));
    }

    [Fact]
    public async Task The_result_converts_to_the_data_of_the_link_run_result()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        h.Gate!.AutoComplete = _ => new ExecutionResult(3, "out", "err");
        await h.SaveAsync(Cmd.Gated("c"));

        RunResult result = await h.RunAsync("c");
        RunResultData data = result.ToLinkData();

        Assert.Equal(result.RunId, data.RunId);
        Assert.Equal(3, data.ExitCode);
        Assert.Equal("out", data.Stdout);
        Assert.Equal("err", data.Stderr);
        Assert.False(data.TimedOut);
        Assert.Null(result.ToLinkData(includeOutput: false).Stdout);
    }
}

internal static class CommandTestJson
{
    public static System.Text.Json.JsonElement Text(string value)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }
}

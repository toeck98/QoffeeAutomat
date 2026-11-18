using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Commands;

/// <summary>
/// Command documents with an action type or a built-in action that the platform lacks are flagged as unsupported and kept, never deleted
/// (PLAN.md S17, T803).
/// </summary>
[Trait("Category", "Bash")]
public sealed class CommandPlatformSupportTests
{
    private static CommandDocument Document(CommandAction action) => new(
        DocumentSchema.CommandVersion,
        "flag_me",
        "Flag me",
        true,
        false,
        RunAsValues.System,
        action);

    [Theory]
    [InlineData("linux", ActionTypes.PowerShell, false)]
    [InlineData("LINUX", ActionTypes.PowerShell, false)]
    [InlineData("linux", ActionTypes.Bash, true)]
    [InlineData("linux", ActionTypes.Process, true)]
    [InlineData("linux", ActionTypes.Builtin, true)]
    [InlineData("linux", ActionTypes.Sequence, true)]
    [InlineData("windows", ActionTypes.Bash, false)]
    [InlineData("windows", ActionTypes.PowerShell, true)]
    [InlineData("windows", ActionTypes.Process, true)]
    [InlineData("simulated", ActionTypes.PowerShell, true)]
    [InlineData("simulated", ActionTypes.Bash, true)]
    [InlineData(null, ActionTypes.PowerShell, true)]
    [InlineData(null, ActionTypes.Bash, true)]
    public void The_rule_says_which_action_types_an_operating_system_can_run(string? os, string actionType, bool supported)
    {
        Assert.Equal(supported, PlatformActionSupport.IsActionTypeSupported(os, actionType));
    }

    [Fact]
    public void The_explanation_names_the_platform_and_says_that_the_command_is_kept()
    {
        string linux = PlatformActionSupport.Explain(PlatformActionSupport.LinuxOs, ActionTypes.PowerShell);
        string windows = PlatformActionSupport.Explain(PlatformActionSupport.WindowsOs, ActionTypes.Bash);

        Assert.Contains("Windows only", linux, StringComparison.Ordinal);
        Assert.Contains("bash or process", linux, StringComparison.Ordinal);
        Assert.Contains("is kept", linux, StringComparison.Ordinal);
        Assert.Contains("Linux only", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void A_powershell_command_on_linux_is_flagged_at_the_action_type()
    {
        var support = new CommandPlatformSupport(PlatformActionSupport.LinuxOs);
        CommandDocument command = Document(new PowerShellAction(PowerShellSources.Inline, Code: "Get-Date"));

        ValidationError error = Assert.Single(support.Check(command));

        Assert.Equal("/action/type", error.Path);
        Assert.Equal(ValidationCodes.Unsupported, error.Code);
        Assert.Contains("kept", error.Message, StringComparison.Ordinal);
        Assert.False(support.IsSupported(command));
    }

    [Fact]
    public void A_bash_command_on_linux_and_a_process_command_everywhere_are_supported()
    {
        var linux = new CommandPlatformSupport(PlatformActionSupport.LinuxOs);

        Assert.True(linux.IsSupported(Document(new BashAction(BashSources.Inline, Code: "echo"))));
        Assert.True(linux.IsSupported(Document(new ProcessAction("/bin/true"))));
        Assert.True(linux.IsSupported(Document(new SequenceAction([new SequenceStep(Command: "other")]))));
    }

    [Fact]
    public void The_unrestricted_policy_supports_everything()
    {
        CommandPlatformSupport support = CommandPlatformSupport.Unrestricted;

        Assert.Null(support.Os);
        Assert.True(support.IsSupported(Document(new PowerShellAction(PowerShellSources.Inline, Code: "Get-Date"))));
        Assert.True(support.IsSupported(Document(new BashAction(BashSources.Inline, Code: "echo"))));
        Assert.True(support.IsSupported(Document(new BuiltinAction("anything.at_all"))));
    }

    [Fact]
    public void A_builtin_action_that_the_platform_does_not_offer_is_flagged_at_its_name()
    {
        int asked = 0;
        var offered = new FixedBuiltins("power.shutdown", "session.lock");
        var support = new CommandPlatformSupport(
            PlatformActionSupport.LinuxOs,
            () =>
            {
                asked++;
                return offered;
            });

        ValidationError error = Assert.Single(support.Check(Document(new BuiltinAction("power.keep_awake"))));

        Assert.Equal("/action/name", error.Path);
        Assert.Equal(ValidationCodes.Unsupported, error.Code);
        Assert.Contains("power.keep_awake", error.Message, StringComparison.Ordinal);
        Assert.Empty(support.Check(Document(new BuiltinAction("power.shutdown"))));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task A_running_command_that_the_platform_cannot_run_is_refused_with_the_reason_and_stays_in_the_configuration()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync(
            new CommandEngineOptions { PlatformSupport = new CommandPlatformSupport(PlatformActionSupport.LinuxOs) });
        CommandDocument windowsOnly = Cmd.Gated("set_display_profile");
        await h.SaveAsync(windowsOnly, Cmd.Process("still_works", "/bin/true"));

        RunResult refused = await h.RunAsync("set_display_profile");
        RunResult works = await h.RunAsync("still_works");

        Assert.Equal(ErrorCodes.Internal, refused.ErrorCode);
        Assert.Contains("not supported on Linux", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("kept", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Gate!.Calls);
        Assert.True(works.Succeeded);

        // Not deleted, not rewritten.
        CommandDocument? stored = h.Config.Effective.GetDocument<CommandDocument>("set_display_profile");
        Assert.NotNull(stored);
        Assert.Equal(windowsOnly.Action, stored.Action);
        Assert.Equal(windowsOnly.Name, stored.Name);
    }

    [Fact]
    public async Task The_same_command_runs_where_the_platform_supports_it()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync(
            new CommandEngineOptions { PlatformSupport = new CommandPlatformSupport(PlatformActionSupport.WindowsOs) });
        await h.SaveAsync(Cmd.Gated("set_display_profile"));
        h.Gate!.AutoComplete = _ => new ExecutionResult(0, "done", string.Empty);

        RunResult result = await h.RunAsync("set_display_profile");

        Assert.True(result.Succeeded);
        Assert.Single(h.Gate.Calls);
    }

    private sealed class FixedBuiltins : IBuiltinActionProvider
    {
        public FixedBuiltins(params string[] names)
        {
            Actions = [.. names.Select(name => new BuiltinActionDescriptor(name, name, RunAsValues.System, []))];
        }

        public IReadOnlyList<BuiltinActionDescriptor> Actions { get; }

        public ValueTask<ExecutionResult> ExecuteAsync(
            string name,
            IReadOnlyDictionary<string, object> variables,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ExecutionResult(0, string.Empty, string.Empty));
        }
    }
}

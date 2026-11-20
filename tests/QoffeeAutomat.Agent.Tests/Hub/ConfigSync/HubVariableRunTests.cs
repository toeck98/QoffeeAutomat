using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Tests.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config.HubLayer;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Tests.Hub.ConfigSync;

/// <summary>The variables document of the hub reaches the runs of the agent as environment variables (PLAN.md S20, D-012, T908).</summary>
[Trait("Category", "ConfigSync")]
public sealed class HubVariableRunTests
{
    [Fact]
    public async Task Hub_variables_reach_every_run_as_QA_HUBVAR_environment_variables_and_never_as_arguments()
    {
        await using var host = new LinkTestHost();
        HubApplyResult applied = await host.Config.Hub.ApplyAsync(
            HubDocs.Generation(
                1,
                HubDocs.Open,
                HubDocs.Command("hub_cmd"),
                HubDocs.Variables("site", "SITE_NAME", "Home office")),
            CancellationToken.None);
        Assert.True(applied.Applied, applied.Error);
        CommandEngine engine = host.Agent.Services.GetRequiredService<CommandEngine>();

        RunResult result = await engine.RunAsync(new RunRequest("hub_cmd"));

        Assert.Null(result.ErrorCode);
        ProcessLaunchRequest launch = Assert.Single(host.Launcher.Launches);
        Assert.Equal("Home office", launch.EnvironmentVariables["QA_HUBVAR_SITE_NAME"]);
        Assert.DoesNotContain(launch.Arguments, argument => argument.Contains("Home office", StringComparison.Ordinal));

        // The hub withdraws the variable: the next run does not get it.
        HubApplyResult second = await host.Config.Hub.ApplyAsync(
            HubDocs.Generation(2, HubDocs.Open, HubDocs.Command("hub_cmd")),
            CancellationToken.None);
        Assert.True(second.Applied, second.Error);

        RunResult again = await engine.RunAsync(new RunRequest("hub_cmd"));

        Assert.Null(again.ErrorCode);
        Assert.False(host.Launcher.Launches[^1].EnvironmentVariables.ContainsKey("QA_HUBVAR_SITE_NAME"));
    }
}

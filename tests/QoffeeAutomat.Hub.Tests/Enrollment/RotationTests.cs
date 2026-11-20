using System.Net;
using System.Text.Json.Nodes;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Enrollment;

/// <summary>Credential rotation bookkeeping (T904): the old credential stays valid until the agent has used the new one.</summary>
[Trait("Category", "Enrollment")]
public sealed class RotationTests
{
    [Fact]
    public async Task A_rotation_that_the_agent_acknowledged_replaces_the_credential_and_revokes_the_old_one()
    {
        var notifier = new RecordingNotifier { RotateResult = true };
        await using HubTestHost host = await HubTestHost.StartAsync(configure: notifier.Register);
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        string old = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "rotating");

        using HttpResponseMessage response = await host.Admin.PostAsync("/api/v1/agents/rotating/rotate-credential");

        JsonNode agent = await HubAssert.JsonAsync(response);
        Assert.False(agent["rotationPending"]!.GetValue<bool>());
        (string agentId, string fresh) = Assert.Single(notifier.Rotations);
        Assert.Equal("rotating", agentId);
        Assert.NotEqual(old, fresh);
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, fresh));
        Assert.Equal((HttpStatusCode.Unauthorized, "revoked"), await EnrollmentKit.OpenLinkAsync(host, old));
        Assert.DoesNotContain(fresh, agent.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rotation_that_nobody_received_is_abandoned_and_the_old_credential_stays()
    {
        var notifier = new RecordingNotifier { RotateResult = false };
        await using HubTestHost host = await HubTestHost.StartAsync(configure: notifier.Register);
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        string old = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "offline");

        using HttpResponseMessage response = await host.Admin.PostAsync("/api/v1/agents/offline/rotate-credential");

        await HubAssert.ProblemAsync(response, HttpStatusCode.Conflict, "agent_offline");
        (_, string neverDelivered) = Assert.Single(notifier.Rotations);
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, old));
        Assert.Equal((HttpStatusCode.Unauthorized, "revoked"), await EnrollmentKit.OpenLinkAsync(host, neverDelivered));
        Assert.False((await EnrollmentKit.GetAgentAsync(host, "offline"))["rotationPending"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Only_an_enrolled_agent_can_be_rotated()
    {
        var notifier = new RecordingNotifier();
        await using HubTestHost host = await HubTestHost.StartAsync(configure: notifier.Register);
        TestToken gated = await EnrollmentKit.CreateTokenAsync(host, approval: true);
        using HttpResponseMessage pending = await EnrollmentKit.EnrollAsync(host, gated.Secret, "waiting");
        await HubAssert.JsonAsync(pending);

        using HttpResponseMessage waiting = await host.Admin.PostAsync("/api/v1/agents/waiting/rotate-credential");
        using HttpResponseMessage unknown = await host.Admin.PostAsync("/api/v1/agents/nobody/rotate-credential");

        await HubAssert.ProblemAsync(waiting, HttpStatusCode.Conflict, "invalid_state");
        await HubAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_agent");
        Assert.Empty(notifier.Rotations);
    }

    [Fact]
    public async Task Both_credentials_work_until_the_agent_connects_with_the_new_one()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        string old = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "bookkeeping");
        AgentRegistry registry = host.Service<AgentRegistry>();

        RotationStart start = await registry.BeginRotationAsync("bookkeeping", CancellationToken.None);

        Assert.Equal(AgentDecision.Done, start.Decision);
        string fresh = start.Credential!;
        Assert.True((await registry.FindAsync("bookkeeping", CancellationToken.None))!.RotationPending);
        Assert.Equal(AgentCredentialOutcome.Accepted, (await registry.AuthenticateAsync(old, CancellationToken.None)).Outcome);
        Assert.True((await registry.FindAsync("bookkeeping", CancellationToken.None))!.RotationPending);

        // Using the new credential proves that the agent has it: the old one is revoked from then on.
        Assert.Equal(AgentCredentialOutcome.Accepted, (await registry.AuthenticateAsync(fresh, CancellationToken.None)).Outcome);
        Assert.False((await registry.FindAsync("bookkeeping", CancellationToken.None))!.RotationPending);
        Assert.Equal(AgentCredentialOutcome.Revoked, (await registry.AuthenticateAsync(old, CancellationToken.None)).Outcome);
        Assert.Equal(AgentCredentialOutcome.Accepted, (await registry.AuthenticateAsync(fresh, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_second_rotation_replaces_the_first_one_that_was_not_used()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        string old = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "twice-rotated");
        AgentRegistry registry = host.Service<AgentRegistry>();

        string first = (await registry.BeginRotationAsync("twice-rotated", CancellationToken.None)).Credential!;
        string second = (await registry.BeginRotationAsync("twice-rotated", CancellationToken.None)).Credential!;

        Assert.NotEqual(first, second);
        Assert.Equal(AgentCredentialOutcome.Revoked, (await registry.AuthenticateAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(AgentCredentialOutcome.Accepted, (await registry.AuthenticateAsync(old, CancellationToken.None)).Outcome);
        await registry.CompleteRotationAsync("twice-rotated", CancellationToken.None);
        Assert.Equal(AgentCredentialOutcome.Accepted, (await registry.AuthenticateAsync(second, CancellationToken.None)).Outcome);
        Assert.Equal(AgentCredentialOutcome.Revoked, (await registry.AuthenticateAsync(old, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Removing_an_agent_in_the_middle_of_a_rotation_revokes_both_credentials()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        string old = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "gone");
        AgentRegistry registry = host.Service<AgentRegistry>();
        string fresh = (await registry.BeginRotationAsync("gone", CancellationToken.None)).Credential!;

        AgentRecord? removed = await registry.RemoveAsync("gone", CancellationToken.None);

        Assert.NotNull(removed);
        Assert.Equal(AgentCredentialOutcome.Revoked, (await registry.AuthenticateAsync(old, CancellationToken.None)).Outcome);
        Assert.Equal(AgentCredentialOutcome.Revoked, (await registry.AuthenticateAsync(fresh, CancellationToken.None)).Outcome);
        Assert.Null(await registry.RemoveAsync("gone", CancellationToken.None));
    }
}

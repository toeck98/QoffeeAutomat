using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Agent.Tests.Link;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Tests.Hub.ConfigSync;

/// <summary>Helpers for the tests of <c>config_sync</c>: the hub side of the protocol over the real hub link.</summary>
internal static class SyncRig
{
    public static string AgentIdOf(LinkTestHost host) => host.Agent.Services.GetRequiredService<AgentIdentity>().Id;

    /// <summary>Joins the hub and answers <c>hello</c> as a hub that offers <c>config_sync</c>.</summary>
    public static async Task<LinkTestClient> JoinAsync(FakeHub hub, LinkTestHost host)
    {
        HubJoinResult joined = await HubLinkRig.ManagerOf(host).JoinAsync(hub.EnrollmentString(), CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);
        LinkTestClient peer = await hub.WaitForAgentAsync();
        await peer.SendAsync(FakeHub.HubClientHello(HubFeatures.ConfigSync));
        return peer;
    }

    public static async Task<JsonElement> OfferAsync(LinkTestClient peer, ConfigDesiredMessage message)
    {
        await peer.SendAsync(JsonSerializer.Serialize(message, HubJsonContext.Default.ConfigDesiredMessage));
        return await peer.WaitForResultAsync(message.Id);
    }

    public static async Task<JsonElement> SendDocumentsAsync(LinkTestClient peer, long requestId, long generation, bool final, params HubDocument[] documents)
    {
        var message = new ConfigDocumentsMessage(requestId, generation, documents, final);
        await peer.SendAsync(JsonSerializer.Serialize(message, HubJsonContext.Default.ConfigDocumentsMessage));
        return await peer.WaitForResultAsync(requestId);
    }

    public static Task<JsonElement> WaitForStatusAsync(LinkTestClient peer, long generation, int after = 0)
    {
        return peer.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == HubMessageTypes.ConfigStatus && frame.GetProperty("generation").GetInt64() == generation,
            "the config_status of generation " + generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            after);
    }

    public static string[] NeededIds(JsonElement result)
    {
        return [.. result.GetProperty("data").GetProperty("need").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];
    }

    public static string ItemState(JsonElement status, string id)
    {
        return status.GetProperty("results").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id).GetProperty("state").GetString()!;
    }
}

/// <summary>The protocol of <c>config_sync</c> on the agent (PLAN.md S20, D-027, T908), over a real hub link to a fake hub that signs like the real one.</summary>
[Trait("Category", "ConfigSync")]
public sealed class ConfigSyncProtocolTests
{
    [Fact]
    public async Task The_agent_announces_config_sync_and_reports_generation_zero_after_the_handshake()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);

        Assert.Contains(HubFeatures.ConfigSync, peer.Frames[0].GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        JsonElement status = await peer.WaitForTypeAsync(HubMessageTypes.ConfigStatus);

        Assert.Equal(0, status.GetProperty("generation").GetInt64());
        Assert.Equal(ConfigStates.Applied, status.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("results").GetArrayLength());
        Assert.Empty(peer.Problems);
    }

    [Fact]
    public async Task A_signed_desired_state_is_fetched_validated_applied_and_reported_and_the_manifest_follows()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        string revBefore = host.ManifestSource.Current.Rev;
        HubDocument command = HubDocs.Command("hub_cmd");
        HubDocument script = HubDocs.Script("hub.ps1", "Write-Output 'hi'\n");
        HubDocument variables = HubDocs.Variables("site", "SITE_NAME", "Home");

        JsonElement offered = await SyncRig.OfferAsync(peer, hub.Desired(100, SyncRig.AgentIdOf(host), 1, HubDocs.Open, command, script, variables));

        Assert.True(offered.GetProperty("ok").GetBoolean());
        string[] needed = SyncRig.NeededIds(offered);
        Assert.Equal(3, needed.Length);
        Assert.Contains("hub_cmd", needed);

        JsonElement delivered = await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, command, script, variables);
        Assert.True(delivered.GetProperty("ok").GetBoolean());

        JsonElement status = await SyncRig.WaitForStatusAsync(peer, 1);
        Assert.Equal(ConfigStates.Applied, status.GetProperty("state").GetString());
        Assert.Equal(ConfigItemStates.Applied, SyncRig.ItemState(status, "hub_cmd"));
        Assert.Equal(ConfigItemStates.Applied, SyncRig.ItemState(status, "hub.ps1"));
        Assert.Equal(ConfigItemStates.Applied, SyncRig.ItemState(status, "site"));

        Assert.Equal(1, host.Config.Hub.State.Generation);
        Assert.Equal(DocumentOrigins.Hub, host.Config.Effective.GetDocument<CommandDocument>("hub_cmd")!.Origin);
        Assert.Equal("Home", host.Config.Hub.Variables["SITE_NAME"]);

        // The manifest rev follows the effective configuration, and the hub command is announced to Home Assistant under its own id.
        Assert.NotEqual(revBefore, host.ManifestSource.Current.Rev);
        Assert.Contains(host.ManifestSource.Current.Commands, entry => entry.Id == "hub_cmd");
        Assert.Empty(peer.Problems);
    }

    [Fact]
    public async Task A_desired_state_with_a_bad_signature_changes_nothing_and_reports_failed()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        HubDocument command = HubDocs.Command("hub_cmd");

        JsonElement offered = await SyncRig.OfferAsync(peer, FakeHub.DesiredWithForeignKey(100, SyncRig.AgentIdOf(host), 1, HubDocs.Locked, command));

        Assert.False(offered.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.BadRequest, LinkTestClient.ErrorCodeOf(offered));
        JsonElement status = await SyncRig.WaitForStatusAsync(peer, 1);
        Assert.Equal(ConfigStates.Failed, status.GetProperty("state").GetString());
        Assert.False(host.Config.Hub.State.IsActive);
        Assert.False(host.Config.Hub.State.LocalConfigLocked);
        Assert.Null(host.Config.Effective.GetDocument<CommandDocument>("hub_cmd"));
        Assert.NotNull(host.Config.Hub.State.LastError);
    }

    [Fact]
    public async Task A_desired_state_that_was_signed_for_another_agent_or_changed_after_signing_is_refused()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        HubDocument command = HubDocs.Command("hub_cmd");

        // Signed for a different agent id: a state that was meant for another PC must not apply here.
        ConfigDesiredMessage foreign = hub.Desired(100, "ffffffffffffffffffffffffffffffff", 1, HubDocs.Open, command);
        JsonElement forged = await SyncRig.OfferAsync(peer, foreign);
        Assert.False(forged.GetProperty("ok").GetBoolean());

        // Changed after signing: the policy was flipped to locked on the way.
        ConfigDesiredMessage signed = hub.Desired(101, SyncRig.AgentIdOf(host), 2, HubDocs.Open, command);
        JsonElement changed = await SyncRig.OfferAsync(peer, signed with { Policy = HubDocs.Locked });
        Assert.False(changed.GetProperty("ok").GetBoolean());

        Assert.False(host.Config.Hub.State.IsActive);
    }

    [Fact]
    public async Task A_document_whose_hash_is_not_the_signed_hash_changes_nothing_and_reports_failed()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        string agentId = SyncRig.AgentIdOf(host);
        HubDocument command = HubDocs.Command("hub_cmd", "As signed");

        // The body was changed on the way and still carries the signed hash.
        HubDocument tampered = command with { Body = HubDocs.Command("hub_cmd", "Tampered").Body };
        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, agentId, 1, HubDocs.Open, command))).GetProperty("ok").GetBoolean());
        JsonElement refused = await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, tampered);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        JsonElement status = await SyncRig.WaitForStatusAsync(peer, 1);
        Assert.Equal(ConfigStates.Failed, status.GetProperty("state").GetString());
        Assert.Equal(ConfigItemStates.Invalid, SyncRig.ItemState(status, "hub_cmd"));
        Assert.False(host.Config.Hub.State.IsActive);

        // The document carries a hash of its own body, which is not the hash that the hub signed.
        HubDocument other = HubDocs.Command("hub_cmd", "Not what was signed");
        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(102, agentId, 2, HubDocs.Open, command))).GetProperty("ok").GetBoolean());
        JsonElement refusedAgain = await SyncRig.SendDocumentsAsync(peer, 103, 2, final: true, other);
        Assert.False(refusedAgain.GetProperty("ok").GetBoolean());
        Assert.False(host.Config.Hub.State.IsActive);
        Assert.Null(host.Config.Effective.GetDocument<CommandDocument>("hub_cmd"));
    }

    [Fact]
    public async Task A_document_that_was_not_asked_for_fails_the_generation()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        HubDocument command = HubDocs.Command("hub_cmd");
        HubDocument extra = HubDocs.Command("extra");

        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, SyncRig.AgentIdOf(host), 1, HubDocs.Open, command))).GetProperty("ok").GetBoolean());
        JsonElement refused = await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, command, extra);

        Assert.False(refused.GetProperty("ok").GetBoolean());
        JsonElement status = await SyncRig.WaitForStatusAsync(peer, 1);
        Assert.Equal(ConfigStates.Failed, status.GetProperty("state").GetString());
        Assert.False(host.Config.Hub.State.IsActive);
    }

    [Fact]
    public async Task One_invalid_document_fails_the_generation_and_the_valid_ones_are_not_applied()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        HubDocument good = HubDocs.Command("good");
        HubDocument broken = HubDocs.InvalidCommand("broken");

        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, SyncRig.AgentIdOf(host), 1, HubDocs.Locked, good, broken))).GetProperty("ok").GetBoolean());
        Assert.True((await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, good, broken)).GetProperty("ok").GetBoolean());

        JsonElement status = await SyncRig.WaitForStatusAsync(peer, 1);
        Assert.Equal(ConfigStates.Failed, status.GetProperty("state").GetString());
        Assert.Equal(ConfigItemStates.Invalid, SyncRig.ItemState(status, "broken"));
        JsonElement brokenResult = status.GetProperty("results").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "broken");
        Assert.True(brokenResult.GetProperty("errors").GetArrayLength() > 0);

        Assert.False(host.Config.Hub.State.IsActive);
        Assert.False(host.Config.Hub.State.LocalConfigLocked);
        Assert.Null(host.Config.Effective.GetDocument<CommandDocument>("good"));
        Assert.NotNull(host.Config.Hub.State.LastError);
        Assert.Empty(peer.Problems);
    }

    [Fact]
    public async Task A_generation_that_is_not_newer_than_the_applied_one_is_ignored_and_needs_no_documents()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        string agentId = SyncRig.AgentIdOf(host);
        HubDocument command = HubDocs.Command("hub_cmd", "Generation five");
        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, agentId, 5, HubDocs.Open, command))).GetProperty("ok").GetBoolean());
        Assert.True((await SyncRig.SendDocumentsAsync(peer, 101, 5, final: true, command)).GetProperty("ok").GetBoolean());
        await SyncRig.WaitForStatusAsync(peer, 5);

        // An old state that is validly signed, replayed: nothing is asked for and nothing changes.
        HubDocument replaced = HubDocs.Command("hub_cmd", "Replayed");
        JsonElement replay = await SyncRig.OfferAsync(peer, hub.Desired(102, agentId, 4, HubDocs.Locked, replaced));
        JsonElement same = await SyncRig.OfferAsync(peer, hub.Desired(103, agentId, 5, HubDocs.Locked, replaced));

        Assert.True(replay.GetProperty("ok").GetBoolean());
        Assert.Empty(SyncRig.NeededIds(replay));
        Assert.Empty(SyncRig.NeededIds(same));
        Assert.Equal(5, host.Config.Hub.State.Generation);
        Assert.False(host.Config.Hub.State.LocalConfigLocked);
        Assert.Equal("Generation five", host.Config.Effective.GetDocument<CommandDocument>("hub_cmd")!.Name);
    }

    [Fact]
    public async Task Reconnecting_with_an_up_to_date_hub_layer_transfers_no_document_bodies()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        string agentId = SyncRig.AgentIdOf(host);
        HubDocument command = HubDocs.Command("hub_cmd");
        HubDocument script = HubDocs.Script("hub.ps1", "text\n");
        await using (LinkTestClient first = await SyncRig.JoinAsync(hub, host))
        {
            Assert.True((await SyncRig.OfferAsync(first, hub.Desired(100, agentId, 1, HubDocs.Open, command, script))).GetProperty("ok").GetBoolean());
            Assert.True((await SyncRig.SendDocumentsAsync(first, 101, 1, final: true, command, script)).GetProperty("ok").GetBoolean());
            await SyncRig.WaitForStatusAsync(first, 1);
            await first.CloseAsync();
        }

        await using LinkTestClient second = await hub.WaitForAgentAsync();
        await second.SendAsync(FakeHub.HubClientHello(HubFeatures.ConfigSync));

        // After every handshake the agent says which generation it has.
        JsonElement hello = await second.WaitForTypeAsync(HubMessageTypes.ConfigStatus);
        Assert.Equal(1, hello.GetProperty("generation").GetInt64());
        Assert.Equal(2, hello.GetProperty("results").GetArrayLength());

        // A newer generation with the same documents: the answer is an empty need, and the generation is applied without a document frame.
        JsonElement offered = await SyncRig.OfferAsync(second, hub.Desired(200, agentId, 2, HubDocs.Open, command, script));
        Assert.True(offered.GetProperty("ok").GetBoolean());
        Assert.Empty(SyncRig.NeededIds(offered));
        JsonElement status = await SyncRig.WaitForStatusAsync(second, 2);
        Assert.Equal(ConfigStates.Applied, status.GetProperty("state").GetString());
        Assert.Equal(2, host.Config.Hub.State.Generation);
        Assert.NotNull(host.Config.Effective.GetDocument<CommandDocument>("hub_cmd"));
    }

    [Fact]
    public async Task Only_the_changed_documents_are_asked_for_when_a_newer_generation_arrives()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        string agentId = SyncRig.AgentIdOf(host);
        HubDocument kept = HubDocs.Command("kept");
        HubDocument before = HubDocs.Command("changing", "Before");
        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, agentId, 1, HubDocs.Open, kept, before))).GetProperty("ok").GetBoolean());
        Assert.True((await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, kept, before)).GetProperty("ok").GetBoolean());
        await SyncRig.WaitForStatusAsync(peer, 1);

        HubDocument after = HubDocs.Command("changing", "After");
        JsonElement offered = await SyncRig.OfferAsync(peer, hub.Desired(102, agentId, 2, HubDocs.Open, kept, after));

        string[] needed = SyncRig.NeededIds(offered);
        Assert.Equal("changing", Assert.Single(needed));
        Assert.True((await SyncRig.SendDocumentsAsync(peer, 103, 2, final: true, after)).GetProperty("ok").GetBoolean());
        await SyncRig.WaitForStatusAsync(peer, 2);
        Assert.Equal("After", host.Config.Effective.GetDocument<CommandDocument>("changing")!.Name);
        Assert.NotNull(host.Config.Effective.GetDocument<CommandDocument>("kept"));
    }

    [Fact]
    public async Task A_hub_command_that_shadows_a_local_command_keeps_the_entity_id_and_changes_the_manifest_rev_and_a_withdrawn_one_restores_the_local_command()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        string agentId = SyncRig.AgentIdOf(host);
        await HubDocs.SaveLocalCommandAsync(host.Config, "shared", "Local version");
        string localRev = host.ManifestSource.Current.Rev;
        Assert.Equal(1, host.ManifestSource.Current.Commands.Count(entry => entry.Id == "shared"));

        HubDocument hubVersion = HubDocs.Command("shared", "Hub version");
        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, agentId, 1, HubDocs.Open, hubVersion))).GetProperty("ok").GetBoolean());
        Assert.True((await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, hubVersion)).GetProperty("ok").GetBoolean());
        await SyncRig.WaitForStatusAsync(peer, 1);

        // The same id, so Home Assistant keeps its entity and its customisations; the rev changes because the content did.
        Assert.Equal(1, host.ManifestSource.Current.Commands.Count(entry => entry.Id == "shared"));
        Assert.Equal("Hub version", host.ManifestSource.Current.Commands.Single(entry => entry.Id == "shared").Name);
        string hubRev = host.ManifestSource.Current.Rev;
        Assert.NotEqual(localRev, hubRev);

        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(102, agentId, 2, HubDocs.Open))).GetProperty("ok").GetBoolean());
        await SyncRig.WaitForStatusAsync(peer, 2);

        Assert.Equal("Local version", host.ManifestSource.Current.Commands.Single(entry => entry.Id == "shared").Name);
        Assert.Equal(localRev, host.ManifestSource.Current.Rev);
    }

    [Fact]
    public async Task When_the_hub_removes_the_agent_the_hub_layer_is_deleted_and_the_local_layer_is_active_again()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        string agentId = SyncRig.AgentIdOf(host);
        await HubDocs.SaveLocalCommandAsync(host.Config, "mine", "Local command");
        HubDocument command = HubDocs.Command("hub_cmd");
        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, agentId, 1, HubDocs.Locked, command))).GetProperty("ok").GetBoolean());
        Assert.True((await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, command)).GetProperty("ok").GetBoolean());
        await SyncRig.WaitForStatusAsync(peer, 1);
        Assert.Null(host.Config.Effective.GetDocument<CommandDocument>("mine"));

        await peer.SendAsync("{\"t\":\"" + HubMessageTypes.Unenroll + "\",\"reason\":\"removed\"}");

        await TestWait.UntilAsync(() => !host.Config.Hub.State.IsActive, "the hub layer to be removed after unenroll");
        Assert.Null(host.Config.Effective.GetDocument<CommandDocument>("hub_cmd"));
        Assert.Equal("Local command", host.Config.Effective.GetDocument<CommandDocument>("mine")!.Name);
        Assert.False(host.Config.Hub.State.LocalConfigLocked);
    }

    [Fact]
    public async Task Leaving_the_hub_with_convert_keeps_the_hub_documents_as_local_documents()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await SyncRig.JoinAsync(hub, host);
        HubDocument command = HubDocs.Command("hub_cmd", "From the hub");
        Assert.True((await SyncRig.OfferAsync(peer, hub.Desired(100, SyncRig.AgentIdOf(host), 1, HubDocs.Open, command))).GetProperty("ok").GetBoolean());
        Assert.True((await SyncRig.SendDocumentsAsync(peer, 101, 1, final: true, command)).GetProperty("ok").GetBoolean());
        await SyncRig.WaitForStatusAsync(peer, 1);

        bool left = await HubLinkRig.ManagerOf(host).LeaveAsync(HubLinkManager.DocumentsConvert, CancellationToken.None);

        Assert.True(left);
        Assert.False(host.Config.Hub.State.IsActive);
        CommandDocument local = host.Config.Local.GetDocument<CommandDocument>("hub_cmd")!;
        Assert.Equal("From the hub", local.Name);
        Assert.Equal(DocumentOrigins.Local, local.Origin);
    }

    [Fact]
    public async Task A_hub_that_did_not_announce_config_sync_gets_no_answer_to_a_desired_state_but_an_error()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubJoinResult joined = await HubLinkRig.ManagerOf(host).JoinAsync(hub.EnrollmentString(), CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);
        await using LinkTestClient peer = await hub.WaitForAgentAsync();
        await peer.SendAsync(FakeHub.HubClientHello());

        JsonElement offered = await SyncRig.OfferAsync(peer, hub.Desired(100, SyncRig.AgentIdOf(host), 1, HubDocs.Open, HubDocs.Command("hub_cmd")));

        Assert.False(offered.GetProperty("ok").GetBoolean());
        Assert.False(host.Config.Hub.State.IsActive);
        Assert.Empty(peer.FramesOf(HubMessageTypes.ConfigStatus));
    }
}

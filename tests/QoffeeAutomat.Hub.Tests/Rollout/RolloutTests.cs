using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Storage;
using QoffeeAutomat.Hub.Tests.Config;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Rollout;

/// <summary>
/// The configuration roll-out over the agent link (T909, PLAN.md S20): the hub pushes the desired state to agents that are online when a profile is published or
/// rolled back, when an assignment or the tags of an agent change and when an agent connects or reports; it serves exactly the documents an agent asks for in frames
/// below the limit, and it records what each agent reports. The agents are played by hand, so every answer and every report is the test's.
/// </summary>
[Trait("Category", "Rollout")]
public sealed class RolloutTests
{
    /// <summary>How long a test waits before it concludes that the hub did not send something. A frame that the hub sends arrives well within this.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private static string CodeOf(JsonElement document)
    {
        return document.GetProperty("body").GetProperty("action").GetProperty("code").GetString()!;
    }

    private static ConfigItemResult Applied(string id) => new("command", id, ConfigItemStates.Applied);

    private static async Task<JsonNode> GetAsync(HubApiClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        return await HubAssert.JsonAsync(response, expected);
    }

    private static async Task ProblemAsync(HubApiClient client, string path, HttpStatusCode status, string code)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        _ = await HubAssert.ProblemAsync(response, status, code);
    }

    // ------------------------------------------------------------------
    // Pushing
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_published_profile_is_pushed_to_an_online_agent_and_the_report_of_the_agent_is_stored()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");

        await rig.PublishCommandProfileAsync("office", "hello", "all");

        JsonElement desired = await agent.NextDesiredAsync();
        Assert.Equal(1, desired.GetProperty("generation").GetInt64());
        JsonElement item = Assert.Single(desired.GetProperty("items").EnumerateArray());
        Assert.Equal("command", item.GetProperty("kind").GetString());
        Assert.Equal("hello", item.GetProperty("id").GetString());
        Assert.Equal("allow", desired.GetProperty("policy").GetProperty("local_config").GetString());
        Assert.Equal("allow", desired.GetProperty("policy").GetProperty("direct_link").GetString());

        // The signature is the one the hub stored when it created the generation.
        AgentGeneration stored = (await rig.Config.GetGenerationAsync("pc1", CancellationToken.None))!;
        Assert.Equal(stored.Signature, desired.GetProperty("sig").GetString());

        agent.AnswerNeed(desired, ("command", "hello"));
        JsonElement documents = await agent.NextDocumentsAsync();
        Assert.Equal(1, documents.GetProperty("generation").GetInt64());
        Assert.True(documents.GetProperty("final").GetBoolean());
        JsonElement document = Assert.Single(documents.GetProperty("documents").EnumerateArray());
        Assert.Equal("command", document.GetProperty("kind").GetString());
        Assert.Equal("hello", document.GetProperty("id").GetString());
        Assert.Equal(item.GetProperty("hash").GetString(), document.GetProperty("hash").GetString());
        agent.AnswerOk(documents);
        agent.ReportStatus(1, ConfigStates.Applied, Applied("hello"));

        AgentConfigStatusInfo status = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate, "the agent to be up to date");
        Assert.True(status.Online);
        Assert.Equal(1, status.DesiredGeneration);
        Assert.Equal(1, status.PushedGeneration);
        Assert.Equal(1, status.AppliedGeneration);
        Assert.Null(status.DeliveryError);
        Assert.Equal(ConfigStates.Applied, status.ReportedState);
        DocumentRollout result = Assert.Single(status.Documents);
        Assert.Equal("hello", result.Id);
        Assert.Equal(ConfigItemStates.Applied, result.State);
        Assert.Empty(status.Errors);
    }

    [Fact]
    public async Task Only_the_documents_the_agent_lacks_are_sent()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.SaveCommandAsync("one");
        await rig.SaveCommandAsync("two");
        await rig.CreateProfileAsync("office", ["one", "two"]);
        _ = await rig.PublishAsync("office");
        await rig.AssignAsync("office", "all");

        JsonElement first = await agent.ApplyNextAsync();
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        _ = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate, "generation 1 to be applied");

        // Only "two" changes: the agent says that it needs only that one.
        await rig.SaveCommandAsync("two", "Write-Output changed");
        await rig.UpdateProfileAsync("office", ["one", "two"]);
        _ = await rig.PublishAsync("office");

        JsonElement second = await agent.NextDesiredAsync();
        Assert.Equal(2, second.GetProperty("generation").GetInt64());
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        agent.AnswerNeed(second, ("command", "two"));
        JsonElement documents = await agent.NextDocumentsAsync();
        JsonElement only = Assert.Single(documents.GetProperty("documents").EnumerateArray());
        Assert.Equal("two", only.GetProperty("id").GetString());
        Assert.Equal("Write-Output changed", CodeOf(only));
        agent.AnswerOk(documents);
        agent.ReportStatus(2, ConfigStates.Applied, Applied("one"), Applied("two"));

        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 2 && info.State == RolloutStates.UpToDate, "generation 2 to be applied");
    }

    [Fact]
    public async Task A_rollback_is_pushed_as_a_new_generation_with_the_documents_of_the_earlier_revision()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "all", "Write-Output v1");
        JsonElement first = await agent.ApplyNextAsync();
        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 1, "generation 1 to be applied");

        await rig.SaveCommandAsync("hello", "Write-Output v2");
        await rig.UpdateProfileAsync("office", ["hello"]);
        _ = await rig.PublishAsync("office");
        _ = await agent.ApplyNextAsync();
        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 2, "generation 2 to be applied");

        PublishResult rolledBack = await rig.PublishAsync("office", revision: 1);
        Assert.False(rolledBack.NewRevision);

        JsonElement desired = await agent.NextDesiredAsync();
        Assert.Equal(3, desired.GetProperty("generation").GetInt64());
        Assert.Equal(
            first.GetProperty("items")[0].GetProperty("hash").GetString(),
            desired.GetProperty("items")[0].GetProperty("hash").GetString());
        agent.AnswerNeed(desired, ("command", "hello"));
        JsonElement documents = await agent.NextDocumentsAsync();
        Assert.Equal("Write-Output v1", CodeOf(documents.GetProperty("documents")[0]));
        agent.AnswerOk(documents);
        agent.ReportStatus(3, ConfigStates.Applied, Applied("hello"));
        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 3, "generation 3 to be applied");
    }

    [Fact]
    public async Task A_new_assignment_reaches_an_agent_that_is_online()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        await rig.EnrollAsync("pc2");
        ScriptedConfigAgent pc1 = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        ScriptedConfigAgent pc2 = await ScriptedConfigAgent.ConnectAsync(rig, "pc2");

        await rig.PublishCommandProfileAsync("office", "hello", "agent:pc1");

        Assert.Equal(1, (await pc1.ApplyNextAsync()).GetProperty("generation").GetInt64());
        await Task.Delay(Settle);
        Assert.DoesNotContain(HubMessageTypes.ConfigDesired, await pc2.DrainAsync());
    }

    // ------------------------------------------------------------------
    // Reconnecting
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_agent_that_was_offline_during_publishes_receives_the_current_generation_on_reconnect_and_skips_the_intermediate_ones()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent before = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "all", "Write-Output v1");
        _ = await before.ApplyNextAsync();
        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 1, "generation 1 to be applied");
        await before.DisconnectAsync(rig);

        // Two publishes while the agent is away: generations 2 and 3.
        await rig.SaveCommandAsync("hello", "Write-Output v2");
        await rig.UpdateProfileAsync("office", ["hello"]);
        _ = await rig.PublishAsync("office");
        await rig.SaveCommandAsync("hello", "Write-Output v3");
        await rig.UpdateProfileAsync("office", ["hello"]);
        _ = await rig.PublishAsync("office");
        Assert.Equal(3, await rig.GenerationOfAsync("pc1"));

        ScriptedConfigAgent after = await ScriptedConfigAgent.ConnectAsync(rig, "pc1", applied: 1);
        JsonElement desired = await after.NextDesiredAsync();
        Assert.Equal(3, desired.GetProperty("generation").GetInt64());
        after.AnswerNeed(desired, ("command", "hello"));
        JsonElement documents = await after.NextDocumentsAsync();
        Assert.Equal("Write-Output v3", CodeOf(documents.GetProperty("documents")[0]));
        after.AnswerOk(documents);
        after.ReportStatus(3, ConfigStates.Applied, Applied("hello"));
        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 3 && info.State == RolloutStates.UpToDate, "generation 3 to be applied");

        // Generation 2 is never sent, and generation 3 only once.
        await Task.Delay(Settle);
        Assert.DoesNotContain(HubMessageTypes.ConfigDesired, await after.DrainAsync());
    }

    [Fact]
    public async Task Reconnecting_with_an_up_to_date_hub_layer_transfers_no_document_bodies()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent first = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "all");
        JsonElement firstDesired = await first.ApplyNextAsync();
        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 1, "generation 1 to be applied");
        await first.DisconnectAsync(rig);

        // The agent reports the generation it has: the hub sends nothing at all.
        ScriptedConfigAgent second = await ScriptedConfigAgent.ConnectAsync(rig, "pc1", applied: 1);
        await Task.Delay(Settle);
        string[] frames = await second.DrainAsync();
        Assert.DoesNotContain(HubMessageTypes.ConfigDesired, frames);
        Assert.DoesNotContain(HubMessageTypes.ConfigDocuments, frames);

        // A new generation that only changes the policy: the hub offers it, the agent already carries every document, so no body travels.
        await rig.UpdateProfileAsync("office", ["hello"], new ProfilePolicyRequest("locked", null));
        _ = await rig.PublishAsync("office");
        JsonElement desired = await second.NextDesiredAsync();
        Assert.Equal(2, desired.GetProperty("generation").GetInt64());
        Assert.Equal("locked", desired.GetProperty("policy").GetProperty("local_config").GetString());
        Assert.Equal(
            firstDesired.GetProperty("items")[0].GetProperty("hash").GetString(),
            desired.GetProperty("items")[0].GetProperty("hash").GetString());
        second.AnswerNeed(desired);
        _ = await rig.WaitForAsync("pc1", info => info.PushedGeneration == 2 && info.DeliveryError is null, "the push of generation 2 to complete");
        Assert.DoesNotContain(HubMessageTypes.ConfigDocuments, await second.DrainAsync());

        second.ReportStatus(2, ConfigStates.Applied, Applied("hello"));
        _ = await rig.WaitForAsync("pc1", info => info.AppliedGeneration == 2 && info.State == RolloutStates.UpToDate, "generation 2 to be applied");
    }

    [Fact]
    public async Task A_new_connection_recomputes_the_desired_state_of_a_new_agent_and_pushes_it()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.PublishCommandProfileAsync("everyone", "hello", "all");

        // The agent enrolls after the assignment: it has no generation until it connects.
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");

        Assert.Equal(1, (await agent.ApplyNextAsync()).GetProperty("generation").GetInt64());
        _ = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate, "the agent to be up to date");
    }

    // ------------------------------------------------------------------
    // Reports and failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_document_that_is_invalid_on_one_agent_is_reported_for_that_agent_while_the_others_apply_the_generation()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        await rig.EnrollAsync("pc2");
        ScriptedConfigAgent pc1 = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        ScriptedConfigAgent pc2 = await ScriptedConfigAgent.ConnectAsync(rig, "pc2");

        await rig.PublishCommandProfileAsync("office", "hello", "all");

        // pc1 rejects the document, pc2 applies it.
        JsonElement desired1 = await pc1.NextDesiredAsync();
        pc1.AnswerNeed(desired1, ("command", "hello"));
        pc1.AnswerOk(await pc1.NextDocumentsAsync());
        pc1.ReportStatus(1, ConfigStates.Failed, new ConfigItemResult("command", "hello", ConfigItemStates.Invalid, ["The action is not supported here."]));
        _ = await pc2.ApplyNextAsync();

        AgentConfigStatusInfo failed = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.Failed, "pc1 to report the failure");
        AgentConfigStatusInfo applied = await rig.WaitForAsync("pc2", info => info.State == RolloutStates.UpToDate, "pc2 to apply the generation");

        Assert.Equal(0, failed.AppliedGeneration);
        Assert.Equal(1, failed.ReportedGeneration);
        Assert.Equal(ConfigStates.Failed, failed.ReportedState);
        DocumentRollout invalid = Assert.Single(failed.Documents);
        Assert.Equal(ConfigItemStates.Invalid, invalid.State);
        Assert.Equal(["The action is not supported here."], invalid.Errors.ToArray());
        Assert.Equal(["command/hello: The action is not supported here."], failed.Errors.ToArray());
        Assert.Equal(1, applied.AppliedGeneration);
        Assert.Empty(applied.Errors);

        // The same facts through the API: per agent and for the revision of the profile.
        JsonNode overview = await GetAsync(rig.Hub.Admin, "/api/v1/rollouts");
        Assert.Equal(2, overview["counts"]!["total"]!.GetValue<int>());
        Assert.Equal(1, overview["counts"]!["upToDate"]!.GetValue<int>());
        Assert.Equal(1, overview["counts"]!["failed"]!.GetValue<int>());
        JsonNode profile = await GetAsync(rig.Hub.Admin, "/api/v1/profiles/office/rollout");
        Assert.Equal(1, profile["revision"]!.GetValue<int>());
        JsonNode[] agents = [.. profile["agents"]!.AsArray().Select(agent => agent!)];
        Assert.Equal(["pc1", "pc2"], agents.Select(agent => agent["agentId"]!.GetValue<string>()).ToArray());
        Assert.Equal("failed", agents[0]["state"]!.GetValue<string>());
        Assert.Equal("invalid", agents[0]["documents"]![0]!["state"]!.GetValue<string>());
        Assert.Equal("up_to_date", agents[1]["state"]!.GetValue<string>());
        Assert.Equal("applied", agents[1]["documents"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refused_generation_is_not_sent_again_over_the_same_connection_but_on_request()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "all");

        // The agent refuses the desired state and reports the failure, as it does for a signature it cannot verify.
        JsonElement desired = await agent.NextDesiredAsync();
        agent.AnswerError(desired);
        agent.ReportStatus(1, ConfigStates.Failed);
        AgentConfigStatusInfo failed = await rig.WaitForAsync(
            "pc1",
            info => info.State == RolloutStates.Failed && info.DeliveryError is not null,
            "the refusal to be recorded");
        Assert.Contains("bad_request", failed.DeliveryError, StringComparison.Ordinal);

        // The report that follows the refusal does not make the hub send the same generation again.
        await Task.Delay(Settle);
        Assert.DoesNotContain(HubMessageTypes.ConfigDesired, await agent.DrainAsync());

        // An operator can ask for another attempt.
        using (HttpResponseMessage response = await rig.Hub.Admin.PostAsync("/api/v1/agents/pc1/config-push"))
        {
            JsonNode accepted = await HubAssert.JsonAsync(response, HttpStatusCode.Accepted);
            Assert.Equal("pc1", accepted["agentId"]!.GetValue<string>());
        }

        JsonElement again = await agent.NextDesiredAsync();
        Assert.Equal(1, again.GetProperty("generation").GetInt64());
        agent.AnswerNeed(again);
        _ = await rig.WaitForAsync("pc1", info => info.PushedGeneration == 1 && info.DeliveryError is null, "the second push to complete");
    }

    [Fact]
    public async Task A_failed_generation_is_tried_again_on_the_next_connection_and_its_record_survives_the_handshake()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent first = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "all");
        JsonElement desired = await first.NextDesiredAsync();
        first.AnswerNeed(desired, ("command", "hello"));
        first.AnswerOk(await first.NextDocumentsAsync());
        first.ReportStatus(1, ConfigStates.Failed, new ConfigItemResult("command", "hello", ConfigItemStates.Invalid, ["Not valid."]));
        _ = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.Failed, "the failure to be recorded");
        await first.DisconnectAsync(rig);

        // After the reconnect the agent reports that it still has generation 0; the hub offers generation 1 again.
        ScriptedConfigAgent second = await ScriptedConfigAgent.ConnectAsync(rig, "pc1", applied: 0);
        JsonElement again = await second.NextDesiredAsync();
        Assert.Equal(1, again.GetProperty("generation").GetInt64());

        AgentConfigStatusInfo status = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.Failed, "the failure to stay recorded");
        Assert.Equal(ConfigStates.Failed, status.ReportedState);
        Assert.Equal(["command/hello: Not valid."], status.Errors.ToArray());

        // This time the agent applies it.
        second.AnswerNeed(again, ("command", "hello"));
        second.AnswerOk(await second.NextDocumentsAsync());
        second.ReportStatus(1, ConfigStates.Applied, Applied("hello"));
        AgentConfigStatusInfo recovered = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate, "the agent to recover");
        Assert.Empty(recovered.Errors);
    }

    [Fact]
    public async Task A_malformed_config_status_is_ignored_and_the_connection_stays_open()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");

        agent.Socket.SendText("""{"t":"config_status","generation":"x","state":"applied","results":[]}""");

        await TestWait.UntilAsync(
            () => rig.Hub.Logs.Messages.Any(message => message.Contains("is not valid and was ignored", StringComparison.Ordinal)),
            "the frame to be ignored");
        Assert.True(rig.Gateway.GetPresence("pc1").Online);
    }

    // ------------------------------------------------------------------
    // Tags
    // ------------------------------------------------------------------

    [Fact]
    public async Task Changing_the_tags_of_an_agent_recomputes_its_desired_state_and_pushes_it()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "tag:office");
        Assert.Equal(0, await rig.GenerationOfAsync("pc1"));

        // The tag brings the profile.
        using (HttpResponseMessage response = await rig.Hub.Admin.PatchAsync("/api/v1/agents/pc1", """{"tags":["office"]}"""))
        {
            _ = await HubAssert.JsonAsync(response);
        }

        JsonElement desired = await agent.ApplyNextAsync();
        Assert.Equal(1, desired.GetProperty("generation").GetInt64());
        Assert.Equal(1, desired.GetProperty("items").GetArrayLength());
        _ = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate, "generation 1 to be applied");

        // Without the tag the agent has nothing: a generation with no documents.
        using (HttpResponseMessage response = await rig.Hub.Admin.PatchAsync("/api/v1/agents/pc1", """{"tags":[]}"""))
        {
            _ = await HubAssert.JsonAsync(response);
        }

        JsonElement empty = await agent.NextDesiredAsync();
        Assert.Equal(2, empty.GetProperty("generation").GetInt64());
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_tag_that_changes_while_the_agent_is_offline_takes_effect_when_it_connects()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "tag:office");

        // The registry changes the tags without anybody calling the roll-out service, as it does for a token that carries tags.
        _ = await rig.Hub.Service<QoffeeAutomat.Hub.Agents.AgentRegistry>().SetTagsAsync("pc1", ["office"], CancellationToken.None);
        Assert.Equal(0, await rig.GenerationOfAsync("pc1"));

        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");

        Assert.Equal(1, (await agent.ApplyNextAsync()).GetProperty("generation").GetInt64());
    }

    // ------------------------------------------------------------------
    // Frames
    // ------------------------------------------------------------------

    [Fact]
    public async Task Documents_are_split_over_frames_below_the_frame_limit()
    {
        const int Limit = 4096;
        await using RolloutRig rig = await RolloutRig.StartAsync(RolloutRig.Options(maxFrameBytes: Limit));
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        string code = new('a', 1500);
        await rig.SaveCommandAsync("big_a", code);
        await rig.SaveCommandAsync("big_b", code);
        await rig.SaveCommandAsync("big_c", code);
        await rig.CreateProfileAsync("office", ["big_a", "big_b", "big_c"]);
        _ = await rig.PublishAsync("office");
        await rig.AssignAsync("office", "all");

        JsonElement desired = await agent.NextDesiredAsync();
        agent.AnswerNeed(desired, ("command", "big_a"), ("command", "big_b"), ("command", "big_c"));

        var ids = new List<string>();
        var finals = new List<bool>();
        bool final;
        do
        {
            JsonElement frame = await agent.NextDocumentsAsync();
            Assert.True(frame.GetRawText().Length <= Limit, "A frame is larger than the limit.");
            ids.AddRange(frame.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("id").GetString()!));
            final = frame.GetProperty("final").GetBoolean();
            finals.Add(final);
            agent.AnswerOk(frame);
        }
        while (!final);

        Assert.Equal(["big_a", "big_b", "big_c"], ids.ToArray());
        Assert.True(finals.Count >= 2, "The documents do not fit into one frame.");
        Assert.Equal(1, finals.Count(isFinal => isFinal));
        Assert.True(finals[^1]);

        agent.ReportStatus(1, ConfigStates.Applied, Applied("big_a"), Applied("big_b"), Applied("big_c"));
        _ = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate && info.DeliveryError is null, "the generation to be applied");
    }

    // ------------------------------------------------------------------
    // Removal and the API
    // ------------------------------------------------------------------

    [Fact]
    public async Task Removing_an_agent_removes_its_roll_out_record()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync();
        await rig.EnrollAsync("pc1");
        ScriptedConfigAgent agent = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");
        await rig.PublishCommandProfileAsync("office", "hello", "all");
        _ = await agent.ApplyNextAsync();
        _ = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate, "the agent to be up to date");
        Assert.Equal(1, RecordCount(rig, "pc1"));

        using (HttpResponseMessage response = await rig.Hub.Admin.DeleteAsync("/api/v1/agents/pc1"))
        {
            HubAssert.Status(response, HttpStatusCode.NoContent);
        }

        await TestWait.UntilAsync(() => RecordCount(rig, "pc1") == 0, "the record of the agent to be removed");
        await ProblemAsync(rig.Hub.Admin, "/api/v1/agents/pc1/config-status", HttpStatusCode.NotFound, "unknown_agent");
    }

    [Fact]
    public async Task The_rollout_endpoints_report_the_status_per_agent_and_per_profile_revision()
    {
        await using RolloutRig rig = await RolloutRig.StartAsync(withUsers: true);
        await rig.EnrollAsync("pc1");
        await rig.EnrollAsync("pc2");
        ScriptedConfigAgent pc1 = await ScriptedConfigAgent.ConnectAsync(rig, "pc1");

        // Nothing is assigned yet: no agent has a desired state, one is offline.
        JsonNode before = await GetAsync(rig.Hub.Viewer, "/api/v1/rollouts");
        Assert.Equal(2, before["counts"]!["total"]!.GetValue<int>());
        Assert.Equal(2, before["counts"]!["noDesiredState"]!.GetValue<int>());
        Assert.Equal(1, before["counts"]!["offline"]!.GetValue<int>());

        await rig.PublishCommandProfileAsync("office", "hello", "all");
        _ = await pc1.ApplyNextAsync();
        _ = await rig.WaitForAsync("pc1", info => info.State == RolloutStates.UpToDate, "pc1 to be up to date");

        JsonNode overview = await GetAsync(rig.Hub.Viewer, "/api/v1/rollouts");
        Assert.Equal(1, overview["counts"]!["upToDate"]!.GetValue<int>());
        Assert.Equal(1, overview["counts"]!["pending"]!.GetValue<int>());
        Assert.Equal(1, overview["counts"]!["offline"]!.GetValue<int>());

        JsonNode online = await GetAsync(rig.Hub.Viewer, "/api/v1/agents/pc1/config-status");
        Assert.Equal("up_to_date", online["state"]!.GetValue<string>());
        Assert.True(online["online"]!.GetValue<bool>());
        Assert.Equal(1, online["desiredGeneration"]!.GetValue<int>());
        Assert.Equal(1, online["appliedGeneration"]!.GetValue<int>());
        Assert.Equal("applied", online["documents"]![0]!["state"]!.GetValue<string>());

        JsonNode offline = await GetAsync(rig.Hub.Viewer, "/api/v1/agents/pc2/config-status");
        Assert.Equal("pending", offline["state"]!.GetValue<string>());
        Assert.False(offline["online"]!.GetValue<bool>());
        Assert.Equal(0, offline["appliedGeneration"]!.GetValue<int>());

        JsonNode revision = await GetAsync(rig.Hub.Viewer, "/api/v1/profiles/office/rollout?revision=1");
        Assert.Equal(2, revision["agents"]!.AsArray().Count);
        Assert.Equal(1, revision["counts"]!["upToDate"]!.GetValue<int>());
        Assert.Equal(1, revision["counts"]!["pending"]!.GetValue<int>());

        await ProblemAsync(rig.Hub.Viewer, "/api/v1/agents/nobody/config-status", HttpStatusCode.NotFound, "unknown_agent");
        await ProblemAsync(rig.Hub.Viewer, "/api/v1/profiles/nobody/rollout", HttpStatusCode.NotFound, "unknown_profile");
        await ProblemAsync(rig.Hub.Viewer, "/api/v1/profiles/office/rollout?revision=9", HttpStatusCode.NotFound, "unknown_revision");
        await ProblemAsync(rig.Hub.Viewer, "/api/v1/profiles/office/rollout?revision=abc", (HttpStatusCode)422, "validation_failed");

        // A profile that was never published has no roll-out.
        await rig.CreateProfileAsync("draft_only", ["hello"]);
        await ProblemAsync(rig.Hub.Viewer, "/api/v1/profiles/draft_only/rollout", HttpStatusCode.NotFound, "not_published");

        // A viewer may look but not push.
        using HttpResponseMessage denied = await rig.Hub.Viewer.PostAsync("/api/v1/agents/pc1/config-push");
        _ = await HubAssert.ProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
    }

    private static long RecordCount(RolloutRig rig, string agentId)
    {
        object? count = rig.Hub.Service<HubDatabase>().Query(connection =>
            HubSql.Scalar(connection, null, "SELECT COUNT(*) FROM agent_config_status WHERE agent_id = @id;", ("@id", agentId)));
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Splitting documents into frames
    // ------------------------------------------------------------------

    private static HubDocument Script(string id, int size) => DocumentHasher.CreateDocument(DocumentKinds.Script, id, Docs.Script(new string('x', size)));

    private static int FrameSize(long generation, IReadOnlyList<HubDocument> documents)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new ConfigDocumentsMessage(long.MaxValue, generation, documents, false), HubJsonContext.Default.ConfigDocumentsMessage).Length;
    }

    [Fact]
    public void Documents_that_fit_are_sent_in_one_frame()
    {
        HubDocument[] documents = [Script("a", 100), Script("b", 100), Script("c", 100)];

        DocumentChunks plan = DocumentChunker.Split(7, documents, 1024 * 1024);

        Assert.Null(plan.TooLarge);
        IReadOnlyList<HubDocument> frame = Assert.Single(plan.Chunks);
        Assert.Equal(["a", "b", "c"], frame.Select(document => document.Id).ToArray());
    }

    [Fact]
    public void Documents_are_packed_in_order_into_frames_that_stay_below_the_limit()
    {
        const int Limit = 4096;
        HubDocument[] documents = [.. Enumerable.Range(0, 10).Select(index => Script("doc" + index.ToString(CultureInfo.InvariantCulture), 1000))];

        DocumentChunks plan = DocumentChunker.Split(7, documents, Limit);

        Assert.Null(plan.TooLarge);
        Assert.True(plan.Chunks.Count > 1);
        foreach (IReadOnlyList<HubDocument> chunk in plan.Chunks)
        {
            Assert.NotEmpty(chunk);
            Assert.True(FrameSize(7, chunk) <= Limit - DocumentChunker.Slack, "A frame is larger than the limit.");
        }

        Assert.Equal(documents.Select(document => document.Id).ToArray(), plan.Chunks.SelectMany(chunk => chunk).Select(document => document.Id).ToArray());
    }

    [Fact]
    public void A_document_that_does_not_fit_into_a_frame_of_its_own_is_reported()
    {
        HubDocument[] documents = [Script("small", 100), Script("huge", 5000)];

        DocumentChunks plan = DocumentChunker.Split(7, documents, 4096);

        Assert.Empty(plan.Chunks);
        Assert.Equal("huge", plan.TooLarge?.Id);
    }

    [Fact]
    public void No_documents_need_no_frame()
    {
        DocumentChunks plan = DocumentChunker.Split(7, [], 4096);

        Assert.Null(plan.TooLarge);
        Assert.Empty(plan.Chunks);
    }
}

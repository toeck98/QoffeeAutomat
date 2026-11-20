using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Config;

namespace QoffeeAutomat.Hub.Tests.Config;

/// <summary>
/// The pure function that computes the desired state of an agent (S20, D-030, T907): precedence by target specificity and priority, the OS
/// filter, the merge of variables and policy, and the warnings about overrides. Nothing here touches a database.
/// </summary>
[Trait("Category", "Config")]
public sealed class DesiredStateComputerTests
{
    private static readonly ConfigAgent Pc = Docs.Agent("pc1", "windows", AgentStatuses.Enrolled, "office", "lab");

    private static readonly ProfilePolicy Inherit = new(DesiredStateComputer.Inherit, DesiredStateComputer.Inherit);

    /// <summary>
    /// Every combination of the targets all, tag and agent, with equal and different priorities, and profiles on the same kind of target. A row is
    /// the profiles as <c>id=target@priority</c> (a profile that appears twice has two assignments), the profile whose document must win, and the
    /// number of profiles that contribute.
    /// </summary>
    public static IEnumerable<object[]> PrecedenceCases()
    {
        // One target.
        yield return Row("p_all=all@0", "p_all", 1);
        yield return Row("p_tag=tag:office@0", "p_tag", 1);
        yield return Row("p_agent=agent:pc1@0", "p_agent", 1);

        // all and tag: the tag is more specific whatever the priorities are.
        yield return Row("p_all=all@5 p_tag=tag:office@5", "p_tag", 2);
        yield return Row("p_all=all@9 p_tag=tag:office@1", "p_tag", 2);
        yield return Row("p_all=all@1 p_tag=tag:office@9", "p_tag", 2);

        // all and agent.
        yield return Row("p_all=all@5 p_agent=agent:pc1@5", "p_agent", 2);
        yield return Row("p_all=all@9 p_agent=agent:pc1@1", "p_agent", 2);
        yield return Row("p_all=all@1 p_agent=agent:pc1@9", "p_agent", 2);

        // tag and agent.
        yield return Row("p_tag=tag:office@5 p_agent=agent:pc1@5", "p_agent", 2);
        yield return Row("p_tag=tag:office@9 p_agent=agent:pc1@1", "p_agent", 2);
        yield return Row("p_tag=tag:office@1 p_agent=agent:pc1@9", "p_agent", 2);

        // all, tag and agent.
        yield return Row("p_all=all@5 p_tag=tag:office@5 p_agent=agent:pc1@5", "p_agent", 3);
        yield return Row("p_all=all@9 p_tag=tag:office@5 p_agent=agent:pc1@1", "p_agent", 3);
        yield return Row("p_all=all@1 p_tag=tag:office@5 p_agent=agent:pc1@9", "p_agent", 3);
        yield return Row("p_all=all@1 p_tag=tag:office@9 p_agent=agent:pc1@1", "p_agent", 3);
        yield return Row("p_all=all@9 p_tag=tag:office@1 p_agent=agent:pc1@1", "p_agent", 3);

        // Equally specific targets: the higher priority wins; with equal priority the profile id decides, whatever the order of the input.
        yield return Row("p_a=tag:office@3 p_b=tag:lab@3", "p_b", 2);
        yield return Row("p_b=tag:lab@3 p_a=tag:office@3", "p_b", 2);
        yield return Row("p_a=tag:office@7 p_b=tag:lab@3", "p_a", 2);
        yield return Row("p_a=tag:office@3 p_b=tag:lab@7", "p_b", 2);
        yield return Row("p_a=all@5 p_b=all@5", "p_b", 2);
        yield return Row("p_a=all@-1 p_b=all@-5", "p_a", 2);
        yield return Row("p_a=all@2 p_b=all@9", "p_b", 2);
        yield return Row("p_a=agent:pc1@2 p_b=agent:pc1@2", "p_b", 2);
        yield return Row("p_a=agent:pc1@8 p_b=agent:pc1@2", "p_a", 2);
        yield return Row("p_a=tag:office@1 p_b=tag:lab@1 p_c=all@100", "p_b", 3);

        // Targets that do not address the agent are ignored.
        yield return Row("p_all=all@0 p_other=tag:nope@99 p_x=agent:pc9@99", "p_all", 1);
        yield return Row("p_other=tag:nope@99", string.Empty, 0);

        // A profile that is reached by several targets counts with its most specific one.
        yield return Row("p_multi=all@0 p_multi=tag:office@0 p_other=tag:lab@5", "p_other", 2);
        yield return Row("p_multi=all@0 p_multi=agent:pc1@0 p_other=tag:lab@9", "p_multi", 2);
    }

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public void The_document_of_the_most_specific_target_wins_then_the_higher_priority_then_the_profile_id(string specs, string expectedWinner, int contributing)
    {
        (List<ProfileSnapshot> profiles, List<ProfileAssignment> assignments) = Parse(specs);

        ComputedDesiredState state = DesiredStateComputer.Compute(Pc, profiles, assignments);

        Assert.Equal(contributing, state.Profiles.Count);
        if (expectedWinner.Length == 0)
        {
            Assert.Empty(state.Documents);
            Assert.Empty(state.Items);
            return;
        }

        DesiredDocument winner = Assert.Single(state.Documents);
        Assert.Equal("shared", winner.Id);
        Assert.Equal(expectedWinner, winner.ProfileId);
        Assert.Equal(expectedWinner, state.Profiles[^1]);

        // Every profile that loses to a later one is one warning: n profiles with the same document give n - 1 overrides.
        ConfigWarning[] overrides = [.. state.Warnings.Where(warning => warning.Kind == "command")];
        Assert.Equal(contributing - 1, overrides.Length);
        if (overrides.Length > 0)
        {
            Assert.Equal(expectedWinner, overrides[^1].Profile);
        }
    }

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public void The_result_does_not_depend_on_the_order_of_the_input(string specs, string expectedWinner, int contributing)
    {
        _ = expectedWinner;
        _ = contributing;
        (List<ProfileSnapshot> profiles, List<ProfileAssignment> assignments) = Parse(specs);
        ComputedDesiredState forward = DesiredStateComputer.Compute(Pc, profiles, assignments);

        profiles.Reverse();
        assignments.Reverse();
        ComputedDesiredState backward = DesiredStateComputer.Compute(Pc, profiles, assignments);

        Assert.Equal(forward.ContentHash(Pc.Id), backward.ContentHash(Pc.Id));
        Assert.Equal(forward.Profiles, backward.Profiles);
    }

    [Fact]
    public void An_override_between_profiles_of_equal_specificity_and_priority_is_reported_as_a_tie_and_any_other_override_as_an_override()
    {
        (ComputedDesiredState tie, ComputedDesiredState priority, ComputedDesiredState specificity) = (
            Compute("p_a=tag:office@3 p_b=tag:lab@3"),
            Compute("p_a=tag:office@3 p_b=tag:lab@4"),
            Compute("p_a=all@3 p_b=tag:lab@3"));

        ConfigWarning tieWarning = Assert.Single(tie.Warnings);
        Assert.Equal(ConfigWarningCodes.OverriddenTie, tieWarning.Code);
        Assert.Equal("p_b", tieWarning.Profile);
        Assert.Equal("p_a", tieWarning.OtherProfile);
        Assert.Equal(ConfigWarningCodes.Overridden, Assert.Single(priority.Warnings).Code);
        Assert.Equal(ConfigWarningCodes.Overridden, Assert.Single(specificity.Warnings).Code);
    }

    [Fact]
    public void Documents_of_different_ids_and_kinds_do_not_override_each_other()
    {
        var first = Profile("p_a", documents: [Resolved("command", "one", Docs.Command("one")), Resolved("script", "run.ps1", Docs.Script("exit 0"))]);
        var second = Profile("p_b", documents: [Resolved("command", "two", Docs.Command("two")), Resolved("sensor", "one", Docs.Sensor("one"))]);

        ComputedDesiredState state = DesiredStateComputer.Compute(
            Pc,
            [first, second],
            [new ProfileAssignment("1", "p_a", "all"), new ProfileAssignment("2", "p_b", "all")]);

        Assert.Empty(state.Warnings);
        string[] expected = ["command/one", "command/two", "script/run.ps1", "sensor/one"];
        Assert.Equal(expected, state.Items.Select(item => item.Kind + "/" + item.Id).ToArray());
    }

    [Theory]
    [InlineData(null, "windows", true)]
    [InlineData(null, "linux", true)]
    [InlineData("", "linux", true)]
    [InlineData("windows", "windows", true)]
    [InlineData("windows", "linux", false)]
    [InlineData("linux", "linux", true)]
    [InlineData("linux", "windows", false)]
    [InlineData("Windows", "windows", true)]
    public void The_OS_filter_of_a_profile_decides_whether_it_applies_to_an_agent(string? filter, string agentOs, bool applies)
    {
        var agent = Docs.Agent("pc1", agentOs);
        var profile = Profile("p_os", os: filter);

        ComputedDesiredState state = DesiredStateComputer.Compute(agent, [profile], [new ProfileAssignment("1", "p_os", "all")]);

        Assert.Equal(applies, state.Documents.Count == 1);
        Assert.Equal(applies, state.Profiles.Contains("p_os"));
    }

    [Fact]
    public void A_profile_that_the_OS_filter_excludes_does_not_override_and_causes_no_warning()
    {
        var windows = Profile("p_windows", os: "windows");
        var linux = Profile("p_linux", os: "linux", priority: 9);

        ComputedDesiredState state = DesiredStateComputer.Compute(
            Pc,
            [windows, linux],
            [new ProfileAssignment("1", "p_windows", "all"), new ProfileAssignment("2", "p_linux", "all")]);

        Assert.Equal("p_windows", Assert.Single(state.Documents).ProfileId);
        Assert.Empty(state.Warnings);
    }

    [Fact]
    public void Variables_merge_in_precedence_order_and_become_one_variables_document()
    {
        var site = Resolved("variables", "site", Docs.Variables(("D", "from-library"), ("A", "from-library")));
        var baseProfile = Profile(
            "p_base",
            documents: [site],
            variables: new Dictionary<string, string> { ["A"] = "1", ["B"] = "1" });
        var specific = Profile(
            "p_specific",
            documents: [],
            variables: new Dictionary<string, string> { ["B"] = "2", ["C"] = "3" });

        ComputedDesiredState state = DesiredStateComputer.Compute(
            Pc,
            [baseProfile, specific],
            [new ProfileAssignment("1", "p_base", "all"), new ProfileAssignment("2", "p_specific", "tag:office")]);

        DesiredDocument variables = Assert.Single(state.Documents);
        Assert.Equal(DocumentKinds.Variables, variables.Kind);
        Assert.Equal(DesiredStateComputer.VariablesId, variables.Id);
        Assert.Equal(DocumentHasher.Hash(DocumentKinds.Variables, variables.Body), variables.Hash);
        Assert.Equal("1", variables.Body.GetProperty("A").GetString());
        Assert.Equal("2", variables.Body.GetProperty("B").GetString());
        Assert.Equal("3", variables.Body.GetProperty("C").GetString());
        Assert.Equal("from-library", variables.Body.GetProperty("D").GetString());
        Assert.Equal(4, variables.Body.EnumerateObject().Count());

        // The only override between profiles is B; A is overridden inside one profile, which is no conflict.
        ConfigWarning warning = Assert.Single(state.Warnings);
        Assert.Equal(ConfigWarningCodes.VariableOverridden, warning.Code);
        Assert.Equal("B", warning.Id);
        Assert.Equal("p_specific", warning.Profile);
        Assert.Equal("p_base", warning.OtherProfile);
    }

    [Fact]
    public void Without_variables_there_is_no_variables_document()
    {
        ComputedDesiredState state = DesiredStateComputer.Compute(Pc, [Profile("p_a")], [new ProfileAssignment("1", "p_a", "all")]);

        Assert.DoesNotContain(state.Documents, document => document.Kind == DocumentKinds.Variables);
    }

    [Fact]
    public void Policy_members_merge_in_precedence_order_and_inherit_never_overrides()
    {
        var everyone = Profile("p_all", documents: [], policy: new ProfilePolicy(LocalConfigPolicies.Locked, DesiredStateComputer.Inherit));
        var group = Profile("p_group", documents: [], policy: new ProfilePolicy(DesiredStateComputer.Inherit, DirectLinkPolicies.Deny));
        var single = Profile("p_single", documents: [], policy: new ProfilePolicy(LocalConfigPolicies.Allow, DesiredStateComputer.Inherit));

        ComputedDesiredState state = DesiredStateComputer.Compute(
            Pc,
            [everyone, group, single],
            [
                new ProfileAssignment("1", "p_all", "all"),
                new ProfileAssignment("2", "p_group", "tag:lab"),
                new ProfileAssignment("3", "p_single", "agent:pc1"),
            ]);

        Assert.Equal(new PolicyInfo(LocalConfigPolicies.Allow, DirectLinkPolicies.Deny), state.Policy);
        ConfigWarning warning = Assert.Single(state.Warnings);
        Assert.Equal(ConfigWarningCodes.PolicyOverridden, warning.Code);
        Assert.Equal("local_config", warning.Id);
        Assert.Equal("p_single", warning.Profile);
        Assert.Equal("p_all", warning.OtherProfile);
    }

    [Fact]
    public void The_default_policy_allows_everything_and_equal_explicit_values_are_no_conflict()
    {
        ComputedDesiredState none = DesiredStateComputer.Compute(Pc, [], []);
        ComputedDesiredState inheriting = DesiredStateComputer.Compute(Pc, [Profile("p_a")], [new ProfileAssignment("1", "p_a", "all")]);
        var first = Profile("p_first", policy: new ProfilePolicy(LocalConfigPolicies.Locked, DirectLinkPolicies.Deny));
        var second = Profile("p_second", policy: new ProfilePolicy(LocalConfigPolicies.Locked, DirectLinkPolicies.Deny));
        ComputedDesiredState same = DesiredStateComputer.Compute(
            Pc,
            [first, second],
            [new ProfileAssignment("1", "p_first", "all"), new ProfileAssignment("2", "p_second", "all")]);

        Assert.Equal(new PolicyInfo(LocalConfigPolicies.Allow, DirectLinkPolicies.Allow), none.Policy);
        Assert.Equal(new PolicyInfo(LocalConfigPolicies.Allow, DirectLinkPolicies.Allow), inheriting.Policy);
        Assert.Equal(new PolicyInfo(LocalConfigPolicies.Locked, DirectLinkPolicies.Deny), same.Policy);
        Assert.DoesNotContain(same.Warnings, warning => warning.Code == ConfigWarningCodes.PolicyOverridden);
    }

    [Fact]
    public void Metric_overrides_of_every_library_id_are_delivered_under_the_one_id_the_agent_knows()
    {
        var baseProfile = Profile("p_base", documents: [Resolved("metric_overrides", "base", Docs.MetricOverrides(10))]);
        var specific = Profile("p_specific", documents: [Resolved("metric_overrides", "extra", Docs.MetricOverrides(30))]);

        ComputedDesiredState state = DesiredStateComputer.Compute(
            Pc,
            [baseProfile, specific],
            [new ProfileAssignment("1", "p_base", "all"), new ProfileAssignment("2", "p_specific", "agent:pc1")]);

        DesiredDocument overrides = Assert.Single(state.Documents);
        Assert.Equal(DocumentKinds.MetricOverrides, overrides.Kind);
        Assert.Equal(DesiredStateComputer.MetricOverridesId, overrides.Id);
        Assert.Equal("p_specific", overrides.ProfileId);
        Assert.Equal(DesiredStateComputer.MetricOverridesId, Assert.Single(state.Warnings).Id);
    }

    [Fact]
    public void A_command_that_runs_a_script_file_that_no_profile_provides_is_a_warning()
    {
        var command = Resolved("command", "run_it", Docs.FileCommand("run_it", "run.ps1"));
        var without = Profile("p_without", documents: [command]);
        var script = Resolved("script", "run.ps1", Docs.Script("exit 0"));
        var with = Profile("p_with", documents: [command, script]);

        ComputedDesiredState missing = DesiredStateComputer.Compute(Pc, [without], [new ProfileAssignment("1", "p_without", "all")]);
        ComputedDesiredState provided = DesiredStateComputer.Compute(Pc, [with], [new ProfileAssignment("1", "p_with", "all")]);

        ConfigWarning warning = Assert.Single(missing.Warnings);
        Assert.Equal(ConfigWarningCodes.MissingScript, warning.Code);
        Assert.Equal("run_it", warning.Id);
        Assert.Empty(provided.Warnings);
    }

    [Fact]
    public void The_content_hash_covers_policy_and_documents_but_not_the_generation()
    {
        ComputedDesiredState state = Compute("p_a=all@0");
        DesiredItem[] items = [.. state.Items];

        Assert.Equal(state.ContentHash(Pc.Id), DesiredStateHash.Of(Pc.Id, state.Policy, items));
        Assert.Equal(state.ContentHash(Pc.Id), DesiredStateHash.Of(Pc.Id, state.Policy, [.. Enumerable.Reverse(items)]));
        Assert.NotEqual(state.ContentHash(Pc.Id), DesiredStateHash.Of(Pc.Id, new PolicyInfo(LocalConfigPolicies.Locked, DirectLinkPolicies.Allow), items));
        Assert.NotEqual(state.ContentHash(Pc.Id), DesiredStateHash.Of(Pc.Id, state.Policy, [items[0] with { Hash = new string('0', 64) }]));
        Assert.NotEqual(state.ContentHash(Pc.Id), DesiredStateHash.Of("pc2", state.Policy, items));
        Assert.Equal(DesiredStateHash.Empty(Pc.Id), DesiredStateComputer.Compute(Pc, [], []).ContentHash(Pc.Id));
        Assert.NotEqual(DesiredStateHash.Empty(Pc.Id), state.ContentHash(Pc.Id));
    }

    [Fact]
    public void Items_are_sorted_by_kind_and_id_in_ordinal_order_as_they_are_signed()
    {
        var profile = Profile(
            "p_a",
            documents:
            [
                Resolved("sensor", "b", Docs.Sensor("b")),
                Resolved("command", "z", Docs.Command("z")),
                Resolved("command", "a", Docs.Command("a")),
                Resolved("script", "B.ps1", Docs.Script("1")),
                Resolved("script", "a.ps1", Docs.Script("2")),
            ]);

        ComputedDesiredState state = DesiredStateComputer.Compute(Pc, [profile], [new ProfileAssignment("1", "p_a", "all")]);

        string[] expected = ["command/a", "command/z", "script/B.ps1", "script/a.ps1", "sensor/b"];
        Assert.Equal(expected, state.Items.Select(item => item.Kind + "/" + item.Id).ToArray());
        Assert.Equal(state.Items.ToArray(), DesiredState.SortItems(state.Items).ToArray());
    }

    [Theory]
    [InlineData("all", true, "all", null)]
    [InlineData("tag:office", true, "tag", "office")]
    [InlineData("tag:Office", true, "tag", "office")]
    [InlineData("agent:pc1", true, "agent", "pc1")]
    [InlineData("tag:", false, null, null)]
    [InlineData("tag:-bad", false, null, null)]
    [InlineData("agent:", false, null, null)]
    [InlineData("agent:a b", false, null, null)]
    [InlineData("group:x", false, null, null)]
    [InlineData("ALL", false, null, null)]
    [InlineData("", false, null, null)]
    public void Assignment_targets_are_all_tag_or_agent(string text, bool valid, string? kind, string? value)
    {
        bool parsed = AssignmentTarget.TryParse(text, out AssignmentTarget? target, out string? error);

        Assert.Equal(valid, parsed);
        if (!valid)
        {
            Assert.Null(target);
            Assert.False(string.IsNullOrEmpty(error));
            return;
        }

        Assert.Equal(kind, target!.Kind.ToString().ToLowerInvariant());
        Assert.Equal(value, target.Value);
        Assert.Equal(text.ToLowerInvariant(), target.Text);
    }

    [Fact]
    public void Specificity_ranks_all_below_tag_below_agent()
    {
        Assert.True(new AssignmentTarget(TargetKind.All, null).Specificity < new AssignmentTarget(TargetKind.Tag, "x").Specificity);
        Assert.True(new AssignmentTarget(TargetKind.Tag, "x").Specificity < new AssignmentTarget(TargetKind.Agent, "x").Specificity);
    }

    private static object[] Row(string specs, string winner, int contributing) => [specs, winner, contributing];

    private static ComputedDesiredState Compute(string specs)
    {
        (List<ProfileSnapshot> profiles, List<ProfileAssignment> assignments) = Parse(specs);
        return DesiredStateComputer.Compute(Pc, profiles, assignments);
    }

    /// <summary>Reads <c>id=target@priority</c> items separated by blanks into profiles (each with a command <c>shared</c> that names its profile) and assignments.</summary>
    private static (List<ProfileSnapshot> Profiles, List<ProfileAssignment> Assignments) Parse(string specs)
    {
        var profiles = new Dictionary<string, ProfileSnapshot>(StringComparer.Ordinal);
        var assignments = new List<ProfileAssignment>();
        foreach (string spec in specs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] sides = spec.Split('=');
            string[] targetAndPriority = sides[1].Split('@');
            int priority = int.Parse(targetAndPriority[1], CultureInfo.InvariantCulture);
            if (!profiles.ContainsKey(sides[0]))
            {
                profiles[sides[0]] = Profile(sides[0], priority);
            }

            assignments.Add(new ProfileAssignment("a" + assignments.Count.ToString(CultureInfo.InvariantCulture), sides[0], targetAndPriority[0]));
        }

        return ([.. profiles.Values], assignments);
    }

    private static ResolvedDocument Resolved(string kind, string id, JsonElement body)
    {
        return new ResolvedDocument(kind, id, 1, DocumentHasher.Hash(kind, body), body);
    }

    private static ProfileSnapshot Profile(
        string id,
        int priority = 0,
        string? os = null,
        IReadOnlyList<ResolvedDocument>? documents = null,
        Dictionary<string, string>? variables = null,
        ProfilePolicy? policy = null)
    {
        JsonElement shared = ConfigJson.Parse($$"""{ "id": "shared", "name": "{{id}}" }""");
        return new ProfileSnapshot(
            id,
            1,
            id,
            priority,
            os,
            documents ?? [Resolved("command", "shared", shared)],
            variables ?? new Dictionary<string, string>(),
            policy ?? Inherit);
    }
}

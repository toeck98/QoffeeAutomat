using System.Security.Cryptography;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Config;

/// <summary>A library document of a profile, resolved at its pinned revision.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The library id.</param>
/// <param name="Revision">The pinned library revision.</param>
/// <param name="Hash">The hash of the body.</param>
/// <param name="Body">The body.</param>
public sealed record ResolvedDocument(string Kind, string Id, int Revision, string Hash, JsonElement Body);

/// <summary>A published revision of a profile with its documents resolved: the input of the computation.</summary>
/// <param name="ProfileId">The profile id.</param>
/// <param name="Revision">The published revision.</param>
/// <param name="Name">The display name.</param>
/// <param name="Priority">The priority; the higher number wins between equally specific targets.</param>
/// <param name="OsFilter"><c>windows</c> or <c>linux</c>, or null for every agent.</param>
/// <param name="Documents">The documents, sorted by kind and id.</param>
/// <param name="Variables">The variables of the profile.</param>
/// <param name="Policy">The policy of the profile.</param>
public sealed record ProfileSnapshot(
    string ProfileId,
    int Revision,
    string Name,
    int Priority,
    string? OsFilter,
    IReadOnlyList<ResolvedDocument> Documents,
    IReadOnlyDictionary<string, string> Variables,
    ProfilePolicy Policy);

/// <summary>An assignment of a profile to a target.</summary>
/// <param name="Id">The assignment id.</param>
/// <param name="ProfileId">The profile.</param>
/// <param name="Target">The text of the target.</param>
public sealed record ProfileAssignment(string Id, string ProfileId, string Target);

/// <summary>A document of a desired state with the profile it comes from.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Id">The id the agent sees.</param>
/// <param name="Hash">The hash of the body.</param>
/// <param name="Body">The body.</param>
/// <param name="ProfileId">The profile it comes from.</param>
/// <param name="ProfileRevision">The revision of that profile.</param>
public sealed record DesiredDocument(string Kind, string Id, string Hash, JsonElement Body, string ProfileId, int ProfileRevision);

/// <summary>The codes of <see cref="ConfigWarning.Code"/>.</summary>
public static class ConfigWarningCodes
{
    /// <summary>A profile replaced a document of an earlier one.</summary>
    public const string Overridden = "overridden";

    /// <summary>A profile replaced a document of another one that is equally specific and has the same priority; the profile id decided.</summary>
    public const string OverriddenTie = "overridden_tie";

    /// <summary>A profile replaced the value of a variable.</summary>
    public const string VariableOverridden = "variable_overridden";

    /// <summary>A profile replaced an explicit policy value of an earlier one.</summary>
    public const string PolicyOverridden = "policy_overridden";

    /// <summary>A command uses a script file that no profile of the agent provides.</summary>
    public const string MissingScript = "missing_script";
}

/// <summary>An override or a problem found while computing a desired state.</summary>
/// <param name="Code">One of <see cref="ConfigWarningCodes"/>.</param>
/// <param name="Kind">The document kind, <c>variable</c> or <c>policy</c>.</param>
/// <param name="Id">The document id, the variable name or the policy member.</param>
/// <param name="Message">What happens.</param>
/// <param name="Profile">The profile that wins or that has the problem.</param>
/// <param name="OtherProfile">The profile that loses.</param>
public sealed record ConfigWarning(string Code, string Kind, string Id, string Message, string Profile, string? OtherProfile);

/// <summary>The desired state of one agent as computed from the published profiles.</summary>
/// <param name="Documents">The documents, sorted by kind and id.</param>
/// <param name="Items">The items of the signed list, sorted by kind and id in ordinal order.</param>
/// <param name="Policy">The merged policy.</param>
/// <param name="Warnings">The overrides and problems.</param>
/// <param name="Profiles">The profiles that contribute, in the order in which they are applied.</param>
public sealed record ComputedDesiredState(
    IReadOnlyList<DesiredDocument> Documents,
    IReadOnlyList<DesiredItem> Items,
    PolicyInfo Policy,
    IReadOnlyList<ConfigWarning> Warnings,
    IReadOnlyList<string> Profiles)
{
    /// <summary>The hash of the content: policy and items, independent of the generation.</summary>
    /// <param name="agentId">The agent the state is for.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public string ContentHash(string agentId) => DesiredStateHash.Of(agentId, Policy, Items);
}

/// <summary>The hash that decides whether a desired state changed (S20: a new generation only when the content changes).</summary>
public static class DesiredStateHash
{
    /// <summary>Hashes the canonical JSON of the signed content with the generation set to 0, so equal content has an equal hash for every generation.</summary>
    /// <param name="agentId">The agent the state is for.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="items">The items; they are sorted before hashing.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public static string Of(string agentId, PolicyInfo policy, IReadOnlyList<DesiredItem> items)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(items);

        byte[] payload = DesiredStateSigner.CanonicalPayload(new DesiredState(agentId, 0, policy, DesiredState.SortItems(items)));
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    /// <summary>The hash of the state without documents and with the default policy: what an agent has that no profile reaches.</summary>
    /// <param name="agentId">The agent the state is for.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public static string Empty(string agentId) => Of(agentId, DesiredStateComputer.DefaultPolicy, []);
}

/// <summary>
/// The desired state of an agent as a pure function (PLAN.md S20, D-030): take the published revisions of the profiles that are assigned to a
/// matching target and pass the OS filter; order them by the specificity of the target (<c>all</c>, then <c>tag</c>, then <c>agent</c>), then by
/// priority (the higher number later) and, when even that is equal, by profile id so that the result is deterministic; later entries override
/// earlier ones on equal kind and id; variables and policy merge the same way. A profile that is reached by several targets counts with its most
/// specific one. The merge reports every override as a warning. Nothing is read or written here: the same input gives the same output.
/// </summary>
public static class DesiredStateComputer
{
    /// <summary>The id of the one variables document of a desired state.</summary>
    public const string VariablesId = "variables";

    /// <summary>The id of the metric override document on the agent, which exists once; every library document of that kind is delivered under it.</summary>
    public const string MetricOverridesId = "metric_overrides";

    /// <summary>The value of a policy member that leaves the decision to the profiles before it.</summary>
    public const string Inherit = "inherit";

    /// <summary>The policy of an agent that no profile gives an opinion about (D-029): everything allowed.</summary>
    public static PolicyInfo DefaultPolicy { get; } = new(LocalConfigPolicies.Allow, DirectLinkPolicies.Allow);

    /// <summary>Computes the desired state of an agent.</summary>
    /// <param name="agent">The agent: its id, operating system and tags.</param>
    /// <param name="profiles">The published revisions of all profiles that have one.</param>
    /// <param name="assignments">All assignments.</param>
    /// <returns>The desired state.</returns>
    public static ComputedDesiredState Compute(
        ConfigAgent agent,
        IEnumerable<ProfileSnapshot> profiles,
        IEnumerable<ProfileAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(assignments);

        Dictionary<string, int> specificity = SpecificityByProfile(agent, assignments);
        var ordered = profiles
            .Where(profile => specificity.ContainsKey(profile.ProfileId) && PassesOsFilter(profile.OsFilter, agent.Os))
            .Select(profile => (Profile: profile, Specificity: specificity[profile.ProfileId]))
            .OrderBy(entry => entry.Specificity)
            .ThenBy(entry => entry.Profile.Priority)
            .ThenBy(entry => entry.Profile.ProfileId, StringComparer.Ordinal)
            .ToList();

        var documents = new Dictionary<(string Kind, string Id), (DesiredDocument Document, int Specificity, int Priority)>();
        var variables = new Dictionary<string, (string Value, string Profile)>(StringComparer.Ordinal);
        var warnings = new List<ConfigWarning>();
        string localConfig = LocalConfigPolicies.Allow;
        string? localConfigSource = null;
        string directLink = DirectLinkPolicies.Allow;
        string? directLinkSource = null;

        foreach ((ProfileSnapshot profile, int rank) in ordered)
        {
            foreach (ResolvedDocument resolved in profile.Documents)
            {
                if (resolved.Kind == DocumentKinds.Variables)
                {
                    continue;
                }

                string id = resolved.Kind == DocumentKinds.MetricOverrides ? MetricOverridesId : resolved.Id;
                var candidate = new DesiredDocument(resolved.Kind, id, resolved.Hash, resolved.Body, profile.ProfileId, profile.Revision);
                if (documents.TryGetValue((resolved.Kind, id), out var previous))
                {
                    bool tie = previous.Specificity == rank && previous.Priority == profile.Priority;
                    warnings.Add(new ConfigWarning(
                        tie ? ConfigWarningCodes.OverriddenTie : ConfigWarningCodes.Overridden,
                        resolved.Kind,
                        id,
                        $"Profile '{profile.ProfileId}' overrides the {resolved.Kind} '{id}' of profile '{previous.Document.ProfileId}'"
                            + (tie ? " (equal specificity and priority: the profile id decides)." : "."),
                        profile.ProfileId,
                        previous.Document.ProfileId));
                }

                documents[(resolved.Kind, id)] = (candidate, rank, profile.Priority);
            }

            foreach (KeyValuePair<string, string> variable in EffectiveVariables(profile))
            {
                if (variables.TryGetValue(variable.Key, out var earlier) && !string.Equals(earlier.Value, variable.Value, StringComparison.Ordinal))
                {
                    warnings.Add(new ConfigWarning(
                        ConfigWarningCodes.VariableOverridden,
                        "variable",
                        variable.Key,
                        $"Profile '{profile.ProfileId}' overrides the variable '{variable.Key}' of profile '{earlier.Profile}'.",
                        profile.ProfileId,
                        earlier.Profile));
                }

                variables[variable.Key] = (variable.Value, profile.ProfileId);
            }

            if (!string.Equals(profile.Policy.LocalConfig, Inherit, StringComparison.Ordinal))
            {
                if (localConfigSource is not null && !string.Equals(localConfig, profile.Policy.LocalConfig, StringComparison.Ordinal))
                {
                    warnings.Add(PolicyWarning("local_config", profile.ProfileId, localConfigSource));
                }

                localConfig = profile.Policy.LocalConfig;
                localConfigSource = profile.ProfileId;
            }

            if (!string.Equals(profile.Policy.DirectLink, Inherit, StringComparison.Ordinal))
            {
                if (directLinkSource is not null && !string.Equals(directLink, profile.Policy.DirectLink, StringComparison.Ordinal))
                {
                    warnings.Add(PolicyWarning("direct_link", profile.ProfileId, directLinkSource));
                }

                directLink = profile.Policy.DirectLink;
                directLinkSource = profile.ProfileId;
            }
        }

        var result = new List<DesiredDocument>(documents.Values.Select(static entry => entry.Document));
        if (variables.Count > 0)
        {
            JsonElement body = ConfigJson.StringObject(variables.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value.Value)));
            string source = ordered.Last(entry => entry.Profile.Variables.Count > 0 || entry.Profile.Documents.Any(static document => document.Kind == DocumentKinds.Variables)).Profile.ProfileId;
            int sourceRevision = ordered.First(entry => entry.Profile.ProfileId == source).Profile.Revision;
            result.Add(new DesiredDocument(DocumentKinds.Variables, VariablesId, DocumentHasher.Hash(DocumentKinds.Variables, body), body, source, sourceRevision));
        }

        warnings.AddRange(MissingScripts(result));
        result.Sort(static (left, right) =>
        {
            int byKind = string.CompareOrdinal(left.Kind, right.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(left.Id, right.Id);
        });

        DesiredItem[] items = [.. result.Select(static document => new DesiredItem(document.Kind, document.Id, document.Hash))];
        return new ComputedDesiredState(
            result,
            DesiredState.SortItems(items),
            new PolicyInfo(localConfig, directLink),
            warnings,
            [.. ordered.Select(static entry => entry.Profile.ProfileId)]);
    }

    /// <summary>Tells whether a profile applies to an agent of an operating system.</summary>
    /// <param name="filter"><c>windows</c> or <c>linux</c>; null or empty for every agent.</param>
    /// <param name="os">The operating system family of the agent.</param>
    /// <returns>True when the profile applies.</returns>
    public static bool PassesOsFilter(string? filter, string os)
    {
        return string.IsNullOrEmpty(filter) || string.Equals(filter, os, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> SpecificityByProfile(ConfigAgent agent, IEnumerable<ProfileAssignment> assignments)
    {
        var specificity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ProfileAssignment assignment in assignments)
        {
            if (AssignmentTarget.TryParse(assignment.Target, out AssignmentTarget? target, out _)
                && target.Matches(agent)
                && (!specificity.TryGetValue(assignment.ProfileId, out int known) || target.Specificity > known))
            {
                specificity[assignment.ProfileId] = target.Specificity;
            }
        }

        return specificity;
    }

    private static ConfigWarning PolicyWarning(string member, string winner, string loser)
    {
        return new ConfigWarning(
            ConfigWarningCodes.PolicyOverridden,
            "policy",
            member,
            $"Profile '{winner}' overrides the policy {member} of profile '{loser}'.",
            winner,
            loser);
    }

    /// <summary>The variables of a profile: those of its library variables documents (by id), then its own, which win.</summary>
    private static Dictionary<string, string> EffectiveVariables(ProfileSnapshot profile)
    {
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ResolvedDocument document in profile.Documents
            .Where(static document => document.Kind == DocumentKinds.Variables)
            .OrderBy(static document => document.Id, StringComparer.Ordinal))
        {
            if (document.Body.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty property in document.Body.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    effective[property.Name] = property.Value.GetString()!;
                }
            }
        }

        foreach (KeyValuePair<string, string> own in profile.Variables)
        {
            effective[own.Key] = own.Value;
        }

        return effective;
    }

    private static List<ConfigWarning> MissingScripts(List<DesiredDocument> documents)
    {
        var scripts = new HashSet<string>(
            documents.Where(static document => document.Kind == DocumentKinds.Script).Select(static document => document.Id),
            StringComparer.Ordinal);
        var warnings = new List<ConfigWarning>();
        foreach (DesiredDocument document in documents.Where(static document => document.Kind == DocumentKinds.Command))
        {
            if (SystemScripts.ScriptFileOf(document.Body) is { } script && !scripts.Contains(script))
            {
                warnings.Add(new ConfigWarning(
                    ConfigWarningCodes.MissingScript,
                    DocumentKinds.Command,
                    document.Id,
                    $"The command '{document.Id}' runs the script file '{script}', which no profile of this agent provides.",
                    document.ProfileId,
                    null));
            }
        }

        return warnings;
    }
}
